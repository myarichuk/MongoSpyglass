using System.Text;
using MongoDB.Bson;
using Spyglass.Collector.Analysis;
using Spyglass.Collector.Ingest;
using Spyglass.Collector.Model;
using Spyglass.Collector.Pipeline;
using Spyglass.Collector.Storage;
using static Spyglass.Collector.Tests.Records;

namespace Spyglass.Collector.Tests;

/// <summary>A <see cref="MemoryStore"/> whose aggregate and alert writes fail on demand: a full disk, a corrupt page.</summary>
internal sealed class FaultyStore : IOperationStore, IAggregateStore, IAlertStore, ICallsiteStore
{
    public readonly MemoryStore Inner = new();
    public volatile bool Fail, FailPrune;
    public int Failures;

    private void Check()
    {
        if (!Fail) return;
        Interlocked.Increment(ref Failures);
        throw new IOException("disk full");
    }

    public void Append(IReadOnlyList<Operation> ops) => Inner.Append(ops);
    public Operation? Get(string id) => Inner.Get(id);
    public List<Operation> Query(OpQuery q) => Inner.Query(q);
    public void Update(IReadOnlyList<OpUpdate> updates) => Inner.Update(updates);
    public int Prune(long olderThanUs, long maxBytes, Action? progress = null) => Inner.Prune(olderThanUs, maxBytes, progress);
    public long Count => Inner.Count;
    public void Merge(IReadOnlyList<MinuteBucket> deltas) { Check(); Inner.Merge(deltas); }
    public void UpsertShapes(IReadOnlyList<ShapeInfo> shapes) { Check(); Inner.UpsertShapes(shapes); }
    public List<MinuteBucket> Buckets(long fromMinute, long toMinute, string? shape = null) => Inner.Buckets(fromMinute, toMinute, shape);
    public ShapeInfo? Shape(string hash) => Inner.Shape(hash);
    public List<ShapeInfo> Shapes() => Inner.Shapes();
    public int PruneShapes(long lastSeenBeforeUs)
    {
        if (FailPrune) throw new IOException("prune failed");
        return Inner.PruneShapes(lastSeenBeforeUs);
    }
    public void Rollup(long nowUs) => Inner.Rollup(nowUs);
    public void Upsert(IReadOnlyList<Alert> alerts) { Check(); Inner.Upsert(alerts); }
    public List<Alert> All() => Inner.All();
    public void Delete(IReadOnlyCollection<string> ids) { Check(); Inner.Delete(ids); }
    public void Upsert(IReadOnlyList<CallsiteRecord> callsites, long nowUs) => Inner.Upsert(callsites, nowUs);
    public void Touch(IReadOnlyCollection<string> hashes, long nowUs) => Inner.Touch(hashes, nowUs);
    public CallsiteRecord? Callsite(string hash) => Inner.Callsite(hash);
    public int PruneCallsites(long unusedSinceUs) => Inner.PruneCallsites(unusedSinceUs);
}

/// <summary>The analysis stage's robustness and bounds: collector-ingest CI-1/2/5/9/14/18/22, collector-analysis CA-1/2/5/6/7/8/11/15/16/19/21.</summary>
public sealed class AnalysisRobustnessTests : IAsyncLifetime
{
    private long _now = T0 + 1_000_000;
    private readonly FaultyStore _store = new();
    private Collector _c = null!;

