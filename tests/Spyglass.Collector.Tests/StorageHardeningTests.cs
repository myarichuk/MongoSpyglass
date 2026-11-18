using System.Text;
using Microsoft.Data.Sqlite;
using Spyglass.Collector.Analysis;
using Spyglass.Collector.Ingest;
using Spyglass.Collector.Model;
using Spyglass.Collector.Pipeline;
using Spyglass.Collector.Storage;
using Spyglass.Collector.Util;
using static Spyglass.Collector.Tests.Records;

namespace Spyglass.Collector.Tests;

/// <summary>Storage writer retry/atomicity/bounds, SQLite indexes and retention, registry bounds.</summary>
public sealed class StorageHardeningTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "spyglass-hard-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static Operation Make(int id, string? coll = "orders", string? status = null)
    {
        Assert.Equal(ParseResult.Ok, RecordParser.Parse(Encoding.UTF8.GetBytes(Op(id, "find", coll: coll)), out var r, out _));
        return new Operation { Id = Ulid.New(T0 / 1000 + id), Op = (OpRecord)r!, ShapeHash = "s" };
    }

    /// <summary>A store whose atomic write fails a set number of times, then works (and counts what reached it).</summary>
    private sealed class Flaky(MemoryStore inner, int failures) : IOperationStore
    {
        public int Failures = failures, Attempts;
        public void Append(IReadOnlyList<Operation> ops) => inner.Append(ops);
        public Operation? Get(string id) => inner.Get(id);
        public List<Operation> Query(OpQuery q) => inner.Query(q);
        public void Update(IReadOnlyList<OpUpdate> updates) => inner.Update(updates);
        public int Prune(long olderThanUs, long maxBytes, Action? progress = null) => inner.Prune(olderThanUs, maxBytes, progress);
        public long Count => inner.Count;
        public void Transact(Action work)
        {
            Attempts++;
            if (Failures-- > 0) throw new IOException("disk hiccup"); // before any of it applies: a rolled back transaction
            work();
        }
    }

    private static StorageWriter Writer(IOperationStore ops, MemoryStore mem) =>
        new(ops, mem, mem, new CollectorOptions { IngestInsecure = true, Auth = AuthMode.None }, () => T0, new LiveHub(10));

    [Fact]
    public async Task A_failed_write_is_retried_whole_and_nothing_is_lost()
    {
        var mem = new MemoryStore();
        var flaky = new Flaky(mem, failures: 2);
        var w = Writer(flaky, mem);
        w.Start();
        w.Enqueue(Make(1));
        w.Enqueue(new CallsiteRecord { Callsite = "abc", Lang = "csharp", Frames = [("f", "a.cs", 1)] });
        await w.Barrier().WaitAsync(TimeSpan.FromSeconds(15));
        w.Complete();
        w.Join();
        Assert.Equal(1, mem.Count);
        Assert.NotNull(mem.Callsite("abc"));
        Assert.Equal(2, w.Retries);
        Assert.Equal(0, w.WriteDropped + w.CallsitesDropped);
        Assert.Equal(0, w.Backlog);
    }

    [Fact]
    public async Task A_write_that_never_succeeds_is_dropped_and_counted_not_retried_forever()
    {
        var mem = new MemoryStore();
        var flaky = new Flaky(mem, failures: int.MaxValue);
        var w = Writer(flaky, mem);
        w.Start();
        w.Enqueue(Make(1));
        w.Enqueue(Make(2));
        await w.Barrier().WaitAsync(TimeSpan.FromSeconds(30));
        w.Complete();
        w.Join();
        Assert.Equal(2, w.WriteDropped);
        Assert.Equal(StorageWriter.MaxAttempts, flaky.Attempts);
        Assert.Equal(0, w.Backlog);                                  // the backlog is released, not stuck
    }

    [Fact]
    public void Transact_rolls_back_appends_and_counters_when_a_later_step_fails()
    {
        using var store = new SqliteStore(_dir);
        Assert.Throws<InvalidOperationException>(() => store.Transact(() =>
        {
            store.Append([Make(1), Make(2)]);
            throw new InvalidOperationException("update failed");
        }));
        Assert.Equal(0, store.Count);
        Assert.Equal(0, store.Bytes);
        Assert.Empty(store.Query(new OpQuery()));
        store.Transact(() => store.Append([Make(1)]));
        Assert.Equal(1, store.Count);
        Assert.True(store.Bytes > 0);
    }

    [Fact]
    public void Failed_ops_are_kept_under_backpressure()
    {
        // ok:1 with a write error is a failure (03 §5.2 note 3): never sampled out
        var mem = new MemoryStore();
        var w = Writer(mem, mem); // not started: the backlog only grows
        var dup = Make(1);
        dup.Op.Reply = new ReplyFacts { WriteErrors = 1 };
        for (var i = 0; i < StorageWriter.Capacity + 5; i++) w.Enqueue(dup);
        Assert.Equal(StorageWriter.Capacity + 5, w.Backlog);         // every one kept past Capacity
        Assert.Equal(0, w.Sampled);
        w.Enqueue(Make(2));                                          // an ordinary op is sampled out
        Assert.Equal(1, w.Sampled);
    }

    [Fact]
    public void Ops_over_the_hard_capacity_are_dropped_and_counted_even_when_failed()
    {
        var mem = new MemoryStore();
        var w = Writer(mem, mem);
        var bad = Make(1);
        bad.Op.Reply = new ReplyFacts { WriteErrors = 1 };
        for (var i = 0; i < StorageWriter.HardCapacity + 10; i++) w.Enqueue(bad);
        Assert.Equal(StorageWriter.HardCapacity, w.Backlog);
        Assert.Equal(10, w.Overflowed);
    }

    private List<string> Plan(SqliteStore store, string sql)
    {
        store.Append([Make(1)]);
        using var c = new SqliteConnection($"Data Source={Path.Combine(_dir, "spyglass.db")};Mode=ReadOnly");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "EXPLAIN QUERY PLAN " + sql;
        var plan = new List<string>();
        using var rd = cmd.ExecuteReader();
        while (rd.Read()) plan.Add(rd.GetString(3));
        return plan;
    }

    [Fact]
    public void Status_and_latency_filters_can_use_their_partial_indexes()
    {
        using var store = new SqliteStore(_dir);
        // the exact WHERE terms Query emits
        Assert.Contains(Plan(store, "SELECT id FROM ops WHERE status = 'error' AND status <> 'ok' ORDER BY id DESC LIMIT 10"), p => p.Contains("ops_status"));
        // the index is still there for a selective threshold: Query falls back to it when the key walk does not fill the page (FC-9)
        Assert.Contains(Plan(store, "SELECT id FROM ops WHERE latency >= 100000 ORDER BY id DESC LIMIT 10"), p => p.Contains("ops_latency"));
        // and Query itself still answers both
        Assert.Empty(store.Query(new OpQuery { Status = "error" }));
        Assert.Empty(store.Query(new OpQuery { MinLatencyUs = 50_000_000_000 }));
        Assert.NotEmpty(store.Query(new OpQuery { Status = "ok" }));
    }

    [Fact]
    public void An_existing_database_gains_the_latency_index_on_open()
    {
        Directory.CreateDirectory(_dir);
        using (var old = new SqliteStore(_dir)) { }
        using (var c = new SqliteConnection($"Data Source={Path.Combine(_dir, "spyglass.db")}"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "DROP INDEX ops_latency";
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();
        using var store = new SqliteStore(_dir);
        using var c2 = new SqliteConnection($"Data Source={Path.Combine(_dir, "spyglass.db")};Mode=ReadOnly");
        c2.Open();
        using var q = c2.CreateCommand();
        q.CommandText = "SELECT count(*) FROM sqlite_master WHERE type = 'index' AND name = 'ops_latency'";
        Assert.Equal(1L, q.ExecuteScalar());
    }

    [Fact]
    public void Retention_returns_exact_counts_and_bytes()
    {
        using var store = new SqliteStore(_dir);
        store.Append([.. Enumerable.Range(0, 300).Select(i => Make(i))]);
        var before = store.Bytes;
        Assert.Equal(100, store.Prune(T0 + 100_000L, long.MaxValue));
        Assert.Equal(200, store.Count);
        Assert.True(store.Bytes < before && store.Bytes > 0);
        Assert.Equal(200, store.Prune(0, 0));                  // the size bound removes the rest
        Assert.Equal(0, store.Count);
        Assert.Equal(0, store.Bytes);
    }

    [Fact]
    public void Connections_expire_when_idle_and_are_capped_and_counted()
    {
        var reg = new ConnectionRegistry(idle: TimeSpan.FromMilliseconds(1), cap: 3);
        for (var i = 0; i < 5; i++) reg.Apply(new ConnRecord { Src = "p", ConnSeq = i, Member = "m", Event = "open", Ts = T0 });
        Assert.Equal(5, reg.Count);
        Thread.Sleep(20);
        reg.Sweep();
        Assert.Equal(0, reg.Count);
        Assert.Equal(5, reg.Evicted);

        var capped = new ConnectionRegistry(cap: 3);
        for (var i = 0; i < 5; i++) capped.Apply(new ConnRecord { Src = "p", ConnSeq = i, Member = "m", Event = "open", Ts = T0 });
        capped.Sweep();
        Assert.Equal(3, capped.Count);
        Assert.Equal(2, capped.Evicted);
        Assert.Equal(3, capped.DropProxy("p"));                      // the proxy's remaining connections go with it
        Assert.Equal(0, capped.Count);
    }

    [Fact]
    public void Producers_expire_when_idle_and_are_capped()
    {
        var reg = new ProducerRegistry(idle: TimeSpan.FromSeconds(10), cap: 2);
        reg.Touch("old", null, 1_000_000);
        reg.Touch("a", null, 100_000_000);
        reg.Touch("b", null, 100_000_001);
        reg.Touch("c", null, 100_000_002);
        var gone = reg.Sweep(100_000_003);
        Assert.Contains("old", gone);                                // idle
        Assert.Contains("a", gone);                                  // over the cap: least recently seen first
        Assert.Equal(2, reg.Count);
        Assert.Equal(2, reg.Expired);
    }

    [Fact]
    public void Interner_keeps_serving_repeated_names_after_high_cardinality_fills_it()
    {
        var i = new Utf8Interner(capacity: 64);
        for (var n = 0; n < 1000; n++) i.Get(Encoding.UTF8.GetBytes($"callsite{n:D8}"));
        var name = Encoding.UTF8.GetBytes("orders");
        var first = i.Get(name);
        Assert.Same(first, i.Get(name));                             // a late arrival is interned: the table did not lock up
        Assert.True(i.Count <= 64);
    }

    [Fact]
    public async Task WorkQueue_add_racing_complete_never_strands_an_item()
    {
        for (var round = 0; round < 200; round++)
        {
            var q = new WorkQueue<int>();
            var added = 0;
            var adder = Task.Run(() =>
            {
                try { q.Add(1); Interlocked.Increment(ref added); } catch (InvalidOperationException) { }
            });
            var taken = 0;
            var consumer = Task.Run(() =>
            {
                while (!q.IsCompleted) if (q.TryTake(out _, 1)) taken++;
            });
            q.CompleteAdding();
            await Task.WhenAll(adder, consumer).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(added, taken);
        }
    }

    [Fact]
    public void MemoryStore_prune_honours_the_size_bound_and_rolls_buckets_up()
    {
        var mem = new MemoryStore();
        mem.Append([.. Enumerable.Range(0, 100).Select(i => Make(i))]);
        Assert.Equal(100, mem.Prune(0, 0));                          // maxBytes 0: nothing may stay
        Assert.Equal(0, mem.Count);

        var old = new MinuteBucket { ShapeHash = "s", Minute = 1000 };
        old.Count = 3;
        mem.Merge([old]);
        mem.Rollup((1000L + 60 * 49) * 60_000_000);                  // 49 h later: minutes become hours
        var b = Assert.Single(mem.Buckets(0, long.MaxValue));
        Assert.Equal(3, b.Count);
        Assert.Equal(1000 / 60 * 60, b.Minute);
    }
}
