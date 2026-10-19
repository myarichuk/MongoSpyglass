using MongoDB.Bson;
using MongoDB.Driver;
using Spyglass.Proxy.Config;
using Spyglass.Proxy.Tests.Infrastructure;

namespace Spyglass.Proxy.Tests;

public sealed class TlsReplicaSetFixture : IAsyncLifetime
{
    public Deployment Mongo { get; private set; } = null!;
    public TestCertificates Certs { get; private set; } = null!;
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "spyglass-tests", "tls-" + Guid.NewGuid().ToString("N")[..8]);

    public async ValueTask InitializeAsync()
    {
        MongodBinaries.SkipIfMissing();
        Certs = TestCertificates.Create(Dir);
        Mongo = await Deployment.StartReplicaSetAsync(3, Certs);
    }

    public async ValueTask DisposeAsync()
    {
        if (Mongo != null) await Mongo.DisposeAsync();
        try { Directory.Delete(Dir, true); } catch (IOException) { }
    }
}

/// <summary>06 §2.4 TLS against a 3-member TLS replica set.</summary>
public sealed class TlsTests(TlsReplicaSetFixture f) : IClassFixture<TlsReplicaSetFixture>
{
    [Fact]
    public async Task Upstream_tls_with_test_ca()
    {
        await using var proxy = await ProxyHost.StartAsync(f.Mongo.DirectUri, configure: a => a["upstream.tls.caFile"] = f.Certs.CaFile);
        Assert.True(proxy.Server.Upstream.UsesTls);
        var (client, _, _) = ReplicaSetTests.Client(proxy.Uri("rs0"));
        var coll = client.GetDatabase("tls").GetCollection<BsonDocument>("c");
        await coll.InsertOneAsync(new BsonDocument("x", 1));
        Assert.Equal(1, await coll.CountDocumentsAsync("{}"));
        await ReplicaSetTests.WaitUntil(() => client.Cluster.Description.Servers.Count == 3, TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task Wrong_ca_fails_closed()
    {
        var other = TestCertificates.Create(Path.Combine(f.Dir, "other"), "Some Other CA");
        var e = await Assert.ThrowsAsync<ConfigException>(() =>
            ProxyHost.StartAsync(f.Mongo.DirectUri, configure: a => a["upstream.tls.caFile"] = other.CaFile));
        Assert.Contains("no upstream seed answered", e.Message);
    }

    [Fact]
    public async Task Downstream_tls_self_signed_with_tlsCAFile()
    {
        await using var proxy = await ProxyHost.StartAsync(f.Mongo.DirectUri, configure: a =>
        {
            a["upstream.tls.caFile"] = f.Certs.CaFile;
            a["listen.tls.mode"] = "tls";
        });
        var pem = Path.Combine(f.Dir, $"proxy-{proxy.BasePort}.pem");
        await File.WriteAllTextAsync(pem, proxy.Server.DownstreamTls!.CertificatePem);

        var client = TestClients.Create(proxy.Uri("rs0", "tls=true"), pem);
        var coll = client.GetDatabase("tls").GetCollection<BsonDocument>("down");
        await coll.InsertOneAsync(new BsonDocument("x", 2));
        Assert.Equal(1, await coll.CountDocumentsAsync("{x: 2}"));
        Assert.Contains(proxy.Server.DownstreamTls.Sha256Fingerprint, await proxy.HttpGetAsync("/readyz"));

        // A plaintext client against a TLS listener doesn't get through.
        var plain = new MongoClient(new MongoClientSettings
        {
            Server = new MongoServerAddress("localhost", proxy.BasePort),
            DirectConnection = true,
            ServerSelectionTimeout = TimeSpan.FromSeconds(2),
        });
        await Assert.ThrowsAsync<TimeoutException>(() =>
            plain.GetDatabase("admin").RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1)));
    }
}
