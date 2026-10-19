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
            // The tight-loop gap (03 §5.2, 5 ms) is the rule's tests' business; here the point is that real traffic raises
            // the alert. Under a loaded full-suite run the driver's loop showed scheduling gaps up to 4.5 ms (12 ms on the
            // first iteration): one over 5 ms mid-loop leaves no run of 10, and the alert never fires.
            NPlusOneGapUs = 50_000,
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
        var loop = ops.Where(o => o.Op.Command == "find" && o.Op.Coll == "orders" && o.Op.Reply is { CursorId: 0, NReturned: 1 }).OrderBy(o => o.Op.Ts).ToList();
        Assert.True(alerts.Any(a => a.Rule == "n-plus-one-suspected"), "no n-plus-one-suspected; gaps (ms) in the loop: " +
            string.Join(" ", loop.Zip(loop.Skip(1), (a, b) => ((b.Op.Ts - a.Op.Ts - (a.Op.RespLastUs ?? 0)) / 1000.0).ToString("F2"))));
        Assert.Contains(ops, o => o.Op.Command == "insert" && o.Failed); // the duplicate key: ok:1 + writeErrors
        var producer = Assert.Single(_collector.Collector.Producers.All);
        Assert.Equal(_proxy.Server.ProxyId.ToString(), producer.Src);
        Assert.Equal(0, producer.Rejected);
    }

    /// <summary>
    /// The join (02 §6) on real traffic: a minimal exporter on the .NET driver's own events, built the way 04 says
    /// (the key from <c>RequestId</c> and <c>ConnectionId.LongServerValue</c>, <c>proxyId</c>/<c>member</c> from the
    /// <c>$spyglass</c> document in the heartbeat for that server, the handshake marker in <c>LibraryInfo</c>). Every
    /// command must join its op, and nothing may conflict or be orphaned.
    /// </summary>
    [Fact]
    public async Task Every_driver_command_joins_its_exporter_record()
    {
        var src = Guid.NewGuid().ToString();
        var discovery = new ConcurrentDictionary<System.Net.EndPoint, (string ProxyId, string Member)>();
        var started = new ConcurrentDictionary<int, CommandStartedEvent>();
        var cmds = new ConcurrentQueue<string>();
        var settings = MongoClientSettings.FromConnectionString(_proxy.Uri(extra: "directConnection=true"));
        settings.ApplicationName = "e2e-joined";
        settings.LibraryInfo = new MongoDB.Driver.Core.Configuration.LibraryInfo("spyglass-dotnet", "0.0.0-test"); // 02 §2.4 marker
        long Us() => (DateTime.UtcNow - DateTime.UnixEpoch).Ticks / 10;
        void Done(int requestId, string status, TimeSpan duration)
        {
            if (!started.TryRemove(requestId, out var s) || s.ConnectionId.LongServerValue is not { } sid) return; // handshakes: 02 §3.3
            var d = discovery.GetValueOrDefault(s.ConnectionId.ServerId.EndPoint);
            cmds.Enqueue(System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["type"] = "cmd", ["ts"] = Us() - (long)duration.TotalMicroseconds, ["src"] = src, ["proxyId"] = d.ProxyId, ["member"] = d.Member,
                ["serverConnectionId"] = sid, ["requestId"] = requestId, ["operationId"] = s.OperationId?.ToString(), ["command"] = s.CommandName,
                ["db"] = s.DatabaseNamespace.DatabaseName, ["status"] = status, ["client_us"] = (long)duration.TotalMicroseconds, ["poolWait_us"] = null,
                ["attempt"] = null, ["uow"] = null, ["trace"] = null, ["callsite"] = null, ["cursorId"] = null, ["errorCode"] = null, ["errorLabels"] = null,
            }));
        }
        settings.ClusterConfigurator = cb => cb
            .Subscribe<ServerHeartbeatSucceededEvent>(e =>
            {
                if (e.Reply?.GetValue("$spyglass", null) is BsonDocument sp)
                    discovery[e.ConnectionId.ServerId.EndPoint] = (sp["proxyId"].AsString, sp["member"].AsString);
            })
            .Subscribe<CommandStartedEvent>(e => started[e.RequestId] = e)
            .Subscribe<CommandSucceededEvent>(e => Done(e.RequestId, "ok", e.Duration))
            .Subscribe<CommandFailedEvent>(e => Done(e.RequestId, "error", e.Duration));
        var client = new MongoClient(settings);
        var orders = client.GetDatabase("joined").GetCollection<BsonDocument>("orders");
        await orders.InsertManyAsync(Enumerable.Range(0, 300).Select(i => new BsonDocument { ["_id"] = i, ["c"] = i % 7 }));
        for (var i = 0; i < 40; i++) await orders.Find(new BsonDocument("_id", i)).FirstOrDefaultAsync();
        Assert.Equal(300, (await orders.Find(new BsonDocument()).ToListAsync()).Count);
        await orders.UpdateManyAsync(new BsonDocument("c", 1), new BsonDocument("$set", new BsonDocument("x", 1)));
        try { await orders.InsertOneAsync(new BsonDocument("_id", 1)); } catch (MongoWriteException) { }
        try { await client.GetDatabase("joined").RunCommandAsync<BsonDocument>(new BsonDocument("noSuchCommand", 1)); } catch (MongoCommandException) { }
        Assert.NotEmpty(discovery);

        // the exporter flushes once, at the end: some of its cmds are on time, the rest merge into stored ops
        var hello = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["type"] = "hello", ["ts"] = Us(), ["src"] = src, ["exporter"] = "spyglass-dotnet", ["version"] = "0.0.0-test", ["schema"] = 1, ["app"] = "e2e-joined",
        });
        var lines = cmds.ToList();
        Assert.True(lines.Count >= 44, $"{lines.Count} cmd records"); // insert, 40 finds, find + getMore, update, insert, the failed command
        using (var http = new HttpClient { BaseAddress = _collector.BaseAddress })
            foreach (var chunk in new[] { hello }.Concat(lines).Chunk(500))
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, "/ingest/v0") { Content = new StringContent(string.Join('\n', chunk) + "\n") };
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Token);
                req.Headers.Add("X-Spyglass-Producer", "spyglass-dotnet");
                Assert.Equal(HttpStatusCode.NoContent, (await http.SendAsync(req)).StatusCode);
            }

        var p = _collector.Collector.Producers.Get(src)!;
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (Volatile.Read(ref p.Joined) < lines.Count && DateTime.UtcNow < deadline) await Task.Delay(50);
        await _collector.Collector.DrainAsync(TimeSpan.FromSeconds(10));
        var j = _collector.Collector.Join;
        Assert.True(p.Joined == lines.Count, $"joined {p.Joined} of {lines.Count} (in time {j.Joined}, late {j.JoinedLate}); orphans {p.Orphans}, conflicts {p.Conflicts}");
        Assert.Equal((0L, 0L), (p.Orphans, p.Conflicts));
        Assert.Equal(1.0, p.JoinRate);

        // and each stored op carries its own command's context
        var stored = _collector.Collector.Ops.Query(new OpQuery { Limit = 1000 }).Where(o => o.Ctx != null).ToList();
        Assert.Equal(lines.Count, stored.Count);
        Assert.All(stored, o =>
        {
            Assert.Equal(o.Op.Command, o.Ctx!.Command);
            Assert.Equal(o.Op.RequestId, o.Ctx.RequestId);
            Assert.True(o.Ctx.ClientUs >= o.Op.ServerLatencyUs, $"{o.Op.Command}: client {o.Ctx.ClientUs} µs < server {o.Op.ServerLatencyUs} µs");
        });
    }
}
