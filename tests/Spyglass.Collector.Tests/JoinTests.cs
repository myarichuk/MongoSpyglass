using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MongoDB.Bson;
using Spyglass.Collector.Analysis;
using Spyglass.Collector.Storage;
using static Spyglass.Collector.Tests.Records;

namespace Spyglass.Collector.Tests;

public sealed class SqliteJoinTests : JoinTests
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "spyglass-join-" + Guid.NewGuid().ToString("N")[..8]);
    protected override object CreateStore() => new SqliteStore(_dir);
    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        ((IDisposable)Store).Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }
}

/// <summary>02 §6 / 03 §3 beyond the vectors: what the join changes for the live feed, the store, the rules and the bounds.</summary>
public class JoinTests : IAsyncLifetime
{
    private long _now = T0 + 1_000_000;
    protected Collector C = null!;
    protected object Store = null!;
    private IOperationStore Ops => (IOperationStore)Store;
    protected virtual object CreateStore() => new MemoryStore();
    protected virtual CollectorOptions Options() => new() { IngestInsecure = true, Auth = AuthMode.None, Data = null, Workers = 3 };

    public ValueTask InitializeAsync()
    {
        Store = CreateStore();
        C = new Collector(Options(), Ops, (IAggregateStore)Store, (IAlertStore)Store, () => Volatile.Read(ref _now), (ICallsiteStore)Store);
        C.Start();
        return ValueTask.CompletedTask;
    }

    public virtual async ValueTask DisposeAsync() => await C.DisposeAsync();

    private async Task Send(params string[] lines)
    {
        var r = C.Ingest(Batch(lines), null, null);
        Assert.True(r is { Status: 204, Rejected: 0 }, r.ToString());
        await C.DrainAsync(TimeSpan.FromSeconds(10));
    }

    private async Task Advance(long us)
    {
        Interlocked.Add(ref _now, us);
        await C.DrainAsync(TimeSpan.FromSeconds(10));
    }

    private static BsonDocument Find(int id) => new() { ["find"] = "orders", ["filter"] = new BsonDocument("_id", id) };

    [Fact]
    public async Task A_waiting_op_is_live_at_once_and_patched_in_place_when_its_context_arrives()
    {
        using var sub = C.Live.Subscribe(_ => true);
        await Send(Hello(), ExporterConn(1, "orders-api"), Op(10, "find", Find(1)));
        var created = await Next(sub, "op.created");
        var id = created.GetProperty("id").GetString()!;
        Assert.False(created.TryGetProperty("joined", out _));
        Assert.Null(Ops.Get(id));                                // waiting for context: not stored yet (02 §6.2)
        Assert.NotNull(C.Analysis.Waiting(id));                  // … but /ops/{id} can still show it

        await Send(Cmd(10, "find", uowId: "u-1", uowName: "GET /orders/{id}", callsite: "9c1e33a70b2f44d1"));
        var updated = await Next(sub, "op.updated");
        Assert.Equal(id, updated.GetProperty("id").GetString());
        Assert.True(updated.GetProperty("joined").GetBoolean());
        Assert.Equal(900, updated.GetProperty("client_us").GetInt64());
        Assert.Equal("GET /orders/{id}", updated.GetProperty("uowName").GetString());

        var stored = Ops.Get(id)!;
        Assert.Equal(("u-1", 40L), (stored.Ctx!.UowId!, stored.Ctx.PoolWaitUs!.Value));
        Assert.Equal(396, stored.ClientOverheadUs); // client_us 900 − respLast 504
        Assert.Null(C.Analysis.Waiting(id));
        Assert.Single(Ops.Query(new OpQuery { Uow = "u-1" }));
        Assert.Empty(Ops.Query(new OpQuery { Uow = "u-2" }));
    }

    [Fact]
    public async Task A_late_cmd_patches_the_stored_op_and_the_live_row()
    {
        await Send(Hello(), ExporterConn(1, "orders-api"), Op(10, "find", Find(1)));
        await Advance(11_000_000); // the 10 s wait runs out: stored wire-only
        var id = Assert.Single(Ops.Query(new OpQuery())).Id;
        Assert.Null(Ops.Get(id)!.Ctx);

        using var sub = C.Live.Subscribe(_ => true);
        await Send(Cmd(10, "find", operationId: "late"));
        var updated = await Next(sub, "op.updated"); // published by the writer once the update is applied
        Assert.Equal(id, updated.GetProperty("id").GetString());
        Assert.Equal("late", Ops.Get(id)!.Ctx!.OperationId);
        Assert.Equal(1, C.Join.JoinedLate);
    }

