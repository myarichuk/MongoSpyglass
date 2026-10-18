using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using Spyglass.Proxy.Tests.Infrastructure;

namespace Spyglass.Proxy.Tests;

public sealed class ObservationFixture : IAsyncLifetime
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

/// <summary>01 §7 correlation and the 02 §3 join key, checked against what the real driver reports.</summary>
public sealed class ObservationTests(ObservationFixture f) : IClassFixture<ObservationFixture>
{
    private static string S(JsonElement e, string p) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

    private (MongoClient Client, ConcurrentQueue<CommandStartedEvent> Started) Client(string? app = null, Action<MongoClientSettings>? configure = null)
    {
        var started = new ConcurrentQueue<CommandStartedEvent>();
        var settings = MongoClientSettings.FromConnectionString(f.Proxy.Uri("rs0"));
        settings.ApplicationName = app;
        settings.ClusterConfigurator = cb => cb.Subscribe<CommandStartedEvent>(started.Enqueue);
        configure?.Invoke(settings);
        return (new MongoClient(settings), started);
    }

    [Fact]
    public async Task Join_key_from_driver_events_matches_wire_ops()
    {
        var (client, started) = Client("join-test");
        var coll = client.GetDatabase("obs").GetCollection<BsonDocument>("join");
        await coll.InsertManyAsync(Enumerable.Range(0, 300).Select(i => new BsonDocument { ["_id"] = Guid.NewGuid().ToString(), ["i"] = i }));
        for (var i = 0; i < 20; i++)
            await coll.Find(new BsonDocument("i", i)).FirstOrDefaultAsync();
        await coll.Find("{}").Sort("{i: 1}").ToListAsync(); // find + getMores
        await coll.WithReadPreference(ReadPreference.Secondary).CountDocumentsAsync("{}");
        await coll.UpdateManyAsync("{}", "{$inc: {i: 1}}");
        await coll.DeleteManyAsync("{i: {$gt: 290}}");
        try { await client.GetDatabase("obs").RunCommandAsync<BsonDocument>(new BsonDocument("noSuchCommand", 1)); }
        catch (MongoCommandException) { }

        var events = started.ToArray();
        Assert.True(events.Length > 20, $"only {events.Length} commands");

        // The driver raises command events for the handshake itself, before the server has assigned the
        // connection id: those (and only those) cannot be joined (02 §3.3; the exporter skips them, 04 §4).
        var handshakes = events.Where(e => e.ConnectionId.LongServerValue == null).ToList();
        Assert.All(handshakes, e => Assert.True(e.CommandName is "isMaster" or "hello", e.CommandName));
        events = events.Where(e => e.ConnectionId.LongServerValue != null).ToArray();
        static (string, long, int) KeyOf(CommandStartedEvent e)
        {
            var ep = (DnsEndPoint)e.ConnectionId.ServerId.EndPoint;
            return ($"{ep.Host.ToLowerInvariant()}:{ep.Port}", e.ConnectionId.LongServerValue!.Value, e.RequestId);
        }

        var index = await f.Proxy.WaitForKeysAsync("op",
            o => (S(o, "member"), o.GetProperty("serverConnectionId").ValueKind == System.Text.Json.JsonValueKind.Number ? o.GetProperty("serverConnectionId").GetInt64() : -1, o.GetProperty("requestId").GetInt32()),
            events.Select(KeyOf).ToList());
        var missing = new List<string>();
        foreach (var e in events)
        {
            var key = KeyOf(e);
            var match = index[key].ToList();
            if (match.Count != 1) { missing.Add($"{e.CommandName} {key} x{match.Count}"); continue; }
            Assert.Equal(e.CommandName, S(match[0], "command"));
            Assert.Equal(e.DatabaseNamespace.DatabaseName, S(match[0], "db"));
            Assert.Equal(f.Proxy.Server.ProxyId.ToString(), S(match[0], "proxyId"));
        }
        Assert.Empty(missing); // join rate 100% on a quiescent run
    }

    [Fact]
    public async Task Op_records_carry_reply_facts()
    {
        var (client, _) = Client("facts-test");
        var db = client.GetDatabase("obs");
        var name = "facts" + Guid.NewGuid().ToString("N")[..6];
        var coll = db.GetCollection<BsonDocument>(name);
        await coll.InsertManyAsync(Enumerable.Range(0, 120).Select(i => new BsonDocument { ["_id"] = i, ["k"] = i % 3 }));
        var all = await coll.Find("{}").Sort("{_id: 1}").ToListAsync();
        var ops = (await f.Proxy.WaitRecordsAsync(r => S(r, "type") == "op" && S(r, "coll") == name, 2)).ToList();

        var insert = ops.First(o => S(o, "command") == "insert");
        Assert.Equal(120, insert.GetProperty("seqDocs").GetProperty("documents").GetInt32());
        Assert.Equal(120, insert.GetProperty("reply").GetProperty("n").GetInt32());
        Assert.Equal("ok", S(insert, "status"));
        Assert.True(insert.GetProperty("ack").GetBoolean());
        Assert.True(insert.GetProperty("serverLatency_us").GetInt64() > 0);
        Assert.NotEqual("", S(insert, "lsid"));
        Assert.Equal(16, S(insert, "lsid").Length);

        var find = ops.First(o => S(o, "command") == "find");
        Assert.Equal(101, find.GetProperty("reply").GetProperty("nReturned").GetInt32()); // default first batch
        Assert.NotEqual("0", S(find.GetProperty("reply"), "cursorId"));
        Assert.Equal(120, all.Count);

        await f.Proxy.WaitRecordsAsync(r => S(r, "type") == "op" && S(r, "command") == "getMore" && S(r, "coll") == name, 1);
        var getMore = f.Proxy.RecordsOf("op").First(o => S(o, "command") == "getMore" && S(o, "coll") == name);
        Assert.Equal(19, getMore.GetProperty("reply").GetProperty("nReturned").GetInt32());
        Assert.Equal("0", S(getMore.GetProperty("reply"), "cursorId"));

        // the exported command is redacted BSON; with values=truncated, short values survive
        var cmd = MongoDB.Bson.Serialization.BsonSerializer.Deserialize<BsonDocument>(Convert.FromBase64String(S(find, "cmd")));
        Assert.Equal(name, cmd["find"].AsString);
        Assert.Equal("obs", cmd["$db"].AsString);
        Assert.Equal(16, cmd["lsid"]["id"].AsString.Length);
    }

