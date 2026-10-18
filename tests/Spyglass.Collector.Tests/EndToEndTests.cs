using System.Collections.Concurrent;
using System.Net;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using Spyglass.Collector.Storage;
using Spyglass.Proxy.Tests.Infrastructure;

namespace Spyglass.Collector.Tests;

/// <summary>
/// M1 end to end: the .NET driver → spyglass-proxy → real mongod, the proxy exporting over HTTP to the
/// collector. Every command the driver reports is one stored operation (02 §3 key), with the shape,
/// cursor and alert the collector derives from it.
/// </summary>
public sealed class EndToEndTests : IAsyncLifetime
{
    private const string Token = "e2e-token";
    private Deployment _mongo = null!;
    private Server _collector = null!;
    private ProxyHost _proxy = null!;

    public async ValueTask InitializeAsync()
    {
        MongodBinaries.SkipIfMissing();
        _mongo = await Deployment.StartStandaloneAsync();
        _collector = await Server.StartAsync(new CollectorOptions
        {
            Bind = new IPEndPoint(IPAddress.Loopback, 0), Token = Token, Auth = AuthMode.None, Data = null, Workers = 2,
        });
        _proxy = await ProxyHost.StartAsync(_mongo.DirectUri, listeners: 1, configure: a =>
        {
            a["collector.endpoint"] = new Uri(_collector.BaseAddress, "ingest/v0").ToString();
            a["collector.token"] = Token;
            a["collector.flushIntervalMs"] = "50";
        });
    }

    public async ValueTask DisposeAsync()
    {
        if (_proxy != null) await _proxy.DisposeAsync();
        if (_collector != null) await _collector.DisposeAsync();
        if (_mongo != null) await _mongo.DisposeAsync();
    }

    [Fact]
    public async Task Every_driver_command_is_one_stored_operation_with_shape_cursor_and_alerts()
    {
        var started = new ConcurrentQueue<CommandStartedEvent>();
        var settings = MongoClientSettings.FromConnectionString(_proxy.Uri(extra: "directConnection=true"));
        settings.ApplicationName = "e2e-app";
        settings.ClusterConfigurator = cb => cb.Subscribe<CommandStartedEvent>(started.Enqueue);
        var client = new MongoClient(settings);
        var db = client.GetDatabase("shop");
        var orders = db.GetCollection<BsonDocument>("orders");
        await orders.InsertManyAsync(Enumerable.Range(0, 1500).Select(i => new BsonDocument { ["_id"] = i, ["customer"] = i % 30 }));
        var page = await orders.Find(new BsonDocument("customer", 3)).Limit(20).ToListAsync();
        foreach (var o in page)                                                    // N+1 in a tight loop
            await orders.Find(new BsonDocument("_id", o["_id"])).FirstOrDefaultAsync();
        var all = await orders.Find(new BsonDocument()).ToListAsync();             // find + getMores
        Assert.Equal(1500, all.Count);
        try { await orders.InsertOneAsync(new BsonDocument("_id", 1)); } catch (MongoWriteException) { }

        var events = started.Where(e => e.ConnectionId.LongServerValue != null).ToList(); // handshakes can't join (02 §3.3)
        var store = _collector.Collector.Ops;
        static (string, long?, int) KeyOf(CommandStartedEvent e)
        {
            var ep = (DnsEndPoint)e.ConnectionId.ServerId.EndPoint;
            return ($"{ep.Host.ToLowerInvariant()}:{ep.Port}", e.ConnectionId.LongServerValue, e.RequestId);
        }
        // wait for the keys themselves: the store also holds ops the driver never reports (monitoring hellos)
        var deadline = DateTime.UtcNow.AddSeconds(15);
        List<Model.Operation> ops;
        ILookup<(string, long?, int), Model.Operation> index;
        while (true)
        {
            ops = store.Query(new OpQuery { Limit = 1000 });
            index = ops.ToLookup(o => (o.Op.Member, o.Op.ServerConnectionId, o.Op.RequestId));
            if (events.All(e => index.Contains(KeyOf(e))) || DateTime.UtcNow > deadline) break;
            await Task.Delay(100);
        }

        var missing = new List<string>();
        foreach (var e in events)
        {
            var key = KeyOf(e);
            var match = index[key].ToList();
            if (match.Count != 1) { missing.Add($"{e.CommandName} {key} x{match.Count}"); continue; }
            Assert.Equal(e.CommandName, match[0].Op.Command);
            Assert.Equal(e.DatabaseNamespace.DatabaseName, match[0].Op.Db);
            Assert.Equal(_proxy.Server.ProxyId.ToString(), match[0].Op.ProxyId);
        }
        Assert.Empty(missing);
        Assert.All(ops.Where(o => o.Op.Db == "shop"), o => Assert.Equal("e2e-app", o.App));

        // getMores share the opening find's shape; the cursor ran to exhaustion
        var scan = ops.Where(o => o.Op.Command is "find" or "getMore" && o.Op.Coll == "orders" && o.Op.RequestId > 0).ToList();
        var getMores = scan.Where(o => o.Op.Command == "getMore").ToList();
        Assert.NotEmpty(getMores);
        Assert.Single(getMores.Select(g => g.ShapeHash).Distinct());
        var opening = ops.Single(o => o.Op.Command == "find" && o.ShapeHash == getMores[0].ShapeHash);
        bool Exhausted() => _collector.Collector.Analysis.ClosedCursors.Any(c => c.OpenOpId == opening.Id && c.ClosedReason == "exhausted" && c.Documents == 1500);
        for (var i = 0; i < 40 && !Exhausted(); i++) await Task.Delay(100); // cursor snapshots refresh each second
        Assert.True(Exhausted());

        var alerts = _collector.Collector.Analysis.Alerts.Snapshot();
        Assert.Contains(alerts, a => a.Rule == "n-plus-one-suspected");
        Assert.Contains(ops, o => o.Op.Command == "insert" && o.Failed); // the duplicate key: ok:1 + writeErrors
        var producer = Assert.Single(_collector.Collector.Producers.All);
        Assert.Equal(_proxy.Server.ProxyId.ToString(), producer.Src);
        Assert.Equal(0, producer.Rejected);
    }
}
