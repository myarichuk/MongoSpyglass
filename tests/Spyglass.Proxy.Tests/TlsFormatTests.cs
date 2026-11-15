using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using Spyglass.Proxy.Config;
using Spyglass.Proxy.Tests.Infrastructure;

namespace Spyglass.Proxy.Tests;

[CollectionDefinition("tls-rs")]
public sealed class TlsReplicaSetCollection : ICollectionFixture<TlsReplicaSetFixture>;

/// <summary>Certificate formats, full chain, URI TLS options and the TLS part of the proxy record, against the TLS replica set (01 §2, §6, 02 §4.2).</summary>
[Collection("tls-rs")]
public sealed class TlsFormatTests(TlsReplicaSetFixture f)
{
    private TestPki Pki => _pki ??= TestPki.Create(Path.Combine(f.Dir, "pki"));
    private TestPki? _pki;

    private static async Task AssertClientTrustingOnlyRootConnects(ProxyHost proxy, string rootCa)
    {
        var client = TestClients.Create(proxy.Uri("rs0", "tls=true"), rootCa);
        var coll = client.GetDatabase("tls").GetCollection<BsonDocument>("fmt");
        await coll.InsertOneAsync(new BsonDocument("x", 3));
        Assert.True(await coll.CountDocumentsAsync("{x: 3}") >= 1);
    }

    [Theory]
    [InlineData("combined")]
    [InlineData("split")]
    [InlineData("encrypted")]
    [InlineData("pfx")]
    public async Task Downstream_cert_formats_send_the_full_chain(string format)
    {
        var pki = Pki;
        await using var proxy = await ProxyHost.StartAsync(f.Mongo.DirectUri, configure: a =>
        {
            a["upstream.tls.caFile"] = f.Certs.CaFile;
            a["listen.tls.mode"] = "tls";
            switch (format)
            {
                case "combined": a["listen.tls.certFile"] = pki.CombinedPemFile; break;
                case "split": a["listen.tls.certFile"] = pki.ChainCertFile; a["listen.tls.keyFile"] = pki.LeafKeyFile; break;
                case "encrypted":
                    a["listen.tls.certFile"] = pki.ChainCertFile; a["listen.tls.keyFile"] = pki.EncryptedKeyFile; a["listen.tls.keyPassword"] = TestPki.Password; break;
                default: a["listen.tls.certFile"] = pki.PfxFile; a["listen.tls.keyPassword"] = TestPki.Password; break;
            }
        });
        // The leaf is signed by an intermediate; the client trusts only the root and must get the intermediate from the proxy.
        await AssertClientTrustingOnlyRootConnects(proxy, pki.RootCaFile);
        Assert.False(proxy.Server.DownstreamTls!.SelfSigned);
    }

    [Fact]
    public async Task Downstream_leaf_only_cert_fails_a_client_that_trusts_only_the_root()
    {
        // control for the test above: without the intermediate the client cannot build a chain
        var pki = Pki;
        await using var proxy = await ProxyHost.StartAsync(f.Mongo.DirectUri, configure: a =>
        {
            a["upstream.tls.caFile"] = f.Certs.CaFile;
            a["listen.tls.mode"] = "tls";
            a["listen.tls.certFile"] = pki.LeafOnlyCertFile;
            a["listen.tls.keyFile"] = pki.LeafKeyFile;
        });
        var client = TestClients.Create(proxy.Uri("rs0", "tls=true"), pki.RootCaFile, s => s.ServerSelectionTimeout = TimeSpan.FromSeconds(3));
        await Assert.ThrowsAnyAsync<Exception>(() => client.GetDatabase("admin").RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1)));
    }

    [Fact]
    public async Task Uri_tlsCAFile_works_without_upstream_tls_caFile()
    {
        var uri = f.Mongo.DirectUri + "&tlsCAFile=" + Uri.EscapeDataString(f.Certs.CaFile);
        await using var proxy = await ProxyHost.StartAsync(uri);
        Assert.True(proxy.Server.Upstream.UsesTls);
        Assert.Equal("caFile", proxy.Server.Upstream.TrustMode);
        var (client, _, _) = ReplicaSetTests.Client(proxy.Uri("rs0"));
        await client.GetDatabase("tls").GetCollection<BsonDocument>("uri").InsertOneAsync(new BsonDocument("x", 1));

        // a URI CA that does not match fails closed
        var other = TestCertificates.Create(Path.Combine(f.Dir, "other-uri"), "Another CA");
        await Assert.ThrowsAsync<ConfigException>(() =>
            ProxyHost.StartAsync(f.Mongo.DirectUri + "&tlsCAFile=" + Uri.EscapeDataString(other.CaFile)));
    }

    [Fact]
    public async Task Proxy_record_reports_tls_without_secrets_and_metric_is_exposed()
    {
        var pki = Pki;
        await using var collector = new FakeCollector();
        await using var proxy = await ProxyHost.StartAsync(f.Mongo.DirectUri, configure: a =>
        {
            a["upstream.tls.caFile"] = f.Certs.CaFile;
            a["listen.tls.mode"] = "tls";
            a["listen.tls.certFile"] = pki.PfxFile;
            a["listen.tls.keyPassword"] = TestPki.Password;
            a["collector.endpoint"] = collector.Endpoint;
            a["collector.token"] = "tok-should-not-matter";
        });
        var line = System.Text.Encoding.UTF8.GetString(proxy.Server.Observation!.ProxyRecord());
        Assert.DoesNotContain(TestPki.Password, line);
        Assert.DoesNotContain("PRIVATE", line);
        var rec = JsonDocument.Parse(line).RootElement;

        var tls = rec.GetProperty("config").GetProperty("tls");
        var up = tls.GetProperty("upstream");
        Assert.True(up.GetProperty("enabled").GetBoolean());
        Assert.Equal("caFile", up.GetProperty("trust").GetString());
        Assert.Equal(f.Certs.CaFile, up.GetProperty("caFile").GetString());
        Assert.Equal("none", up.GetProperty("revocation").GetString());
        Assert.False(up.GetProperty("allowInvalidHostnames").GetBoolean());
        Assert.False(up.TryGetProperty("clientCert", out _));
        var listen = tls.GetProperty("listen");
        Assert.Equal("tls", listen.GetProperty("mode").GetString());
        Assert.Equal("file", listen.GetProperty("source").GetString());
        var cert = listen.GetProperty("cert");
        Assert.Equal("CN=localhost", cert.GetProperty("subject").GetString());
        Assert.Equal("CN=Spyglass Test Intermediate", cert.GetProperty("issuer").GetString());
        Assert.Equal(proxy.Server.DownstreamTls!.Sha256Fingerprint, cert.GetProperty("sha256").GetString());
        Assert.Equal("system", tls.GetProperty("collector").GetProperty("trust").GetString());
        Assert.False(tls.GetProperty("collector").GetProperty("https").GetBoolean());

        // per-member server certificates, captured during upstream validation
        var members = rec.GetProperty("members").EnumerateArray().ToList();
        Assert.NotEmpty(members);
        foreach (var m in members)
        {
            var sc = m.GetProperty("cert");
            Assert.Equal("CN=localhost", sc.GetProperty("subject").GetString());
            Assert.Equal("CN=Spyglass Test CA", sc.GetProperty("issuer").GetString());
            Assert.Equal(64, sc.GetProperty("sha256").GetString()!.Length);
        }

        var metrics = await proxy.HttpGetAsync("/metrics");
        Assert.Contains("spyglass_proxy_tls_cert_expiry_seconds{hop=\"listen\",member=\"\"}", metrics);
        Assert.Contains("spyglass_proxy_tls_cert_expiry_seconds{hop=\"upstream_server\",member=\"localhost:", metrics);
        Assert.DoesNotContain(TestPki.Password, metrics);
        Assert.DoesNotContain(TestPki.Password, await proxy.HttpGetAsync("/readyz"));
    }
}