    [Fact]
    public async Task Errors_and_unacknowledged_writes()
    {
        var (client, _) = Client("err-test");
        var db = client.GetDatabase("obs");
        try { await db.RunCommandAsync<BsonDocument>(new BsonDocument("definitelyNotACommand", 1)); }
        catch (MongoCommandException) { }
        var err = (await f.Proxy.WaitRecordsAsync(r => S(r, "command") == "definitelyNotACommand", 1))[0];
        Assert.Equal("error", S(err, "status"));
        Assert.Equal("CommandNotFound", S(err.GetProperty("reply"), "codeName"));
        Assert.Contains("spyglass_command_errors_total{command=\"definitelyNotACommand\"", await f.Proxy.HttpGetAsync("/metrics"));

        var w0 = db.GetCollection<BsonDocument>("w0").WithWriteConcern(WriteConcern.Unacknowledged);
        await w0.InsertOneAsync(new BsonDocument("x", 1));
        var unack = (await f.Proxy.WaitRecordsAsync(r => S(r, "coll") == "w0" && S(r, "command") == "insert", 1))[0];
        Assert.False(unack.GetProperty("ack").GetBoolean());
        Assert.Equal("unacknowledged", S(unack, "status"));
        Assert.Equal(JsonValueKind.Null, unack.GetProperty("reply").ValueKind);
    }

    [Fact]
    public async Task Connection_records_carry_client_metadata()
    {
        var (client, _) = Client("conn-test-app");
        await client.GetDatabase("admin").RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1));
        var open = (await f.Proxy.WaitRecordsAsync(r => S(r, "type") == "conn" && S(r, "event") == "open" && S(r, "app") == "conn-test-app", 1))[0];
        Assert.Contains("mongo-csharp-driver", S(open, "driver"));
        Assert.Equal(JsonValueKind.Number, open.GetProperty("serverConnectionId").ValueKind);
        Assert.Equal(JsonValueKind.Null, open.GetProperty("exporter").ValueKind);

        // MongoClient.Dispose() leaves the shared cluster (and its sockets) alive; dispose the cluster itself.
        MongoDB.Driver.ClusterRegistry.Instance.UnregisterAndDisposeCluster(client.Cluster);
        var close = await f.Proxy.WaitRecordsAsync(r => S(r, "type") == "conn" && S(r, "event") == "close" && S(r, "app") == "conn-test-app", 1);
        Assert.All(close, c => Assert.Equal("client_closed", S(c, "closeReason")));
    }

    [Fact]
    public async Task Exporter_marker_is_detected()
    {
        var (client, _) = Client("marker-test", s => s.LibraryInfo = new MongoDB.Driver.Core.Configuration.LibraryInfo("spyglass-dotnet", "0.1.0"));
        await client.GetDatabase("admin").RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1));
        var open = (await f.Proxy.WaitRecordsAsync(r => S(r, "type") == "conn" && S(r, "app") == "marker-test", 1))[0];
        Assert.Equal("spyglass-dotnet", S(open, "exporter"));
        Assert.Contains("|spyglass-dotnet", S(open, "driver"));
    }

    [Fact]
    public async Task Cursor_gauge_tracks_open_exhausted_and_killed_cursors()
    {
        var (client, _) = Client("cursor-test");
        var name = "cur" + Guid.NewGuid().ToString("N")[..6];
        var coll = client.GetDatabase("obs").GetCollection<BsonDocument>(name);
        await coll.InsertManyAsync(Enumerable.Range(0, 50).Select(i => new BsonDocument("_id", i)));

        string Gauge() => f.Proxy.Server.Observation!.Commands.CursorsOpen.WithLabels("obs", name).Value.ToString();

        using (var cursor = await coll.FindAsync("{}", new FindOptions<BsonDocument> { BatchSize = 10 }))
        {
            await cursor.MoveNextAsync();
            await ReplicaSetTests.WaitUntil(() => Gauge() == "1", TimeSpan.FromSeconds(5));
        } // dispose → killCursors
        await ReplicaSetTests.WaitUntil(() => Gauge() == "0", TimeSpan.FromSeconds(5));

        var exhausted = await coll.Find("{}", new FindOptions { BatchSize = 10 }).ToListAsync();
        Assert.Equal(50, exhausted.Count);
        await f.Proxy.WaitRecordsAsync(r => S(r, "command") == "getMore" && S(r, "coll") == name && S(r.GetProperty("reply"), "cursorId") == "0", 1);
        Assert.Equal("0", Gauge());
    }
}
