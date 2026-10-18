using Spyglass.Collector.Model;
using Spyglass.Collector.Ingest;
using MongoDB.Bson;
using Spyglass.Collector.Analysis;
using Spyglass.Collector.Storage;
using static Spyglass.Collector.Tests.Records;

namespace Spyglass.Collector.Tests;

/// <summary>03 §7 SQLite specifics: restart, retention, the size bound, rollup, key-range time filters.</summary>
public sealed class StoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "spyglass-store-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static CollectorOptions Options() => new() { IngestInsecure = true, Auth = AuthMode.None, Workers = 2 };

    private static async Task Run(Collector c, params string[] lines)
    {
        c.Ingest(Batch(lines), null, null);
        await c.DrainAsync(TimeSpan.FromSeconds(5));
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (c.Writer.Backlog > 0 && DateTime.UtcNow < deadline) await Task.Delay(10);
        await Task.Delay(400);
    }

    [Fact]
    public async Task Ops_shapes_and_alerts_survive_a_restart()
    {
        string opId, alertId;
        var store = new SqliteStore(_dir);
        await using (var c = new Collector(Options(), store, store, store))
        {
            c.Start();
            await Run(c, Conn(1, "app"), Op(1, "find", new BsonDocument { ["find"] = "orders", ["filter"] = new BsonDocument("x", 1) }, latencyUs: 250_000));
            opId = Assert.Single(store.Query(new OpQuery())).Id;
            alertId = Assert.Single(c.Analysis.Alerts.Snapshot()).Id;
        }

        store.Dispose();
        using var reopened = new SqliteStore(_dir);
        await using var c2 = new Collector(Options(), reopened, reopened, reopened);
        var op = reopened.Get(opId)!;
        Assert.Equal("app", op.App);
        Assert.Equal("find", op.Op.Command);
        Assert.Equal(250_000, op.ServerLatencyUs);
        Assert.Contains("slow-shape", op.Alerts!);
        Assert.NotNull(op.Op.Cmd);                                   // raw line round-trips through the parser
        Assert.Equal(alertId, Assert.Single(c2.Analysis.Alerts.Snapshot()).Id); // restored into the book
        Assert.Single(reopened.Shapes());
        Assert.Single(reopened.Buckets(0, long.MaxValue));
    }

    [Fact]
    public async Task Query_filters_and_pages_newest_first()
    {
        using var store = new SqliteStore(_dir);
        await using var c = new Collector(Options(), store, store, store);
        c.Start();
        var lines = new List<string> { Conn(1, "a1"), Conn(2, "a2", sid: 8) };
        for (var i = 0; i < 30; i++)
            lines.Add(Op(i, i % 3 == 0 ? "insert" : "find", ts: T0 + i * 1000_000L, coll: i % 2 == 0 ? "even" : "odd",
                connSeq: i % 2 == 0 ? 1 : 2, sid: i % 2 == 0 ? 7 : 8, latencyUs: i * 1000));
        await Run(c, [.. lines]);

        Assert.Equal(30, store.Count);
        var page1 = store.Query(new OpQuery { Limit = 10 });
        Assert.Equal(Enumerable.Range(20, 10).Reverse(), page1.Select(o => o.Op.RequestId));
        var page2 = store.Query(new OpQuery { Limit = 10, Cursor = page1[^1].Id });
        Assert.Equal(Enumerable.Range(10, 10).Reverse(), page2.Select(o => o.Op.RequestId));
        Assert.All(store.Query(new OpQuery { Coll = "even", Limit = 100 }), o => Assert.Equal("even", o.Op.Coll));
        Assert.All(store.Query(new OpQuery { App = "a2", Limit = 100 }), o => Assert.Equal("a2", o.App));
        Assert.Equal(10, store.Query(new OpQuery { Command = "insert", Limit = 100 }).Count);
        Assert.Equal(5, store.Query(new OpQuery { MinLatencyUs = 25_000, Limit = 100 }).Count);
        var window = store.Query(new OpQuery { From = T0 + 5_000_000, To = T0 + 9_000_000, Limit = 100 });
        Assert.Equal([9, 8, 7, 6, 5], window.Select(o => o.Op.RequestId));
    }

    [Fact]
    public async Task Retention_and_size_bound_delete_oldest_first()
    {
        var store = new SqliteStore(_dir);
        await using (var c = new Collector(Options(), store, store, store))
        {
            c.Start();
            for (var b = 0; b < 10; b++)
                await Run(c, [.. Enumerable.Range(0, 200).Select(i => Op(b * 1000 + i, "find", ts: T0 + (b * 1000 + i) * 1000L,
                    cmd: new BsonDocument { ["find"] = "orders", ["filter"] = new BsonDocument("pad", new string('x', 400)) }))]);
        }
        Assert.Equal(2000, store.Count);

        // retention: everything before op 5000's ts
        Assert.Equal(1000, store.Prune(T0 + 5_000_000L, long.MaxValue));
        Assert.Equal(1000, store.Count);
        Assert.True(store.Query(new OpQuery { Limit = 1000 }).All(o => o.Op.RequestId >= 5000));

        // size bound: below the current live size → oldest deleted until it fits
        var bytes = store.Bytes;
        store.Prune(0, bytes / 2);
        Assert.True(store.Bytes <= bytes / 2, $"{store.Bytes} > {bytes / 2}");
        var kept = store.Query(new OpQuery { Limit = 1000 }).Select(o => o.Op.RequestId).ToList();
        Assert.InRange(kept.Count, 450, 550);                        // about half, not everything
        Assert.Equal(9199, kept.Max());                              // the newest survive (last batch: 9000..9199)
        store.Dispose();
    }

    /// <summary>Retention deletes in chunks, releasing the write lock between them (a day of ops is millions of rows).</summary>
    [Fact]
    public void Retention_runs_in_chunks()
    {
        using var store = new SqliteStore(_dir);
        var ops = new List<Operation>();
        for (var i = 0; i < 12_000; i++)
        {
            Assert.Equal(ParseResult.Ok, RecordParser.Parse(System.Text.Encoding.UTF8.GetBytes(Op(i, "find", ts: T0 + i * 1000L)), out var r, out _));
            ops.Add(new Operation { Id = Ulid.New((T0 + i * 1000L) / 1000), Op = (OpRecord)r!, ShapeHash = "s" });
        }
        store.Append(ops);
        var chunks = 0;
        Assert.Equal(11_000, store.Prune(T0 + 11_000_000L, long.MaxValue, () => chunks++));
        Assert.Equal(3, chunks);                                     // 5000 + 5000 + 1000
        Assert.Equal(1000, store.Count);
    }

    [Fact]
    public void Minute_buckets_roll_up_to_hours_after_48h()
    {
        using var store = new SqliteStore(_dir);
        var now = 1_800_000_000_000_000L;                           // µs
        var nowMinute = now / 60_000_000;
        MinuteBucket B(long minute, long count) { var b = new MinuteBucket { ShapeHash = "s", Minute = minute, Count = count }; b.Latency.Add(1000); return b; }
        var oldHour = (nowMinute - 50 * 60) / 60;
        store.Merge([B(oldHour * 60, 3), B(oldHour * 60 + 1, 4), B(nowMinute - 5, 1)]);
        store.Merge([B(nowMinute - 5, 2)]);                          // deltas add
        store.Rollup(now);
        var buckets = store.Buckets(0, long.MaxValue, "s");
        Assert.Equal(2, buckets.Count);
        Assert.Contains(buckets, b => b.Minute == oldHour * 60 && b.Count == 7 && b.Latency.Count == 2); // hourly row
        Assert.Contains(buckets, b => b.Minute == nowMinute - 5 && b.Count == 3);
    }
}
