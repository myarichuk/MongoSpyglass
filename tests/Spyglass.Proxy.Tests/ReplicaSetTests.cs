using System.Collections.Concurrent;
using System.Net;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using Spyglass.Proxy.Tests.Infrastructure;

namespace Spyglass.Proxy.Tests;

public sealed class ReplicaSetFixture : IAsyncLifetime
{
    public Deployment Mongo { get; private set; } = null!;
    public ProxyHost Proxy { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        MongodBinaries.SkipIfMissing();
        Mongo = await Deployment.StartReplicaSetAsync(3);
        Proxy = await ProxyHost.StartAsync(Mongo.DirectUri);
    }

    public async ValueTask DisposeAsync()
    {
        if (Proxy != null) await Proxy.DisposeAsync();
        if (Mongo != null) await Mongo.DisposeAsync();
    }
}

/// <summary>06 §2.2 topology against a real 3-member replica set.</summary>
public sealed class ReplicaSetTests(ReplicaSetFixture f) : IClassFixture<ReplicaSetFixture>
{
    /// <summary>A client through the proxy that records every endpoint it opens a connection to.</summary>
    internal static (MongoClient Client, ConcurrentBag<EndPoint> Dialled, ConcurrentBag<BsonDocument> Heartbeats) Client(string uri)
    {
        var dialled = new ConcurrentBag<EndPoint>();
        var heartbeats = new ConcurrentBag<BsonDocument>();
        var settings = MongoClientSettings.FromConnectionString(uri);
        settings.ClusterConfigurator = cb =>
        {
            cb.Subscribe<ConnectionOpenedEvent>(e => dialled.Add(e.ServerId.EndPoint));
            cb.Subscribe<ServerHeartbeatSucceededEvent>(e => heartbeats.Add(e.Reply));
        };
        return (new MongoClient(settings), dialled, heartbeats);
    }

    private HashSet<int> ProxyPorts => f.Proxy.Server.Topology.Members.Select(m => m.ListenPort).ToHashSet();
    private HashSet<int> RealPorts => f.Mongo.Members.Select(m => m.Port).ToHashSet();

    [Fact]
    public async Task Driver_discovers_members_only_through_the_proxy()
    {
        var (client, dialled, heartbeats) = Client(f.Proxy.Uri("rs0"));
        var coll = client.GetDatabase("rs").GetCollection<BsonDocument>("disc");
        await coll.InsertOneAsync(new BsonDocument("x", 1));
        var _ = await coll.Find("{}").Limit(1).ToListAsync(); // primary read

        // secondary read forces connections to other members
        await coll.WithReadPreference(ReadPreference.Secondary).Find("{}").Limit(1).ToListAsync();

        await WaitUntil(() => client.Cluster.Description.Servers.Count == 3, TimeSpan.FromSeconds(20));
        var servers = client.Cluster.Description.Servers.Select(s => ((DnsEndPoint)s.EndPoint).Port).ToHashSet();
        Assert.Equal(3, servers.Count);
        Assert.Subset(ProxyPorts, servers);

        var dialledPorts = dialled.Select(Port).ToHashSet();
        Assert.Empty(dialledPorts.Intersect(RealPorts));
        Assert.Subset(ProxyPorts, dialledPorts);

        // every heartbeat reply is rewritten: hosts/primary/me name proxy listeners, $spyglass present
        Assert.NotEmpty(heartbeats);
        foreach (var hb in heartbeats)
        {
            Assert.True(hb.Contains("$spyglass"));
            foreach (var h in hb["hosts"].AsBsonArray)
                Assert.Contains(int.Parse(h.AsString.Split(':')[1]), ProxyPorts);
            Assert.Contains(int.Parse(hb["me"].AsString.Split(':')[1]), ProxyPorts);
            Assert.Equal(hb["me"].AsString, hb["$spyglass"]["member"].AsString);
        }
    }

    [Fact]
    public async Task Listener_ports_are_base_plus_index_in_hosts_order()
    {
        var members = f.Proxy.Server.Topology.Members.OrderBy(m => m.ListenPort).ToList();
        Assert.Equal(Enumerable.Range(f.Proxy.BasePort, 3), members.Select(m => m.ListenPort));
        Assert.Equal(f.Mongo.Members.Select(m => m.Port), members.Select(m => m.Upstream.Port));
        Assert.Contains("ready", await f.Proxy.HttpGetAsync("/readyz"));
    }

    [Fact]
    public async Task Direct_connection_to_one_listener_pins_that_member()
    {
        var secondaryListener = f.Proxy.BasePort + 2;
        var client = new MongoClient($"mongodb://localhost:{secondaryListener}/?directConnection=true");
        var hello = await client.GetDatabase("admin").RunCommandAsync<BsonDocument>(new BsonDocument("hello", 1));
        Assert.Equal($"localhost:{secondaryListener}", hello["me"].AsString);
    }

