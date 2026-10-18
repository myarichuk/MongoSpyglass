using System.IO.Compression;
using System.Text;
using MongoDB.Bson;
using Spyglass.Collector.Analysis;
using Spyglass.Collector.Storage;
using static Spyglass.Collector.Tests.Records;

namespace Spyglass.Collector.Tests;

/// <summary>The pipeline suite against SQLite (03 §7 v1 store).</summary>
public sealed class SqlitePipelineTests : PipelineTests
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "spyglass-collector-" + Guid.NewGuid().ToString("N")[..8]);
    protected override object CreateStore() => new SqliteStore(_dir);
    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        ((IDisposable)Store).Dispose();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }
}

/// <summary>03 §1–§6 through <see cref="Collector.Ingest"/> with an in-memory store and a controllable clock.</summary>
public class PipelineTests : IAsyncLifetime
{
    private long _now = T0 + 1_000_000;
    private Collector _c = null!;
    private IOperationStore _store = null!;
    private IAggregateStore _agg = null!;

    protected virtual object CreateStore() => new MemoryStore();
    protected object Store = null!;

    public ValueTask InitializeAsync()
    {
        var o = new CollectorOptions { IngestInsecure = true, Auth = AuthMode.None, Data = null, Workers = 3 };
        var store = CreateStore();
        Store = store;
        _store = (IOperationStore)store;
        _agg = (IAggregateStore)store;
        _c = new Collector(o, _store, _agg, (IAlertStore)store, () => Volatile.Read(ref _now));
        _c.Start();
        return ValueTask.CompletedTask;
    }

    public virtual async ValueTask DisposeAsync() => await _c.DisposeAsync();

    private async Task<IngestResult> Send(params string[] lines)
    {
        var r = _c.Ingest(Batch(lines), null, "proxy");
        await _c.DrainAsync(TimeSpan.FromSeconds(5));
        await Until(() => _c.Writer.Backlog == 0);
        await Task.Delay(300); // the writer's batch interval
        return r;
    }