    public ValueTask InitializeAsync()
    {
        var o = new CollectorOptions { IngestInsecure = true, Auth = AuthMode.None, Data = null, Workers = 2 };
        _c = new Collector(o, _store, _store, _store, () => Volatile.Read(ref _now), _store);
        _c.Start();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await _c.DisposeAsync();

    private async Task Send(params string[] lines)
    {
        _c.Ingest(Batch(lines), null, "proxy");
        await _c.DrainAsync(TimeSpan.FromSeconds(5));
        await Until(() => _c.Writer.Backlog == 0);
        await Task.Delay(300); // the writer's batch interval
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

    private static Operation Parse(string line, string shape = "s")
    {
        Assert.Equal(ParseResult.Ok, RecordParser.Parse(Encoding.UTF8.GetBytes(line), out var r, out _));
        return new Operation { Id = Ulid.New(((OpRecord)r!).Ts / 1000), Op = (OpRecord)r!, ShapeHash = shape };
    }

    [Fact]
    public void Skew_window_survives_billions_of_samples()
    {
        // CA-1: a running total as the index wrapped negative after 2^31 joins
        var w = new AnalysisStage.SkewWindow();
        long? last = null;
        for (var i = 0; i < 100_000; i++) if (w.Add(i % 2 == 0 ? 10 : 20) is { } m) last = m;
        Assert.Contains(last, new long?[] { 10, 20 });
        long? median = null;
        for (var i = 0; i < 64 + 16; i++) median = w.Add(5) ?? median;
        Assert.Equal(5, median);
    }

    [Fact]
    public async Task A_failing_store_does_not_end_the_analysis_thread_and_nothing_is_lost()
    {
        // CI-1: Flush and Housekeeping wrote to the store with no guard, and an unhandled exception ends a .NET process
        _store.Fail = true;
        await Send(Op(1, "find"));
        await Housekeeping();
        Assert.True(_c.Analysis.Alive);
        Assert.True(_store.Failures > 0);
        Assert.Empty(_store.Buckets(0, long.MaxValue));
        Assert.Contains(_c.Health().Checks, c => c.Name == "storage" && c.Detail.Contains("disk full"));

        _store.Fail = false;
        await Send(Op(2, "find"));
        await Housekeeping();
        var b = Assert.Single(_store.Buckets(0, long.MaxValue));
        Assert.Equal(2, b.Count);                                        // the failed flush's delta came back with the next one
        Assert.Single(_store.Shapes());
        Assert.Contains(_c.Health().Checks, c => c.Name == "storage" && c.Detail == "writes succeeding");
    }

    [Fact]
    public async Task An_op_whose_analysis_throws_is_still_stored_and_counted()
    {
        // CA-8: a throwing live filter (any exception in a rule) skipped Finalize and the op was never written
        using var sub = _c.Live.Subscribe(_ => throw new InvalidOperationException("filter bug"));
        await Send(Op(1, "find"), Op(2, "find"));
        Assert.Equal(2, _store.Query(new OpQuery()).Count);
        Assert.True(_c.Analysis.Errors >= 2);
        Assert.NotEmpty(_store.Shapes()); // the shape is in the catalog although the op's analysis failed
    }

    [Fact]
    public async Task Stream_records_of_one_read_are_not_duplicates_of_each_other()
    {
        // CI-2: the dedup key had no seq, so records sharing a microsecond (one socket read) were dropped
        await Send(Op(7, "getMore", new BsonDocument("getMore", 1L), stream: true, cursorId: 1));
        string Stream(int seq) =>
            $$$"""{"type":"stream","ts":{{{T0 + 5}}},"src":"{{{Records.Proxy}}}","member":"localhost:27017","serverConnectionId":7,"requestId":7,"seq":{{{seq}}},"respBytes":100,"reply":{"cursorId":"1","nReturned":5,"ok":1}}""";
        await Send(Stream(2), Stream(3), Stream(4));
        Assert.Equal(4, Assert.Single(_store.Query(new OpQuery())).ReplyCount);
        await Send(Stream(3)); // a real resend
        Assert.Equal(4, Assert.Single(_store.Query(new OpQuery())).ReplyCount);
        Assert.Equal(1, _c.Partitions.Sum(p => p.Duplicates));
    }

    [Fact]
    public async Task An_active_stream_is_not_evicted_by_newer_ones_and_a_finished_one_leaves()
    {
        // CI-22 / CA-16: the map evicted by insertion order after 10,000 newer streams, so a long-lived one went deaf
        await Send(Op(1, "find", stream: true, cursorId: 5, nReturned: 1));
        async Task Fill(int from, int n)
        {
            for (var i = from; i < from + n; i += 1000)
                _c.Ingest(Batch(Enumerable.Range(i, Math.Min(1000, from + n - i)).Select(k => Op(k, "hello", stream: true, sid: 100 + k, connSeq: 100 + k)).ToArray()), null, "proxy");
            await _c.DrainAsync(TimeSpan.FromSeconds(30));
        }
        string Stream(long cursor, int n) =>
            $$$"""{"type":"stream","ts":{{{T0 + 5}}},"src":"{{{Records.Proxy}}}","member":"localhost:27017","serverConnectionId":7,"requestId":1,"seq":{{{n}}},"respBytes":100,"reply":{"cursorId":"{{{cursor}}}","nReturned":5,"ok":1}}""";
        await Fill(1000, 6000);
        await Send(Stream(5, 2));                    // activity: moves it to the young end
        await Fill(8000, 6000);                      // 12,000 streams in all since it opened: past the 10,000 cap
        await Send(Stream(5, 3));
        Assert.Equal(3, _store.Query(new OpQuery { Command = "find" }).Single().ReplyCount);
        await Send(Stream(0, 4));                    // the last batch: closes the cursor, with its documents
        var closed = Assert.Single(_c.Analysis.Cursors.Closed);
        Assert.Equal("exhausted", closed.ClosedReason);
        Assert.Equal(1 + 5 + 5 + 5, closed.Documents); // the opening batch and three stream batches, the last one included
        Assert.Equal(0, closed.GetMores);            // no getMore was sent for stream batches
    }

    [Fact]
    public async Task A_producer_clock_far_behind_is_clamped_and_its_alerts_do_not_flap()
    {
        // CI-9: a negative ts became an id that sorts after every real one; CA-5: event time compared with the collector's clock
        await Send(Op(1, "find", ts: -5_000_000), Op(2, "find", ts: 5));
        Assert.Equal(2, _c.ClockSkewed);
        Assert.All(_store.Query(new OpQuery()), o => { Assert.Equal(_now, o.Ts); Assert.StartsWith("0", o.Id); });

        // 30 minutes behind (inside the retention, so kept as it is), on a slow shape: the quiet period runs on the collector's clock
        await Send(Op(3, "find", latencyUs: 200_000, ts: _now - (long)TimeSpan.FromMinutes(30).TotalMicroseconds));
        var a = Assert.Single(Alerts("slow-shape"));
        Assert.True(a.Open);
        _now += 5_000_000;
        await Housekeeping();
        Assert.True(Assert.Single(Alerts("slow-shape")).Open);
        _now += (long)TimeSpan.FromMinutes(11).TotalMicroseconds;
        await Housekeeping();
        Assert.False(Assert.Single(Alerts("slow-shape")).Open);
    }

    [Fact]
    public async Task Cursors_of_a_lagging_producer_are_not_timed_out_at_once()
    {
        // CA-5: idle time came from the producer's ts
        var lag = _now - (long)TimeSpan.FromMinutes(20).TotalMicroseconds;
        await Send(Op(1, "find", nReturned: 2, cursorId: 77, ts: lag));
        _now += 5_000_000;
        await Housekeeping();
        Assert.Empty(_c.Analysis.Cursors.Closed);
        Assert.Single(_c.Analysis.OpenCursors);
        _now += (long)TimeSpan.FromMinutes(11).TotalMicroseconds;
        await Housekeeping();
        Assert.Equal("timed-out", Assert.Single(_c.Analysis.ClosedCursors).ClosedReason);
    }

    [Fact]
    public async Task Error_burst_survives_ops_interleaving_across_a_minute_boundary()
    {
        // CA-19: a tumbling window reset on every switch between two minutes
        var m0 = (T0 / 60_000_000 - 3) * 60_000_000;
        var lines = new List<string>();
        for (var i = 0; i < 40; i++)
            lines.Add(Op(i, "insert", ts: i % 2 == 0 ? m0 + 1000 : m0 + 60_000_000 + 1000, status: i is 2 or 4 or 6 or 8 ? "error" : "ok",
                codeName: i is 2 or 4 or 6 or 8 ? "DuplicateKey" : null, code: i is 2 or 4 or 6 or 8 ? 11000 : null));
        await Send([.. lines]);
        Assert.Single(Alerts("error-burst"));
    }

    [Fact]
    public async Task Resolved_alerts_and_unused_shapes_are_deleted_from_the_store_but_a_muted_alert_stays()
    {
        // CI-5: nothing was ever deleted from the alerts and shapes tables; CA-21: a muted alert that expired lost its mute
        await Send(Op(1, "find", latencyUs: 200_000), Op(2, "insert", latencyUs: 200_000));
        var alerts = Alerts("slow-shape");
        Assert.Equal(2, alerts.Length);
        Assert.True(await _c.Analysis.Invoke(() => _c.Analysis.Alerts.SetMuted(alerts[0].Id, true)));
        await Housekeeping();
        Assert.Equal(2, _store.All().Count);
        Assert.Equal(2, _store.Shapes().Count);

        _now += (long)TimeSpan.FromMinutes(11).TotalMicroseconds; // resolves
        await Housekeeping();
        _now += (long)TimeSpan.FromDays(31).TotalMicroseconds;     // past both retentions
        await Housekeeping();
        await Housekeeping();
        var left = Assert.Single(_store.All());
        Assert.Equal(alerts[0].Id, left.Id);
        Assert.True(left.Muted);
        Assert.Empty(_store.Shapes());
        Assert.Empty(_c.Analysis.Aggregates.Shapes);
        Assert.Equal(2, _c.Analysis.ShapesEvicted);
        Assert.Single(Alerts("slow-shape"));
    }

    [Fact]
    public void Expiring_alerts_and_shapes_is_bounded_in_memory_too()
    {
        var book = new AggregateBook();
        for (var i = 0; i < 5; i++)
        {
            var op = Parse(Op(i, "find", ts: T0 + i), shape: "h" + i);
            book.Add(op, T0);
        }
        Assert.Equal(5, book.Shapes.Count);
        Assert.Equal(["h0", "h1"], book.Expire(T0 + (long)TimeSpan.FromDays(30).TotalMicroseconds + 2, TimeSpan.FromDays(30)).Order());
        Assert.Equal(3, book.Shapes.Count);
    }

    [Fact]
    public void Slowest_examples_decay_with_the_op_retention()
    {
        // CA-7: the all-time top 10 kept an old incident for ever and then resolved to no op at all
        var book = new AggregateBook(TimeSpan.FromHours(24));
        book.Add(Parse(Op(1, "find", latencyUs: 5_000_000, ts: T0)), T0);
        var later = T0 + (long)TimeSpan.FromHours(25).TotalMicroseconds;
        book.Add(Parse(Op(2, "find", latencyUs: 300_000, ts: later)), later);
        Assert.Equal(300_000, Assert.Single(book.Shapes["s"].Slowest).LatencyUs);
    }

    [Fact]
    public void Sketch_keeps_long_latencies_and_never_reports_above_the_max()
    {
        // CA-15
        var s = new Sketch();
        s.Add(120_000_000);
        Assert.InRange(s.Quantile(0.99), 120_000_000 * 0.98, 120_000_000);
        var one = new Sketch();
        one.Add(300);
        Assert.Equal(300, one.Quantile(1), 6);
    }

    [Fact]
    public void Open_cursors_are_evicted_least_recently_active_first_in_constant_time()
    {
        // CA-11
        var reg = new CursorRegistry();
        for (var i = 1; i <= 100_000; i++) reg.Opened("p", "m", i, "op", "shape", null, "db", "c", i, 1);
        reg.Touched("p", "m", 1, 200_000, 1);
        reg.Opened("p", "m", 100_001, "op", "shape", null, "db", "c", 200_001, 1);
        Assert.NotNull(reg.Get("p", "m", 1));
        Assert.Null(reg.Get("p", "m", 2));
        Assert.Equal(100_000, reg.Open.Count());
        reg.Opened("p", "m", 100_001, "op", "shape", null, "db", "c", 200_002, 1); // the same id again: replaced, nobody else evicted
        Assert.NotNull(reg.Get("p", "m", 3));
    }

    [Fact]
    public void A_getMore_with_maxTimeMS_waits_by_design_even_for_a_cursor_never_seen_open()
    {
        // CA-6
        var gm = new BsonDocument { ["getMore"] = 5L, ["collection"] = "orders", ["maxTimeMS"] = 1000, ["$db"] = "shop" }.ToBson();
        var plain = new BsonDocument { ["getMore"] = 5L, ["collection"] = "orders", ["$db"] = "shop" }.ToBson();
        Assert.True(Partition.AwaitsByDesign("getMore", gm));
        Assert.False(Partition.AwaitsByDesign("getMore", plain));
        Assert.False(Partition.AwaitsByDesign("find", gm));
    }

    [Fact]
    public async Task A_killCursors_for_a_cursor_the_server_had_dropped_is_abandoned_not_killed()
    {
        // CA-17
        await Send(Op(1, "find", nReturned: 2, cursorId: 78));
        var kill = Op(2, "killCursors", new BsonDocument { ["killCursors"] = "orders", ["cursors"] = new BsonArray { 78L } })
            .Replace("\"ok\":1", "\"ok\":1,\"cursorsNotFound\":1");
        await Send(kill);
        Assert.Equal("server-expired", Assert.Single(_c.Analysis.Cursors.Closed).ClosedReason);
        Assert.Single(Alerts("cursor-abandoned"));
    }

    [Fact]
    public void Join_eviction_does_not_take_a_newer_cmd_with_a_stale_queue_entry()
    {
        // CI-14: a stale entry in the order queue removed the newer cmd under the same key and left the gauge high
        var counters = new JoinCounters();
        var producers = new ProducerRegistry();
        var engine = new JoinEngine(counters, producers, 10, 1, 1);
        Operation Op(JoinKey k) => new()
        {
            Id = Ulid.New(1),
            Op = new OpRecord { Src = k.ProxyId, Member = k.Member, ServerConnectionId = k.ServerConnectionId, RequestId = k.RequestId, Command = "find", Db = "shop", Status = "ok" },
        };
        CmdRecord Cmd(JoinKey k) => new() { Src = "e", ProxyId = k.ProxyId, Member = k.Member, ServerConnectionId = k.ServerConnectionId, RequestId = k.RequestId, Command = "find", Db = "shop" };
        var a = new JoinKey("p", "m", 1, 1);
        var b = new JoinKey("p", "m", 1, 2);
        var o = new PartitionOutput();
        var a1 = Cmd(a);
        engine.OnCmd(a1, a, 0, o);                        // waits
        engine.OnOp(Op(a), 1, 0, false, o);               // joins it: its order entry stays behind
        engine.OnOp(Op(b), 2, 0, false, o);               // the 1-slot finalized ring forgets a
        var a2 = Cmd(a);
        engine.OnCmd(a2, a, 3, o);                        // the same key again: waits (its own entry behind the stale one)
        Assert.Equal(1, counters.PendingCmds);
        var c = new JoinKey("p", "m", 1, 3);
        engine.OnCmd(Cmd(c), c, 4, o);                    // full: evicts a2 (found behind a1's stale entry), nothing else
        Assert.Equal(1, counters.OrphanExpired);
        Assert.Equal(1, counters.PendingCmds);
        Assert.Equal(1, engine.WaitingCmds);
    }

    [Fact]
    public async Task Connection_rule_state_is_dropped_when_the_connection_closes()
    {
        // CI-18: ConnState kept its valueHashes for connections that were gone
        await Send(Conn(1, "app"), Op(1, "find", new BsonDocument { ["find"] = "orders", ["filter"] = new BsonDocument("a", 1) }, connSeq: 1));
        var state = typeof(Partition).GetField("_state", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        int Count() => _c.Partitions.Sum(p => ((System.Collections.ICollection)state.GetValue(p)!).Count);
        Assert.Equal(1, Count());
        await Send(Conn(1, "app", ev: "close"));
        _now += 11_000_000; // the partitions' clock is the injected one: past the sweep interval
        await Task.Delay(300);
        Assert.Equal(0, Count());
    }
}