    [Fact]
    public async Task Without_an_active_exporter_nothing_waits()
    {
        await Send(Conn(1, "orders-api"), Op(10, "find", Find(1)));
        Assert.Single(Ops.Query(new OpQuery()));
        Assert.Equal(0, C.Join.PendingOps);
        await Advance(601_000_000); // an exporter that went quiet ten minutes ago no longer holds ops back
        await Send(Hello());
        await Send(Op(11, "find", Find(2)));
        Assert.Equal(1, C.Join.PendingOps);
        await Advance(601_000_000);
        await Send(Op(12, "find", Find(3)));
        Assert.Equal(0, C.Join.PendingOps);
        Assert.Equal(3, Ops.Query(new OpQuery()).Count);
    }

    [Fact]
    public async Task Handshake_and_monitoring_hellos_never_wait()
    {
        await Send(Hello(), ExporterConn(1, "orders-api"), Op(10, "hello", coll: null, db: "admin"), Op(11, "isMaster", coll: null, db: "admin"));
        Assert.Equal(0, C.Join.PendingOps);
        Assert.Equal(2, Ops.Query(new OpQuery()).Count);
    }

    [Fact]
    public async Task Shutdown_stores_the_ops_still_waiting()
    {
        await Send(Hello(), ExporterConn(1, "orders-api"), Op(10, "find", Find(1)), Op(11, "find", Find(2)));
        Assert.Equal(2, C.Join.PendingOps);
        await C.DisposeAsync();
        Assert.Equal(2, Ops.Query(new OpQuery()).Count);
        C = new Collector(Options(), Ops, (IAggregateStore)Store, (IAlertStore)Store, () => _now, (ICallsiteStore)Store); // for DisposeAsync
    }

    [Fact]
    public async Task Callsite_frames_are_stored_and_kept_by_use()
    {
        await Send(Hello(), Callsite("9c1e33a70b2f44d1", ("OrdersRepository.GetByIdAsync", "src/Orders/OrdersRepository.cs", 42)));
        var cs = C.Callsites.Callsite("9c1e33a70b2f44d1")!;
        Assert.Equal("OrdersRepository.GetByIdAsync", cs.Frames[0].Fn);
        Assert.Equal(42, cs.Frames[0].Line);
        Assert.Equal(0, C.Callsites.PruneCallsites(_now - 1));
        Assert.Equal(1, C.Callsites.PruneCallsites(_now + 1));
    }

    [Fact]
    public async Task Exporter_skew_above_a_second_raises_an_alert()
    {
        // the exporter's clock runs 3 s ahead: every cmd.ts is 3 s after its op's
        var lines = new List<string> { Hello(), ExporterConn(1, "orders-api") };
        for (var i = 1; i <= 32; i++)
        {
            lines.Add(Op(i, "find", Find(i), ts: T0 + i * 1000));
            lines.Add(Cmd(i, "find", ts: T0 + i * 1000 + 3_000_000));
        }
        await Send([.. lines]);
        Assert.Equal(32, C.Join.Joined);
        var a = Assert.Single(C.Analysis.Alerts.Snapshot(), x => x.Rule == "exporter-skew");
        Assert.Equal(Exporter, a.Scope["src"]);
        Assert.Contains("ahead of", a.Summary);
        Assert.Equal(3_000_000, C.Producers.Get(Exporter)!.SkewUs);
    }

    [Fact]
    public async Task A_join_conflict_raises_exporter_skew()
    {
        await Send(Hello(), ExporterConn(1, "orders-api"), Op(10, "find", Find(1)), Cmd(10, "insert"));
        Assert.Equal(1, C.Join.Conflicts);
        var a = Assert.Single(C.Analysis.Alerts.Snapshot(), x => x.Rule == "exporter-skew");
        Assert.Contains("join conflict", a.Summary);
        Assert.Equal(1, C.Producers.Get(Exporter)!.Conflicts);
    }

    [Fact]
    public async Task Producers_count_cmds_joins_and_orphans_per_exporter()
    {
        await Send(Hello(), ExporterConn(1, "orders-api"), Op(1, "find", Find(1)), Op(2, "find", Find(2)),
            Cmd(1, "find"), Cmd(2, "find"), Cmd(3, "ping", db: "admin", proxyId: null));
        var p = C.Producers.Get(Exporter)!;
        Assert.Equal(("spyglass-dotnet", "0.1.0", "orders-api"), (p.Kind, p.Version!, p.App!));
        Assert.Equal((3L, 2L, 1L), (p.Cmds, p.Joined, p.Orphans));
        Assert.Equal(2.0 / 3, p.JoinRate!.Value, 6);
        var metrics = C.RenderMetrics();
        Assert.Contains("spyglass_collector_join_total{result=\"joined\"} 2", metrics);
        Assert.Contains("spyglass_collector_join_total{result=\"orphan_no_key\"} 1", metrics);
    }

    private static async Task<JsonElement> Next(LiveHub.Subscription sub, string name)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var e = await sub.Reader.ReadAsync(cts.Token);
            if (e.Name == name) return JsonDocument.Parse(e.Payload).RootElement.Clone();
        }
    }
}