    private static async Task Until(Func<bool> cond)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!cond() && DateTime.UtcNow < deadline) await Task.Delay(10);
    }

    private Alert[] Alerts(string rule) => _c.Analysis.Alerts.Snapshot().Where(a => a.Rule == rule).ToArray();

    private async Task Housekeeping()
    {
        _c.Analysis.Tick();
        await Task.Delay(250);
    }

    [Fact]
    public async Task Op_is_stored_enriched_and_published_live()
    {
        using var sub = _c.Live.Subscribe(_ => true);
        var r = await Send(Conn(1, "orders-api"), Op(10, "find", new BsonDocument { ["find"] = "orders", ["filter"] = new BsonDocument("_id", 5) }, nReturned: 1));
        Assert.Equal(204, r.Status);
        Assert.Equal(2, r.Accepted);
        var op = Assert.Single(_store.Query(new OpQuery()));
        Assert.Equal("orders-api", op.App);
        Assert.Equal(500, op.ServerLatencyUs);
        Assert.Equal(26, op.Id.Length);
        Assert.True(sub.Reader.TryRead(out var ev));
        Assert.Equal("op.created", ev.Name);
        Assert.Contains("\"app\":\"orders-api\"", Encoding.UTF8.GetString(ev.Payload));
        var shape = Assert.Single(_agg.Shapes());
        Assert.Equal(op.ShapeHash, shape.ShapeHash);
        var bucket = Assert.Single(_agg.Buckets(0, long.MaxValue));
        Assert.Equal(1, bucket.Count);
    }

    [Fact]
    public async Task Ingest_status_codes_follow_02_5()
    {
        Assert.Equal(413, _c.Ingest(Batch(Enumerable.Range(0, 2001).Select(i => Op(i, "ping")).ToArray()), null, null).Status);
        Assert.Equal(413, _c.Ingest(new byte[Collector.MaxBatchBytes + 1], null, null).Status);
        Assert.Equal(400, _c.Ingest(Batch("garbage", "[1]"), null, null).Status);

        var mixed = _c.Ingest(Batch(Op(1, "ping"), """{"type":"op","ts":1,"src":"p"}""", "garbage"), null, null);
        Assert.Equal(204, mixed.Status);
        Assert.Equal(1, mixed.Accepted);
        Assert.Equal(2, mixed.Rejected);   // → X-Spyglass-Rejected: 2

        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true)) gz.Write(Batch(Op(2, "ping")));
        Assert.Equal(204, _c.Ingest(ms.ToArray(), "gzip", null).Status);
        await _c.DrainAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Queue_above_90_percent_answers_429()
    {
        var o = new CollectorOptions { IngestInsecure = true, Auth = AuthMode.None, QueueBytes = 4096, Workers = 1 };
        var store = new MemoryStore();
        await using var c = new Collector(o, store, store, store); // not started: nothing drains
        Assert.Equal(204, c.Ingest(Batch(Enumerable.Range(0, 20).Select(i => Op(i, "ping")).ToArray()), null, null).Status);
        Assert.Equal(429, c.Ingest(Batch(Op(99, "ping")), null, null).Status);
    }

    [Fact]
    public async Task Duplicates_are_suppressed()
    {
        var line = Op(10, "find");
        await Send(line);
        await Send(line); // the proxy retries a batch once after a 5xx (02 §5)
        Assert.Single(_store.Query(new OpQuery()));
        Assert.Equal(1, _c.Partitions.Sum(p => p.Duplicates));
    }

    [Fact]
    public async Task Redacted_command_with_a_body_is_rejected_and_future_ts_clamped()
    {
        // 03 §9: an auth command must carry only {$redacted: name}
        var bad = Op(1, "saslStart", new BsonDocument { ["saslStart"] = 1, ["payload"] = "secret" });
        var good = Op(2, "saslStart", new BsonDocument("$redacted", "saslStart"));
        var r = await Send(bad, good);
        Assert.Equal(1, r.Rejected);
        Assert.Equal(1, _c.RedactionRejected);

        await Send(Op(3, "find", ts: _now + (long)TimeSpan.FromMinutes(10).TotalMicroseconds));
        Assert.Equal(1, _c.ClockSkewed);
        Assert.Contains(_store.Query(new OpQuery()), o => o.Op.RequestId == 3 && o.Ts == _now);
    }

    [Fact]
    public async Task Cursor_lifecycle_across_connections()
    {
        var find = new BsonDocument { ["find"] = "orders", ["filter"] = new BsonDocument("s", "A") };
        await Send(Conn(1, "app"), Op(1, "find", find, nReturned: 101, cursorId: 99));
        Assert.Single(_c.Analysis.Cursors.Open);

        // getMore on another pooled connection (another serverConnectionId)
        var gm = new BsonDocument { ["getMore"] = 99L, ["collection"] = "orders" };
        await Send(Op(5, "getMore", gm, sid: 8, connSeq: 2, nReturned: 101, cursorId: 99));
        var c = Assert.Single(_c.Analysis.Cursors.Open);
        Assert.Equal(202, c.Documents);
        Assert.Equal(1, c.GetMores);
        var ops = _store.Query(new OpQuery());
        Assert.Equal(ops.Single(o => o.Op.Command == "find").ShapeHash, ops.Single(o => o.Op.Command == "getMore").ShapeHash); // 03 §4.3

        await Send(Op(6, "getMore", gm, sid: 9, connSeq: 3, nReturned: 7, cursorId: 0));
        Assert.Empty(_c.Analysis.Cursors.Open);
        Assert.Equal("exhausted", Assert.Single(_c.Analysis.Cursors.Closed).ClosedReason);
        Assert.Empty(Alerts("cursor-abandoned"));
    }

    [Fact]
    public async Task Abandoned_cursors_by_server_expiry_and_by_timeout()
    {
        var find = new BsonDocument { ["find"] = "events", ["filter"] = new BsonDocument() };
        await Send(Conn(1, "app"), Op(1, "find", find, coll: "events", cursorId: 501), Op(2, "find", find, coll: "events", cursorId: 502));
        await Send(Op(3, "getMore", new BsonDocument("getMore", 501L), coll: "events", status: "error", code: 43, codeName: "CursorNotFound"));
        Assert.Equal("server-expired", _c.Analysis.Cursors.Closed.Single().ClosedReason);
        Assert.Single(Alerts("cursor-abandoned"));

        _now += (long)TimeSpan.FromMinutes(11).TotalMicroseconds; // > cursorTimeout (10 min) idle
        await Housekeeping();
        Assert.Contains(_c.Analysis.Cursors.Closed, x => x.CursorId == 502 && x.ClosedReason == "timed-out");
        Assert.Equal(2, Alerts("cursor-abandoned").Single().Occurrences);
    }

    [Fact]
    public async Task Killed_cursor_is_not_abandoned()
    {
        await Send(Op(1, "find", nReturned: 2, cursorId: 77));
        await Send(Op(2, "killCursors", new BsonDocument { ["killCursors"] = "orders", ["cursors"] = new BsonArray { 77L } }));
        Assert.Equal("killed", Assert.Single(_c.Analysis.Cursors.Closed).ClosedReason);
        Assert.Empty(Alerts("cursor-abandoned"));
    }

    [Fact]
    public async Task Slow_large_unbounded_in_and_unacknowledged()
    {
        await Send(Conn(1, "app"),
            Op(1, "find", latencyUs: 150_000),                                                   // slow-shape (> 100 ms)
            Op(2, "find", new BsonDocument { ["find"] = "big", ["filter"] = new BsonDocument("x", 1) }, coll: "big", nReturned: 1500), // large-result
            Op(3, "find", new BsonDocument { ["find"] = "orders", ["filter"] = new BsonDocument("id", new BsonDocument("$in", new BsonArray(Enumerable.Range(0, 1001)))) }),
            Op(4, "insert", status: "unacknowledged"),
            Op(5, "find", latencyUs: 90_000));                                                    // not slow
        Assert.Single(Alerts("slow-shape"));
        Assert.Equal(1, Alerts("slow-shape")[0].Occurrences);
        Assert.Single(Alerts("large-result"));
        Assert.Single(Alerts("unbounded-in"));
        Assert.Equal(Severity.Info, Assert.Single(Alerts("unacknowledged-writes")).Severity);
        var slow = _store.Query(new OpQuery()).Single(o => o.Op.RequestId == 1);
        Assert.Contains("slow-shape", slow.Alerts!);
    }

    [Fact]
    public async Task Unbounded_scan_needs_a_collection_known_to_be_large()
    {
        var all = new BsonDocument { ["find"] = "orders", ["filter"] = new BsonDocument() };
        await Send(Conn(1, "app"), Op(1, "find", all, nReturned: 50));
        Assert.Empty(Alerts("unbounded-scan"));                   // collection not known to be large
        await Send(Op(2, "find", new BsonDocument { ["find"] = "orders", ["filter"] = new BsonDocument("a", 1) }, nReturned: 1200));
        await Send(Op(3, "find", all, nReturned: 101));
        Assert.Single(Alerts("unbounded-scan"));
        await Send(Op(4, "find", new BsonDocument { ["find"] = "orders", ["filter"] = new BsonDocument(), ["limit"] = 10 }));
        Assert.Equal(1, Alerts("unbounded-scan")[0].Occurrences); // a limit makes it bounded
    }

    [Fact]
    public async Task N_plus_one_suspected_needs_a_tight_loop_on_one_connection()
    {
        var lines = new List<string> { Conn(1, "app") };
        var ts = _now;
        for (var i = 0; i < 10; i++)
        {
            lines.Add(Op(100 + i, "find", new BsonDocument { ["find"] = "items", ["filter"] = new BsonDocument("orderId", i) }, coll: "items", ts: ts, latencyUs: 300));
            ts += 300 + 4 + 1_000; // next starts 1 ms after the previous reply
        }
        await Send([.. lines]);
        Assert.Single(Alerts("n-plus-one-suspected"));

        // the same ops with 20 ms think time between them: no tight loop
        lines = [Conn(2, "app2", sid: 8)];
        for (var i = 0; i < 10; i++)
        {
            lines.Add(Op(200 + i, "find", new BsonDocument { ["find"] = "items", ["filter"] = new BsonDocument("orderId", i) }, coll: "items", ts: ts, sid: 8, connSeq: 2, latencyUs: 300));
            ts += 20_000;
        }
        await Send([.. lines]);
        Assert.Single(Alerts("n-plus-one-suspected"));
    }

    [Fact]
    public async Task Repeated_query_same_values_within_a_second()
    {
        var q = new BsonDocument { ["find"] = "users", ["filter"] = new BsonDocument("_id", 42) };
        await Send(Conn(1, "app"), Op(1, "find", q, coll: "users", ts: _now), Op(2, "find", q, coll: "users", ts: _now + 100_000), Op(3, "find", q, coll: "users", ts: _now + 200_000));
        Assert.Single(Alerts("repeated-query"));
        var other = new BsonDocument { ["find"] = "users", ["filter"] = new BsonDocument("_id", 43) };
        await Send(Op(4, "find", other, coll: "users", ts: _now), Op(5, "find", other, coll: "users", ts: _now + 2_000_000), Op(6, "find", other, coll: "users", ts: _now + 4_000_000));
        Assert.Single(Alerts("repeated-query")); // spread over 4 s: no
    }

    [Fact]
    public async Task Error_burst_needs_rate_and_volume()
    {
        var lines = new List<string> { Conn(1, "app") };
        for (var i = 0; i < 19; i++) lines.Add(Op(i, "insert", status: i < 3 ? "error" : "ok", codeName: i < 3 ? "DuplicateKey" : null, code: i < 3 ? 11000 : null));
        await Send([.. lines]);
        Assert.Empty(Alerts("error-burst"));                       // 19 ops: under the ≥ 20 volume
        await Send(Op(50, "insert", status: "error", codeName: "DuplicateKey", code: 11000));
        var a = Assert.Single(Alerts("error-burst"));
        Assert.Equal("DuplicateKey", a.Scope["codeName"]);
    }

    [Fact]
    public async Task Alerts_resolve_when_quiet_reopen_and_mute()
    {
        await Send(Op(1, "find", latencyUs: 200_000));
        var a = Assert.Single(Alerts("slow-shape"));
        Assert.True(a.Open);

        _now += (long)TimeSpan.FromMinutes(11).TotalMicroseconds;
        await Housekeeping();
        Assert.False(Assert.Single(Alerts("slow-shape")).Open);

        await Send(Op(2, "find", latencyUs: 200_000, ts: _now));
        var reopened = Assert.Single(Alerts("slow-shape"));
        Assert.True(reopened.Open);
        Assert.Equal(2, reopened.Occurrences);

        Assert.True(await _c.Analysis.Invoke(() => _c.Analysis.Alerts.SetMuted(reopened.Id, true))); // the book is single-threaded
        await Send(Op(3, "find", latencyUs: 200_000, ts: _now));
        Assert.Equal(2, Assert.Single(Alerts("slow-shape")).Occurrences);
    }

    [Fact]
    public async Task Stream_replies_count_into_the_op()
    {
        await Send(Op(7, "getMore", new BsonDocument("getMore", 1L), stream: true, cursorId: 1));
        await Send(
            """{"type":"stream","ts":1,"src":"8a1e2b3c-0000-4000-8000-000000000001","member":"localhost:27017","serverConnectionId":7,"requestId":7,"seq":2,"respBytes":100,"reply":{"cursorId":"1","nReturned":5,"ok":1}}""",
            """{"type":"stream","ts":2,"src":"8a1e2b3c-0000-4000-8000-000000000001","member":"localhost:27017","serverConnectionId":7,"requestId":7,"seq":3,"respBytes":100,"reply":{"cursorId":"1","nReturned":5,"ok":1}}""");
        Assert.Equal(3, Assert.Single(_store.Query(new OpQuery())).ReplyCount);
    }
}
