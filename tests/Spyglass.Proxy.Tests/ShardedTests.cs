using System.Collections.Concurrent;
using System.Net;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using Spyglass.Proxy.Config;
using Spyglass.Proxy.Tests.Infrastructure;

namespace Spyglass.Proxy.Tests;

/// <summary>06 §2.2 sharded: one listener per mongos, no topology rewrite (R2 only), driver balances across them.</summary>
public sealed class ShardedTests
{
    [Fact]
    public async Task Two_mongos_through_two_listeners()
    {
        MongodBinaries.SkipIfMissing();
        await using var mongo = await Deployment.StartShardedAsync(routers: 2);
        await using var proxy = await ProxyHost.StartAsync(mongo.DirectUri, listeners: 2);
        Assert.Equal(TopologyKind.Sharded, proxy.Server.Topology.Kind);
        Assert.Equal(2, proxy.Server.Topology.Members.Count);

        var dialled = new ConcurrentBag<EndPoint>();
        var settings = MongoClientSettings.FromConnectionString($"mongodb://localhost:{proxy.BasePort},localhost:{proxy.BasePort + 1}/");
        settings.ClusterConfigurator = cb => cb.Subscribe<ConnectionOpenedEvent>(e => dialled.Add(e.ServerId.EndPoint));
        var client = new MongoClient(settings);

        var hello = await client.GetDatabase("admin").RunCommandAsync<BsonDocument>(new BsonDocument("hello", 1));
        Assert.Equal("isdbgrid", hello["msg"].AsString);
        Assert.True(hello.Contains("$spyglass"));
        Assert.False(hello.Contains("hosts"));

        var coll = client.GetDatabase("shard").GetCollection<BsonDocument>("c");
        await Parallel.ForEachAsync(Enumerable.Range(0, 200), new ParallelOptions { MaxDegreeOfParallelism = 16 },
            async (i, _) => await coll.InsertOneAsync(new BsonDocument("i", i)));
        Assert.Equal(200, await coll.CountDocumentsAsync("{}"));

        await ReplicaSetTests.WaitUntil(() => client.Cluster.Description.Servers.Count == 2, TimeSpan.FromSeconds(10));
        var proxyPorts = proxy.Server.Topology.Members.Select(m => m.ListenPort).ToHashSet();
        var dialledPorts = dialled.Select(ReplicaSetTests.Port).ToHashSet();
        Assert.Equal(proxyPorts, dialledPorts); // both listeners used, nothing else dialled
        var ops = proxy.RecordsOf("op").Where(o => o.GetProperty("command").GetString() == "insert").ToList();
        Assert.Equal(2, ops.Select(o => o.GetProperty("member").GetString()).Distinct().Count()); // load spread over both mongos
    }
}
