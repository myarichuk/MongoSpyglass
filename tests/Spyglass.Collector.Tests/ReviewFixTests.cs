using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MongoDB.Bson;
using Spyglass.Collector.Analysis;
using Spyglass.Collector.Ingest;
using Spyglass.Collector.Model;
using Spyglass.Collector.Pipeline;
using Spyglass.Collector.Storage;
using Spyglass.Collector.Ui.Services;
using static Spyglass.Collector.Tests.Records;

namespace Spyglass.Collector.Tests;

/// <summary>Regression tests for the collector/UI review (FC-1 … FC-21).</summary>
public sealed class ReviewFixTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "spyglass-fc-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static StringContent Json(string j) => new(j, Encoding.UTF8, "application/json");

    private static Task<Server> StartPassword(IReadOnlyList<IPNetwork>? trusted = null)
    {
        var o = new CollectorOptions { Bind = new IPEndPoint(IPAddress.Loopback, 0), Token = "t", Auth = AuthMode.Password, AdminPassword = "pw", Data = null, Workers = 1 };
        if (trusted != null) o.TrustedProxies = trusted;
        return Server.StartAsync(o);
    }

    private static async Task<HttpStatusCode> LoginFrom(HttpClient http, string password, string? xff = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/login") { Content = Json($$"""{"password":"{{password}}"}""") };
        if (xff != null) req.Headers.Add("X-Forwarded-For", xff);
        return (await http.SendAsync(req)).StatusCode;
    }

    // ---- FC-1, FC-2, FC-19 ----------------------------------------------------------------

    [Fact]
    public async Task The_login_pages_mode_probe_is_not_a_login_attempt()
    {
        await using var s = await StartPassword();
        using var http = new HttpClient { BaseAddress = s.BaseAddress };
        for (var i = 0; i < 20; i++)
        {
            var r = await http.GetAsync("/api/v1/auth/mode");
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            Assert.Equal("password", JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.GetProperty("auth").GetString());
        }
        Assert.Equal(HttpStatusCode.OK, await LoginFrom(http, "pw"));
    }

    [Fact]
    public async Task One_clients_failures_never_lock_another_client_behind_a_trusted_proxy()
    {
        await using var s = await StartPassword(); // loopback (this test's peer) is the proxy
        using var http = new HttpClient { BaseAddress = s.BaseAddress };
        var last = HttpStatusCode.OK;
        for (var i = 0; i < 8 && last != HttpStatusCode.TooManyRequests; i++) last = await LoginFrom(http, "no", "203.0.113.9");
        Assert.Equal(HttpStatusCode.TooManyRequests, last);
        Assert.Equal(HttpStatusCode.TooManyRequests, await LoginFrom(http, "pw", "203.0.113.9"));
        // another client, however it spells its header; the proxy appends the real peer on the right, so what a client puts to the left is not believed
        Assert.Equal(HttpStatusCode.OK, await LoginFrom(http, "pw", "203.0.113.10"));
        Assert.Equal(HttpStatusCode.TooManyRequests, await LoginFrom(http, "pw", "203.0.113.10, 203.0.113.9"));
        Assert.Equal(HttpStatusCode.OK, await LoginFrom(http, "pw", "203.0.113.9, 203.0.113.11"));
    }

    [Fact]
    public async Task X_forwarded_for_is_ignored_from_an_untrusted_peer()
    {
        await using var s = await StartPassword([new IPNetwork(IPAddress.Parse("10.0.0.0"), 8)]); // the peer (127.0.0.1) is not the proxy
        using var http = new HttpClient { BaseAddress = s.BaseAddress };
        var last = HttpStatusCode.OK;
        for (var i = 0; i < 8 && last != HttpStatusCode.TooManyRequests; i++) last = await LoginFrom(http, "no", $"198.51.100.{i + 1}"); // a rotating spoof does not dodge the limit
        Assert.Equal(HttpStatusCode.TooManyRequests, last);
    }

    [Fact]
    public async Task A_locked_basic_client_gets_retry_after_and_a_chunked_login_body_over_the_cap_is_413()
    {
        await using var s = await StartPassword();
        using var http = new HttpClient { BaseAddress = s.BaseAddress };
        HttpResponseMessage resp = null!;
        for (var i = 0; i < 8; i++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "/api/v1/whoami");
            req.Headers.Add("Authorization", "Basic " + Convert.ToBase64String("u:bad"u8));
            req.Headers.Add("X-Forwarded-For", "198.51.100.77");
            resp = await http.SendAsync(req);
        }
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.True(resp.Headers.Contains("Retry-After"));

        using var big = new HttpRequestMessage(HttpMethod.Post, "/api/v1/login") { Content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes("{\"password\":\"" + new string('x', 100_000) + "\"}"))) };
        big.Content.Headers.ContentType = new("application/json");
        big.Headers.TransferEncodingChunked = true;
        big.Headers.Add("X-Forwarded-For", "198.51.100.78");
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await http.SendAsync(big)).StatusCode);
    }

    // ---- FC-3, FC-4, FC-17 -----------------------------------------------------------------

    [Fact]
    public void Shell_values_are_validated_before_they_are_emitted_as_code()
    {
        var injected = MongoShell.RunCommand("shop", """{"insert":"a","documents":[{"x":{"$numberInt":"1}]}); db.getSiblingDB('shop').dropDatabase(); ({a:[{b:1"}}]}""");
        Assert.NotNull(injected);
        Assert.DoesNotContain("db.getSiblingDB('shop')", injected); // the attacker's statement stays inside a quoted string
        Assert.Contains("\"$numberInt\": \"1}]}", injected); // an ordinary (quoted) document
        foreach (var bad in new[]
                 {
                     """{"$numberDouble":"1); evil("}""", """{"$date":{"$numberLong":"1); evil("}}""", """{"$numberLong":"x"}""", """{"$oid":"zz"}""",
                     """{"$timestamp":{"t":"1); evil(","i":1}}""", """{"$binary":{"base64":"AQID","subType":"00); evil("}}""", """{"$date":"1); evil("}""",
                 })
        {
            var s = MongoShell.RunCommand("shop", "{\"find\":\"a\",\"filter\":{\"x\":" + bad + "}}");
            Assert.NotNull(s);
            Assert.Matches("\"\\$\\w+\": ", s); // the wrapper key survives as an ordinary quoted field
        }
        Assert.Contains("NumberLong(\"9\")", MongoShell.RunCommand("shop", """{"find":"a","filter":{"x":{"$numberLong":"9"}}}"""));
        Assert.Contains("\"x\": 7", MongoShell.RunCommand("shop", """{"find":"a","filter":{"x":{"$numberInt":"7"}}}"""));
        Assert.Contains("\"x\": Infinity", MongoShell.RunCommand("shop", """{"find":"a","filter":{"x":{"$numberDouble":"Infinity"}}}"""));
    }

    [Fact]
    public void Shell_never_throws_for_an_oversized_binary_subtype_or_a_malformed_wrapper()
    {
        var s = MongoShell.RunCommand("shop", """{"find":"a","filter":{"x":{"$binary":{"base64":"","subType":"1234567890"}}}}""");
        Assert.NotNull(s);
        Assert.DoesNotContain("BinData(", s);
        Assert.NotNull(MongoShell.RunCommand("shop", """{"find":"a","x":{"$binary":{"base64":"!!","subType":"zz"}}}"""));
        Assert.NotNull(MongoShell.RunCommand("shop", """{"find":"a","x":{"$regularExpression":{"pattern":1}}}"""));
        Assert.NotNull(MongoShell.RunCommand("shop", """{"find":"a","x":{"$timestamp":5}}"""));
    }

    // ---- FC-5, FC-14, FC-21 (_muted) ----------------------------------------------------------

    [Fact]
    public async Task A_mute_reaches_the_store_at_once_and_a_muted_alerts_counts_follow_it()
    {
        long now = T0 + 1_000_000;
        var store = new MemoryStore();
        var o = new CollectorOptions { IngestInsecure = true, Auth = AuthMode.None, Data = null, Workers = 1 };
        var c = new Collector(o, store, store, store, () => Volatile.Read(ref now), store);
        c.Start();
        c.Ingest(Batch(Op(1, "find", latencyUs: 200_000)), null, "proxy");
        await c.DrainAsync(TimeSpan.FromSeconds(5));
        var a = c.Analysis.Alerts.Snapshot().Single(x => x.Rule == "slow-shape");
        Assert.True(await c.Analysis.Invoke(() => c.Analysis.Mute(a.Id, true)));
        c.Analysis.Tick();
        await Task.Delay(400);
        Assert.True(store.All().Single(x => x.Id == a.Id).Muted); // before the alert ever resolves
        for (var i = 2; i < 6; i++) // it keeps firing while muted
        {
            Volatile.Write(ref now, now + 1_000_000);
            c.Ingest(Batch(Op(i, "find", latencyUs: 200_000)), null, "proxy");
            await c.DrainAsync(TimeSpan.FromSeconds(5));
        }
        await c.DisposeAsync(); // final flush
        var stored = store.All().Single(x => x.Id == a.Id);
        Assert.True(stored.Muted);
        Assert.Equal(5, stored.Occurrences);
        Assert.Equal(5, c.Analysis.Alerts.Snapshot().Single(x => x.Id == a.Id).Occurrences);
    }

    [Fact]
    public void A_muted_hit_does_not_rebuild_the_summary()
    {
        var book = new AlertBook();
        var scope = new Dictionary<string, string?>();
        book.Hit("slow-shape", "s", scope, "first", "op1", 1_000, 1_000);
        Assert.True(book.SetMuted(book.Find("slow-shape", "s")!.Id, true));
        Assert.Null(book.Hit("slow-shape", "s", scope, "second", "op2", 2_000, 2_000));
        Assert.Equal("first", book.Find("slow-shape", "s")!.Summary); // not replaced: callers skip building it
    }

    // ---- FC-6 -----------------------------------------------------------------------------

    [Fact]
    public async Task An_idle_open_connection_keeps_its_attribution()
    {
        var reg = new ConnectionRegistry();
        reg.Apply(new ConnRecord { Src = "p", ConnSeq = 1, Member = "m", Event = "open", Ts = T0, App = "orders-api", Driver = "node" });
        await Task.Delay(30);
        reg.Sweep();
        Assert.Equal("orders-api", reg.Get("p", 1)?.App);
    }

    // ---- FC-7, FC-11 ---------------------------------------------------------------------------

    private sealed class Rig : IAsyncDisposable
    {
        public long Now = T0 + 1_000_000;
        public readonly FaultyStore Store = new();
        public readonly Collector C;
        public Rig()
        {
            var o = new CollectorOptions { IngestInsecure = true, Auth = AuthMode.None, Data = null, Workers = 1 };
            C = new Collector(o, Store, Store, Store, () => Volatile.Read(ref Now), Store);
            C.Start();
        }
        public async Task Send(params string[] lines)
        {
            C.Ingest(Batch(lines), null, "proxy");
            await C.DrainAsync(TimeSpan.FromSeconds(5));
        }
        public ValueTask DisposeAsync() => C.DisposeAsync();
    }

    [Fact]
    public async Task A_failing_aggregate_store_is_retried_with_backoff_not_on_every_flush_and_recovers()
    {
        await using var rig = new Rig();
        rig.Store.Fail = true;
        await rig.Send(Op(1, "find"));
        for (var i = 0; i < 12; i++) { rig.C.Analysis.Tick(); await Task.Delay(100); } // ~1.2 s of flush attempts every 100 ms
        Assert.InRange(rig.Store.Failures, 1, 8); // an unthrottled flush fails 2+ calls per turn: > 20 here
        rig.Store.Fail = false;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (rig.Store.Buckets(0, long.MaxValue).Count == 0 && DateTime.UtcNow < deadline) { rig.C.Analysis.Tick(); await Task.Delay(100); }
        Assert.Equal(1, Assert.Single(rig.Store.Buckets(0, long.MaxValue)).Count); // nothing lost, nothing double counted
    }

    [Fact]
    public void Pending_deltas_are_bounded_oldest_minute_first_and_counted()
    {
        var book = new AggregateBook(maxPendingDeltas: 3);
        for (var i = 0; i < 6; i++)
        {
            Assert.Equal(ParseResult.Ok, RecordParser.Parse(Encoding.UTF8.GetBytes(Op(i, "find", ts: T0 + i * 60_000_000L)), out var r, out _));
            book.Add(new Operation { Id = Ulid.New(T0 / 1000), Op = (OpRecord)r!, ShapeHash = "s" }, T0);
        }
        book.BoundPending();
        Assert.Equal(3, book.DeltasDropped);
        var (deltas, _) = book.TakeDirty();
        Assert.Equal(3, deltas.Count);
        Assert.True(deltas.Min(d => d.Minute) >= (T0 + 3 * 60_000_000L) / 60_000_000);
    }

    [Fact]
    public void One_undecodable_aggregate_row_does_not_block_the_merge()
    {
        using var store = new SqliteStore(_dir);
        var bucket = new MinuteBucket { ShapeHash = "s", Minute = 100, Count = 1 };
        store.Merge([bucket]);
        using (var c = new SqliteConnection($"Data Source={Path.Combine(_dir, "spyglass.db")}"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "UPDATE agg_minute SET data = x'0102' WHERE shape = 's'";
            cmd.ExecuteNonQuery();
        }
        store.Merge([new MinuteBucket { ShapeHash = "s", Minute = 100, Count = 2 }, new MinuteBucket { ShapeHash = "t", Minute = 100, Count = 5 }]);
        Assert.Equal(1, store.UndecodableRows);
        Assert.Equal([2L, 5L], store.Buckets(0, 1000).OrderBy(b => b.ShapeHash).Select(b => b.Count).ToArray());
    }

    [Fact]
    public async Task A_failing_retention_step_keeps_storage_unhealthy_between_runs_and_does_not_rescan_every_minute()
    {
        await using var rig = new Rig();
        await rig.Send(Op(1, "find"));
        rig.Store.FailPrune = true;
        Volatile.Write(ref rig.Now, rig.Now + 61 * 60_000_000L); // retention and the hourly shape scan are due
        for (var i = 0; i < 4; i++) { rig.C.Analysis.Tick(); await Task.Delay(300); }
        Assert.True(rig.C.Analysis.StoreFailingSince != 0, "the retention failure was wiped by the flush that follows it");
        rig.Store.FailPrune = false;
        Volatile.Write(ref rig.Now, rig.Now + 11 * 60_000_000L);
        for (var i = 0; i < 4; i++) { rig.C.Analysis.Tick(); await Task.Delay(300); }
        Assert.Equal(0, rig.C.Analysis.StoreFailingSince);
    }

    // ---- FC-8 ------------------------------------------------------------------------------

    [Fact]
    public async Task A_resend_of_a_clamped_ts_is_still_a_duplicate()
    {
        long now = T0 + 1_000_000;
        var store = new MemoryStore();
        var o = new CollectorOptions { IngestInsecure = true, Auth = AuthMode.None, Data = null, Workers = 1 };
        await using var c = new Collector(o, store, store, store, () => Volatile.Read(ref now), store);
        c.Start();
        var line = Op(1, "find", ts: T0 + 3_600_000_000); // the producer's clock is an hour ahead
        c.Ingest(Batch(line), null, "proxy");
        Volatile.Write(ref now, now + 1_000_000); // its retry after a second (02 §5)
        c.Ingest(Batch(line), null, "proxy");
        await c.DrainAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(400);
        Assert.Equal(1, store.Count);
        Assert.Equal(1, c.Partitions.Sum(p => p.Duplicates));
    }

    // ---- FC-9, FC-21 (stored rows, export) -----------------------------------------------------

    private static Operation Make(int i, long latencyUs)
    {
        Assert.Equal(ParseResult.Ok, RecordParser.Parse(Encoding.UTF8.GetBytes(Op(i, "find", latencyUs: latencyUs, ts: T0 + i)), out var r, out _));
        return new Operation { Id = Ulid.New(T0 / 1000 + i), Op = (OpRecord)r!, ShapeHash = "s" };
    }

    [Fact]
    public void A_min_latency_query_on_a_large_table_stops_after_one_page_and_a_rare_one_still_finds_every_match()
    {
        using var store = new SqliteStore(_dir);
        const int n = 200_000;
        var fast = Make(1, 500);
        var slow = Make(2, 900_000);
        for (var from = 0; from < n; from += 5000)
            store.Append([.. Enumerable.Range(from, 5000).Select(i => new Operation { Id = Ulid.New(T0 / 1000 + i), Op = (i % 2000 == 0 ? slow : fast).Op, ShapeHash = "s" })]);
        Assert.Equal(n, store.Count);

        // most rows pass the threshold: the page fills from the newest rows
        var sw = Stopwatch.StartNew();
        var page = store.Query(new OpQuery { MinLatencyUs = 100, Limit = 100 });
        var first = sw.Elapsed;
        Assert.Equal(100, page.Count);
        Assert.Equal(page.OrderByDescending(o => o.Id, StringComparer.Ordinal).Select(o => o.Id), page.Select(o => o.Id));
        Assert.True(first < TimeSpan.FromMilliseconds(500), $"{first.TotalMilliseconds} ms");

        // 1 in 2000: the key walk comes up short and the index answers; every match, newest first
        var rare = store.Query(new OpQuery { MinLatencyUs = 800_000, Limit = 1000 });
        Assert.Equal(n / 2000, rare.Count);
        Assert.Equal(rare.OrderByDescending(o => o.Id, StringComparer.Ordinal).Select(o => o.Id), rare.Select(o => o.Id));
        Assert.Empty(store.Query(new OpQuery { MinLatencyUs = 50_000_000_000 }));
    }

    [Fact]
    public void A_stored_row_with_bad_utf8_stays_readable_and_a_skipped_row_does_not_end_a_page()
    {
        using var store = new SqliteStore(_dir);
        var ops = Enumerable.Range(0, 5).Select(i => Make(i, 500)).ToList();
        store.Append(ops);
        // a row an older build stored, with a stray invalid byte in the app name (U+FFFD after the repair) and one that is hopeless
        using (var c = new SqliteConnection($"Data Source={Path.Combine(_dir, "spyglass.db")}"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "UPDATE ops SET raw = CAST(replace(CAST(raw AS TEXT), '\"member\":\"localhost', '\"member\":\"local' || char(0xDC80) || 'host') AS BLOB) WHERE id = $a;" +
                              "UPDATE ops SET raw = x'7B7D' WHERE id = $b";
            cmd.Parameters.AddWithValue("$a", ops[1].Id);
            cmd.Parameters.AddWithValue("$b", ops[3].Id);
            cmd.ExecuteNonQuery();
        }
        var q = new OpQuery { Limit = 5 };
        var page = store.Query(q);
        Assert.Equal(4, page.Count); // the hopeless row is skipped
        Assert.Equal(5, q.Scanned);   // but a page of 5 rows read is full: it is not the end
        Assert.Equal(ops[0].Id, q.LastId);
    }

    // ---- FC-10 ------------------------------------------------------------------------------

    [Fact]
    public async Task A_publish_that_throws_after_the_commit_does_not_count_the_batch_as_failed()
    {
        var mem = new MemoryStore();
        var live = new LiveHub(10);
        using var sub = live.Subscribe(_ => throw new InvalidOperationException("filter bug"));
        var w = new StorageWriter(new GetThrows(mem), mem, mem, new CollectorOptions { IngestInsecure = true, Auth = AuthMode.None }, () => T0, live);
        w.Start();
        var op = Make(1, 500);
        w.Enqueue(op);
        w.Enqueue(new OpUpdate(op.Id, 2, null, new CmdRecord { Src = Exporter, Raw = [], UowId = "u" }));
        await w.Barrier().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, w.WriteDropped);
        Assert.Equal(0, w.Retries);
        Assert.Equal(1, w.Written);
        w.Complete();
    }

    private sealed class GetThrows(MemoryStore inner) : IOperationStore
    {
        public void Append(IReadOnlyList<Operation> ops) => inner.Append(ops);
        public Operation? Get(string id) => throw new InvalidOperationException("row did not materialize");
        public List<Operation> Query(OpQuery q) => inner.Query(q);
        public void Update(IReadOnlyList<OpUpdate> updates) => inner.Update(updates);
        public int Prune(long olderThanUs, long maxBytes, Action? progress = null) => inner.Prune(olderThanUs, maxBytes, progress);
        public long Count => inner.Count;
    }

    // ---- FC-12, FC-13 ---------------------------------------------------------------------------

    [Fact]
    public async Task A_shape_recreated_after_eviction_continues_its_stored_history()
    {
        await using var rig = new Rig();
        var stored = new ShapeInfo { ShapeHash = "h", ShapeJson = "{\"a\":1}", Command = "find", Db = "shop", Coll = "orders", FirstSeen = 5, LastSeen = 6, Count = 40 };
        rig.Store.UpsertShapes([stored]);
        var op = Parse(Op(1, "find"));
        op.ShapeHash = "h";
        op.ShapeJson = "{\"a\":1}"; // carries JSON, yet the catalog does not know the shape
        rig.C.Analysis.Post(op, null);
        await rig.C.DrainAsync(TimeSpan.FromSeconds(5));
        rig.C.Analysis.Tick();
        await Task.Delay(400);
        var s = rig.Store.Shape("h")!;
        Assert.Equal(5, s.FirstSeen);
        Assert.Equal(41, s.Count);
    }

    private static Operation Parse(string line)
    {
        Assert.Equal(ParseResult.Ok, RecordParser.Parse(Encoding.UTF8.GetBytes(line), out var r, out _));
        return new Operation { Id = Ulid.New(((OpRecord)r!).Ts / 1000), Op = (OpRecord)r!, ShapeHash = "s" };
    }

    // ---- FC-18 -------------------------------------------------------------------------------

    [Fact]
    public async Task Unauthenticated_health_carries_no_exception_text_but_a_signed_in_caller_gets_it()
    {
        var o = new CollectorOptions { Bind = new IPEndPoint(IPAddress.Loopback, 0), Token = "t", Auth = AuthMode.Password, AdminPassword = "pw", Data = null, Workers = 1 };
        await using var s = await Server.StartAsync(o);
        using var http = new HttpClient { BaseAddress = s.BaseAddress };
        s.Collector.Analysis.GetType().GetMethod("Fault", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(s.Collector.Analysis, [new InvalidOperationException("secret /var/lib/spyglass/x.db")]);
        var anon = await (await http.GetAsync("/healthz")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("secret", anon);
        Assert.Contains("exceptions isolated", anon);

        using var req = new HttpRequestMessage(HttpMethod.Get, "/healthz");
        req.Headers.Add("Authorization", "Basic " + Convert.ToBase64String("u:pw"u8));
        Assert.Contains("secret", await (await http.SendAsync(req)).Content.ReadAsStringAsync());
    }
}