/// <summary>Upstream client certificates: mongod started with net.tls requiring them (no --tlsAllowConnectionsWithoutCertificates).</summary>
public sealed class TlsClientCertTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "spyglass-tests", "tlsmtls-" + Guid.NewGuid().ToString("N")[..8]);
    private TestPki _pki = null!;
    private Deployment _mongo = null!;

    public async ValueTask InitializeAsync()
    {
        MongodBinaries.SkipIfMissing();
        _pki = TestPki.Create(_dir);
        _mongo = await Deployment.StartStandaloneAsync(_pki.AsTestCertificates(), requireClientCert: true);
    }

    public async ValueTask DisposeAsync()
    {
        if (_mongo != null) await _mongo.DisposeAsync();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private async Task AssertConnects(Action<Dictionary<string, string>> configure, string? uri = null)
    {
        await using var proxy = await ProxyHost.StartAsync(uri ?? _mongo.DirectUri, listeners: 1, configure: a =>
        {
            a["upstream.tls.caFile"] = _pki.RootCaFile;
            configure(a);
        });
        // The driver has no client cert of its own; the proxy presents one to mongod.
        var client = TestClients.Create($"mongodb://localhost:{proxy.BasePort}/?directConnection=true", null);
        var coll = client.GetDatabase("mtls").GetCollection<BsonDocument>("c");
        await coll.InsertOneAsync(new BsonDocument("x", 1));
        Assert.Equal(1, await coll.CountDocumentsAsync("{x: 1}"));

        var rec = JsonDocument.Parse(proxy.Server.Observation!.ProxyRecord()).RootElement;
        var cc = rec.GetProperty("config").GetProperty("tls").GetProperty("upstream").GetProperty("clientCert");
        Assert.Equal("CN=spyglass-client", cc.GetProperty("subject").GetString());
        Assert.Contains("spyglass_proxy_tls_cert_expiry_seconds{hop=\"upstream_client\",member=\"\"}", await proxy.HttpGetAsync("/metrics"));
        Assert.DoesNotContain(TestPki.Password, rec.GetRawText());
    }

    [Fact]
    public Task Combined_pem_client_cert() => AssertConnects(a => a["upstream.tls.certFile"] = _pki.ClientCombinedPemFile);

    [Fact]
    public Task Split_pem_client_cert() => AssertConnects(a =>
    {
        a["upstream.tls.certFile"] = _pki.ClientCertFile;
        a["upstream.tls.keyFile"] = _pki.ClientKeyFile;
    });

    [Fact]
    public Task Pfx_client_cert_via_uri_options() => AssertConnects(_ => { },
        _mongo.DirectUri + "&tlsCertificateKeyFile=" + Uri.EscapeDataString(_pki.ClientPfxFile) +
        "&tlsCertificateKeyFilePassword=" + Uri.EscapeDataString(TestPki.Password));

    [Fact]
    public async Task Without_a_client_cert_mongod_refuses_the_proxy()
    {
        await Assert.ThrowsAsync<ConfigException>(() =>
            ProxyHost.StartAsync(_mongo.DirectUri, listeners: 1, configure: a => a["upstream.tls.caFile"] = _pki.RootCaFile));
    }
}