/// <summary>The bounds on join state (03 §11): past them, the oldest waiting op is stored early and counted.</summary>
public sealed class JoinBoundTests
{
    [Fact]
    public async Task Waiting_ops_are_bounded()
    {
        var store = new MemoryStore();
        await using var c = new Collector(new CollectorOptions { IngestInsecure = true, Auth = AuthMode.None, Data = null, Workers = 1, JoinMaxWaitingOps = 4 },
            store, store, store);
        c.Start();
        var lines = new List<string> { Hello(), ExporterConn(1, "orders-api") };
        for (var i = 1; i <= 10; i++) lines.Add(Op(i, "find", new BsonDocument { ["find"] = "orders", ["filter"] = new BsonDocument("_id", i) }));
        c.Ingest(Batch([.. lines]), null, null);
        await c.DrainAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(4, c.Join.PendingOps);
        Assert.Equal(6, c.Join.EvictedOps);
        Assert.Equal(6, store.Query(new OpQuery()).Count); // the oldest six, stored wire-only
        Assert.Equal(Enumerable.Range(1, 6), store.Query(new OpQuery()).Select(o => o.Op.RequestId).Order());
    }
}

/// <summary>03 §8 over HTTP: an op is reachable while it waits, then carries its context, callsite frames included.</summary>
public sealed class JoinApiTests : IAsyncLifetime
{
    private Server _s = null!;
    private HttpClient _http = null!;

    public async ValueTask InitializeAsync()
    {
        _s = await Server.StartAsync(new CollectorOptions { Bind = new IPEndPoint(IPAddress.Loopback, 0), IngestInsecure = true, Auth = AuthMode.None, Data = null, Workers = 2 });
        _http = new HttpClient { BaseAddress = _s.BaseAddress };
    }

    public async ValueTask DisposeAsync()
    {
        _http.Dispose();
        await _s.DisposeAsync();
    }

    private async Task Post(params string[] lines)
    {
        using var content = new ByteArrayContent(Batch(lines));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/x-ndjson");
        Assert.Equal(HttpStatusCode.NoContent, (await _http.PostAsync("/ingest/v0", content)).StatusCode);
        await _s.Collector.DrainAsync(TimeSpan.FromSeconds(10));
    }

    private async Task<JsonElement> Get(string path) => JsonDocument.Parse(await _http.GetStringAsync(path)).RootElement.Clone();

    [Fact]
    public async Task Op_detail_shows_the_joined_context_and_the_callsite()
    {
        var now = (DateTime.UtcNow - DateTime.UnixEpoch).Ticks / 10;
        using var sub = _s.Collector.Live.Subscribe(e => e.Name == "op.created");
        await Post(Hello(), ExporterConn(1, "orders-api"), Callsite("9c1e33a70b2f44d1", ("Repo.Get", "src/Repo.cs", 42), ("Controller.Get", "src/Controller.cs", 18)),
            Op(10, "find", new BsonDocument { ["find"] = "orders", ["filter"] = new BsonDocument("_id", 1) }, ts: now));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var waitingId = (await sub.Reader.ReadAsync(cts.Token)).Op!.Id; // the live row the UI would click
        Assert.Empty((await Get("/api/v1/ops")).GetProperty("items").EnumerateArray()); // waiting: not stored yet
        var before = await Get($"/api/v1/ops/{waitingId}");
        Assert.False(before.GetProperty("op").TryGetProperty("context", out _));

        await Post(Cmd(10, "find", ts: now - 100, uowId: "u-1", uowName: "GET /orders/{id}", callsite: "9c1e33a70b2f44d1"));
        var after = (await Get($"/api/v1/ops/{waitingId}")).GetProperty("op");
        var ctx = after.GetProperty("context");
        Assert.Equal(Exporter, ctx.GetProperty("src").GetString());
        Assert.Equal(900, ctx.GetProperty("client_us").GetInt64());
        Assert.Equal(396, ctx.GetProperty("clientOverhead_us").GetInt64());
        Assert.Equal("GET /orders/{id}", ctx.GetProperty("uow").GetProperty("name").GetString());
        Assert.Equal(["Repo.Get", "Controller.Get"], ctx.GetProperty("frames").EnumerateArray().Select(f => f.GetProperty("fn").GetString()));
        Assert.Single((await Get("/api/v1/ops?uow=u-1")).GetProperty("items").EnumerateArray());

        var exporter = (await Get("/api/v1/producers")).GetProperty("items").EnumerateArray().Single(p => p.GetProperty("src").GetString() == Exporter);
        Assert.Equal("spyglass-dotnet", exporter.GetProperty("kind").GetString());
        Assert.Equal(1.0, exporter.GetProperty("join").GetProperty("rate").GetDouble());
    }
}