    [Fact]
    public async Task Compression_is_stripped_from_the_handshake()
    {
        var (client, _, heartbeats) = Client(f.Proxy.Uri("rs0", "compressors=zlib"));
        await client.GetDatabase("admin").RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1));
        await WaitUntil(() => !heartbeats.IsEmpty, TimeSpan.FromSeconds(10));
        Assert.All(heartbeats, hb => Assert.False(hb.Contains("compression")));

        // control: directly, the same client options negotiate zlib
        var (direct, _, directHb) = Client(f.Mongo.DirectUri + "&compressors=zlib");
        await direct.GetDatabase("admin").RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1));
        await WaitUntil(() => !directHb.IsEmpty, TimeSpan.FromSeconds(10));
        Assert.Contains(directHb, hb => hb.Contains("compression"));
    }

    internal static int Port(EndPoint e) => e switch
    {
        DnsEndPoint d => d.Port,
        IPEndPoint i => i.Port,
        _ => -1
    };

    internal static async Task WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("condition not met");
            await Task.Delay(100);
        }
    }
}

/// <summary>Failover and reconfiguration mutate the replica set, so they get their own deployment.</summary>
public sealed class ReplicaSetMutationTests
{
    [Fact]
    public async Task Stepdown_writes_resume_on_new_primary_through_proxy()
    {
        MongodBinaries.SkipIfMissing();
        await using var mongo = await Deployment.StartReplicaSetAsync(3);
        await using var proxy = await ProxyHost.StartAsync(mongo.DirectUri);
        var (client, dialled, _) = ReplicaSetTests.Client(proxy.Uri("rs0"));
        var coll = client.GetDatabase("fo").GetCollection<BsonDocument>("c");
        await coll.InsertOneAsync(new BsonDocument("phase", "before"));
        var before = client.Cluster.Description.Servers.Single(s => s.Type == MongoDB.Driver.Core.Servers.ServerType.ReplicaSetPrimary).EndPoint;

        try
        {
            await client.GetDatabase("admin").RunCommandAsync<BsonDocument>(
                new BsonDocument { ["replSetStepDown"] = 30, ["secondaryCatchUpPeriodSecs"] = 10 });
        }
        catch (MongoException) { /* the primary drops connections on stepdown */ }

        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (true)
        {
            try
            {
                await coll.InsertOneAsync(new BsonDocument("phase", "after"));
                break;
            }
            catch (Exception e) when (e is MongoException or TimeoutException && DateTime.UtcNow < deadline)
            {
                await Task.Delay(250);
            }
        }

        var after = client.Cluster.Description.Servers.Single(s => s.Type == MongoDB.Driver.Core.Servers.ServerType.ReplicaSetPrimary).EndPoint;
        Assert.NotEqual(before, after);
        var proxyPorts = proxy.Server.Topology.Members.Select(m => m.ListenPort).ToHashSet();
        Assert.Contains(ReplicaSetTests.Port(after), proxyPorts);
        Assert.Empty(dialled.Select(ReplicaSetTests.Port).Intersect(mongo.Members.Select(m => m.Port)));
        Assert.Equal(2, await coll.CountDocumentsAsync("{}"));
    }

    [Fact]
    public async Task Added_member_gets_a_listener_and_is_discovered()
    {
        MongodBinaries.SkipIfMissing();
        await using var mongo = await Deployment.StartReplicaSetAsync(2);
        await using var proxy = await ProxyHost.StartAsync(mongo.DirectUri, listeners: 2);
        var (client, dialled, _) = ReplicaSetTests.Client(proxy.Uri("rs0"));
        await client.GetDatabase("admin").RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1));

        var port = Ports.FreeRange(1);
        await using var extra = MongodProcess.Start(port, mongo.Root, ["--replSet", "rs0"]);
        await extra.WaitListeningAsync(TimeSpan.FromSeconds(30));

        var admin = client.GetDatabase("admin");
        var cfg = (await admin.RunCommandAsync<BsonDocument>(new BsonDocument("replSetGetConfig", 1)))["config"].AsBsonDocument;
        cfg["version"] = cfg["version"].ToInt32() + 1;
        cfg["members"].AsBsonArray.Add(new BsonDocument { ["_id"] = 9, ["host"] = $"localhost:{port}", ["priority"] = 0, ["votes"] = 0 });
        await admin.RunCommandAsync<BsonDocument>(new BsonDocument("replSetReconfig", cfg));

        await ReplicaSetTests.WaitUntil(() => client.Cluster.Description.Servers.Count == 3, TimeSpan.FromSeconds(30));
        var newMember = proxy.Server.Topology.Members.Single(m => m.Upstream.Port == port);
        Assert.Contains(client.Cluster.Description.Servers, s => ReplicaSetTests.Port(s.EndPoint) == newMember.ListenPort);
        Assert.DoesNotContain(dialled, e => ReplicaSetTests.Port(e) == port);
    }
}
