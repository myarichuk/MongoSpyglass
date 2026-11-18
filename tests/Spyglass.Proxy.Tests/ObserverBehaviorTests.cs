using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using MongoDB.Bson;
using Spyglass.Proxy.Config;
using Spyglass.Proxy.Forwarding;
using Spyglass.Proxy.Metrics;
using Spyglass.Proxy.Observation;
using Spyglass.Proxy.Topology;
using Spyglass.Proxy.Wire;

namespace Spyglass.Proxy.Tests;

/// <summary>
/// The observer end to end without sockets: driver-shaped frames go through the real taps, workers, correlation,
/// redaction and metrics, and the exported records are read back. (Fixes from the observation review, PO-n.)
/// </summary>
public sealed class ObserverBehaviorTests
{
    private sealed class NoMap : IAddressMap
    {
        public string? Advertise(HostPort upstream) => null;
    }

    private sealed class Harness : IAsyncDisposable
    {
        public readonly ProxyServer Server;
        public readonly Observer Observer;
        public readonly ConcurrentQueue<JsonElement> Records = new();
        private readonly Member _member;
        private int _seq;

        public Harness(params string[] args) : this(true, args) { }

        /// <param name="start">false: the workers never run, as in a stalled observer thread.</param>
        public Harness(bool start, params string[] args)
        {
            var config = ConfigLoader.Load(["--upstream.uri", "mongodb://127.0.0.1:1/", .. args], new Dictionary<string, string?>());
            Server = new ProxyServer(config);
            Observer = new Observer(Server);
            Observer.RecordTap = line => Records.Enqueue(JsonDocument.Parse(line.ToArray()).RootElement.Clone());
            if (start) Observer.Start();
            _member = new Member(new HostPort("127.0.0.1", 1), 1, "127.0.0.1:1",
                new HelloRewriteContext { RewriteTopology = false, Map = new NoMap(), Self = "127.0.0.1:1", SpyglassDocument = [5, 0, 0, 0, 0] },
                Server.Metrics);
        }

        public sealed record Conn(ConnectionContext Ctx, IFrameTap Req, IFrameTap Rep);

        public Conn Open()
        {
            var ctx = new ConnectionContext(Interlocked.Increment(ref _seq), _member, Server, "127.0.0.1:40000");
            Observer.Opened(ctx);
            return new Conn(ctx, Observer.CreateTap(ctx, isRequest: true), Observer.CreateTap(ctx, isRequest: false));
        }

        public void Close(Conn c, CloseReason reason = CloseReason.ClientClosed) => Observer.Closed(c.Ctx, reason, null);

        public static void Feed(IFrameTap tap, byte[] frame)
        {
            var t = Stopwatch.GetTimestamp();
            tap.Begin(FrameHeader.Read(frame), t, RequestKind.Other);
            tap.Body(frame.AsSpan(FrameHeader.Size));
            tap.End(t);
        }

        public static byte[] Msg(int requestId, int responseTo, BsonDocument body, uint flags = 0)
        {
            var frame = StandaloneTests.OpMsg(requestId, body);
            BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(8), responseTo);
            BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(16), flags);
            return frame;
        }

        /// <summary>The same OP_MSG as <paramref name="msg"/>, as an OP_COMPRESSED frame (zlib).</summary>
        public static byte[] Compressed(byte[] msg)
        {
            var inner = msg.AsSpan(16).ToArray();
            using var ms = new MemoryStream();
            using (var z = new ZLibStream(ms, CompressionLevel.Fastest, leaveOpen: true))
                z.Write(inner);
            var payload = ms.ToArray();
            var frame = new byte[16 + 9 + payload.Length];
            msg.AsSpan(0, 16).CopyTo(frame);
            BinaryPrimitives.WriteInt32LittleEndian(frame, frame.Length);
            BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(12), OpCodes.Compressed);
            BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(16), OpCodes.Msg);
            BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(20), inner.Length);
            frame[24] = 2;
            payload.CopyTo(frame, 25);
            return frame;
        }

        public async Task<List<JsonElement>> WaitAsync(Func<JsonElement, bool> match, int count = 1)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (true)
            {
                var hits = Records.Where(match).ToList();
                if (hits.Count >= count) return hits;
                Assert.True(DateTime.UtcNow < deadline, "timed out waiting for records: " + string.Join("\n", Records.Select(r => r.GetRawText())));
                await Task.Delay(10);
            }
        }

        public async Task UntilAsync(Func<bool> cond)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!cond())
            {
                Assert.True(DateTime.UtcNow < deadline, "timed out");
                await Task.Delay(10);
            }
        }

        public long Dropped(string reason) => Server.Metrics.ObservationDropped.WithLabels(reason).Value;

        public async ValueTask DisposeAsync()
        {
            await Observer.DisposeAsync();
            await Server.DisposeAsync();
        }
    }

    private static string S(JsonElement e, string p) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
    private static bool IsOp(JsonElement r, string command) => S(r, "type") == "op" && S(r, "command") == command;

    private static BsonDocument Dup(string msg) => new() { ["ok"] = 0.0, ["code"] = 11000, ["codeName"] = "DuplicateKey", ["errmsg"] = msg };

    // ---- PO-4: reply errmsg and request comment follow the value mode ----

    [Theory]
    [InlineData("full")]
    [InlineData("truncated")]
    [InlineData("shape")]
    public async Task Errmsg_does_not_carry_values_of_secret_fields(string mode)
    {
        await using var h = new Harness("--capture.values", mode);
        var c = h.Open();
        Harness.Feed(c.Req, Harness.Msg(1, 0, new BsonDocument { ["insert"] = "users", ["$db"] = "app" }));
        Harness.Feed(c.Rep, Harness.Msg(100, 1, Dup("E11000 duplicate key error collection: app.users index: apiKey_1 dup key: { apiKey: \"sk_live_SECRET123\", email: \"a@b.c\" }")));
        var op = (await h.WaitAsync(r => IsOp(r, "insert")))[0];

        var reply = op.GetProperty("reply");
        Assert.DoesNotContain("sk_live_SECRET123", op.GetRawText());
        if (mode == "shape")
            Assert.False(reply.TryGetProperty("errmsg", out _));
        else
        {
            Assert.Contains("E11000 duplicate key error collection: app.users", S(reply, "errmsg"));
            Assert.Contains("apiKey: \"$redacted\"", S(reply, "errmsg"));
            Assert.Contains("email: \"a@b.c\"", S(reply, "errmsg")); // not a secret name; short
        }
    }

    [Fact]
    public async Task Errmsg_quoted_values_are_truncated_in_truncated_mode()
    {
        await using var h = new Harness("--capture.values", "truncated", "--capture.truncateStringsAt", "10");
        var c = h.Open();
        Harness.Feed(c.Req, Harness.Msg(1, 0, new BsonDocument { ["insert"] = "users", ["$db"] = "app" }));
        Harness.Feed(c.Rep, Harness.Msg(100, 1, Dup("E11000 dup key: { email: \"" + new string('e', 40) + "\" }")));
        var msg = S((await h.WaitAsync(r => IsOp(r, "insert")))[0].GetProperty("reply"), "errmsg");
        Assert.DoesNotContain(new string('e', 40), msg);
        Assert.Contains("eeeeeeeeee…(+30)\"", msg);
    }

    [Theory]
    [InlineData("full", 200)]
    [InlineData("truncated", 64)]
    [InlineData("shape", -1)]
    public async Task Comment_follows_the_value_mode(string mode, int expected)
    {
        await using var h = new Harness("--capture.values", mode);
        var c = h.Open();
        Harness.Feed(c.Req, Harness.Msg(1, 0, new BsonDocument { ["find"] = "users", ["$db"] = "app", ["comment"] = new string('c', 200) }));
        Harness.Feed(c.Rep, Harness.Msg(100, 1, new BsonDocument { ["ok"] = 1.0 }));
        var op = (await h.WaitAsync(r => IsOp(r, "find")))[0];
        if (expected < 0) Assert.False(op.TryGetProperty("comment", out _));
        else Assert.Equal(expected, S(op, "comment").Length);
    }

    // ---- PO-3: a request whose reply is never observed doesn't linger ----

    [Fact]
    public async Task Reply_without_a_slot_releases_the_pending_request()
    {
        await using var h = new Harness();
        var c = h.Open();
        Harness.Feed(c.Req, Harness.Msg(1, 0, new BsonDocument { ["find"] = "users", ["$db"] = "app" }));
        await h.UntilAsync(() => h.Observer.Pool.ChargedBytes == 0);

        // exhaust the capture pool: the reply finds no slot
        var held = new List<FrameCapture>();
        for (FrameCapture? s; (s = h.Observer.Pool.Rent(false)) != null;) held.Add(s);
        Harness.Feed(c.Rep, Harness.Msg(100, 1, new BsonDocument { ["ok"] = 1.0 }));
        held.ForEach(h.Observer.Pool.Return);
        Assert.Equal(1, h.Dropped("queue_full"));

        // an unrelated op after it, then close: the lost reply's request is not reported as connection_closed
        Harness.Feed(c.Req, Harness.Msg(2, 0, new BsonDocument { ["ping"] = 1, ["$db"] = "admin" }));
        Harness.Feed(c.Rep, Harness.Msg(101, 2, new BsonDocument { ["ok"] = 1.0 }));
        await h.WaitAsync(r => IsOp(r, "ping"));
        h.Close(c);
        await h.WaitAsync(r => S(r, "type") == "conn" && S(r, "event") == "close");
        Assert.DoesNotContain(h.Records, r => S(r, "type") == "op" && S(r, "status") == "connection_closed");
    }

    [Fact]
    public async Task Request_that_never_gets_a_reply_ages_out_and_is_counted()
    {
        await using var h = new Harness();
        h.Observer.PendingMaxAge = TimeSpan.FromMilliseconds(200);
        var c = h.Open();
        Harness.Feed(c.Req, Harness.Msg(1, 0, new BsonDocument { ["find"] = "users", ["$db"] = "app" }));
        await h.UntilAsync(() => h.Dropped("pending_evicted") == 1);
        h.Close(c);
        await h.WaitAsync(r => S(r, "type") == "conn" && S(r, "event") == "close");
        Assert.DoesNotContain(h.Records, r => S(r, "type") == "op"); // not a false connection_closed failure
    }

    [Fact]
    public void Pending_age_out_is_configurable_and_defaults_to_hours()
    {
        var h = new Harness(false);
        Assert.Equal(TimeSpan.FromHours(6), h.Observer.PendingMaxAge); // index builds, $out, compact and dumps outlive any minutes-scale bound
        var short_ = new Harness(false, "--limits.pendingMaxAgeSeconds", "90");
        Assert.Equal(TimeSpan.FromSeconds(90), short_.Observer.PendingMaxAge);
    }

    [Fact]
    public void A_stalled_observer_holds_a_bounded_number_of_slotless_reply_notices_per_connection()
    {
        // FP-3: the pool has no room for any slot, so every reply is slotless; nobody drains the worker queue
        var h = new Harness(false, "--limits.observeQueueBytes", "512");
        var c = h.Open();
        for (var i = 0; i < 1000; i++)
            Harness.Feed(c.Rep, Harness.Msg(100 + i, 1 + i, new BsonDocument { ["ok"] = 1.0 }));
        Assert.Equal(CaptureTap.UnobservedSlots, Volatile.Read(ref c.Ctx.UnobservedInflight));
    }

    [Fact]
    public async Task Pending_request_at_close_is_still_connection_closed()
    {
        await using var h = new Harness();
        var c = h.Open();
        Harness.Feed(c.Req, Harness.Msg(1, 0, new BsonDocument { ["find"] = "users", ["$db"] = "app" }));
        h.Close(c);
        var op = (await h.WaitAsync(r => IsOp(r, "find")))[0];
        Assert.Equal("connection_closed", S(op, "status"));
    }

    // ---- PO-7: compression=observe inflates replies ----

    [Fact]
    public async Task Observe_reads_facts_from_a_compressed_reply()
    {
        await using var h = new Harness("--compression", "observe");
        var c = h.Open();
        Harness.Feed(c.Req, Harness.Compressed(Harness.Msg(1, 0, new BsonDocument { ["insert"] = "users", ["$db"] = "app", ["documents"] = new BsonArray { new BsonDocument("a", 1) } })));
        Harness.Feed(c.Rep, Harness.Compressed(Harness.Msg(100, 1, Dup("E11000 duplicate key"))));
        var op = (await h.WaitAsync(r => IsOp(r, "insert")))[0];
        Assert.Equal("error", S(op, "status"));
        Assert.Equal("DuplicateKey", S(op.GetProperty("reply"), "codeName"));
        var errors = h.Observer.Commands.Errors.WithLabels("insert", "app", "users", "", "DuplicateKey");
        Assert.Equal(1, errors.Value);
    }

    [Fact]
    public async Task Observe_follows_a_compressed_exhaust_stream()
    {
        await using var h = new Harness("--compression", "observe");
        var c = h.Open();
        Harness.Feed(c.Req, Harness.Compressed(Harness.Msg(1, 0, new BsonDocument { ["getMore"] = 77L, ["collection"] = "users", ["$db"] = "app" })));
        var batch = new BsonDocument { ["cursor"] = new BsonDocument { ["nextBatch"] = new BsonArray { 1, 2, 3 }, ["id"] = 77L, ["ns"] = "app.users" }, ["ok"] = 1.0 };
        Harness.Feed(c.Rep, Harness.Compressed(Harness.Msg(100, 1, batch, flags: 2u))); // moreToCome (bit 1)
        Harness.Feed(c.Rep, Harness.Compressed(Harness.Msg(101, 100, batch)));
        var op = (await h.WaitAsync(r => IsOp(r, "getMore")))[0];
        Assert.True(op.GetProperty("stream").GetBoolean());
        var stream = (await h.WaitAsync(r => S(r, "type") == "stream"))[0];
        Assert.Equal(3, stream.GetProperty("reply").GetProperty("nReturned").GetInt32());
        Assert.Equal(0, h.Server.Metrics.UncorrelatedReplies.WithLabels().Value);
    }

    [Fact]
    public async Task Opaque_keeps_a_compressed_reply_unparsed()
    {
        await using var h = new Harness("--compression", "opaque");
        var c = h.Open();
        Harness.Feed(c.Req, Harness.Compressed(Harness.Msg(1, 0, new BsonDocument { ["find"] = "users", ["$db"] = "app" })));
        Harness.Feed(c.Rep, Harness.Compressed(Harness.Msg(100, 1, Dup("E11000 duplicate key"))));
        var op = (await h.WaitAsync(r => S(r, "type") == "op"))[0];
        Assert.Equal("(compressed)", S(op, "command"));
        Assert.False(op.GetProperty("reply").GetProperty("parsed").GetBoolean());
    }

    // ---- PO-13: conn open ts is the accept time ----

    [Fact]
    public async Task Conn_open_ts_is_when_the_connection_opened()
    {
        await using var h = new Harness();
        var c = h.Open();
        var before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000;
        await Task.Delay(300);
        Harness.Feed(c.Req, Harness.Msg(1, 0, new BsonDocument { ["ping"] = 1, ["$db"] = "admin" }));
        Harness.Feed(c.Rep, Harness.Msg(100, 1, new BsonDocument { ["ok"] = 1.0 }));
        var open = (await h.WaitAsync(r => S(r, "type") == "conn" && S(r, "event") == "open"))[0];
        var op = (await h.WaitAsync(r => IsOp(r, "ping")))[0];
        Assert.True(op.GetProperty("ts").GetInt64() - open.GetProperty("ts").GetInt64() >= 250_000, "open must predate the first op by the idle time");
        Assert.True(open.GetProperty("ts").GetInt64() <= before + 50_000);
    }

    // ---- PO-10: a non-finite ok ----

    [Fact]
    public async Task Non_finite_ok_is_an_error_and_does_not_fault_the_observer()
    {
        await using var h = new Harness();
        var c = h.Open();
        Harness.Feed(c.Req, Harness.Msg(1, 0, new BsonDocument { ["find"] = "users", ["$db"] = "app" }));
        Harness.Feed(c.Rep, Harness.Msg(100, 1, new BsonDocument { ["ok"] = double.NaN }));
        var op = (await h.WaitAsync(r => IsOp(r, "find")))[0];
        Assert.Equal("error", S(op, "status"));
        Assert.Equal(0, h.Dropped("observer_fault"));
    }

    // ---- PO-11: cursors gauge ----

    [Fact]
    public async Task Failed_getMore_and_closed_connections_release_cursors()
    {
        await using var h = new Harness();
        var c = h.Open();
        BsonDocument Opened(long id) => new() { ["cursor"] = new BsonDocument { ["firstBatch"] = new BsonArray(), ["id"] = id, ["ns"] = "app.users" }, ["ok"] = 1.0 };

        Harness.Feed(c.Req, Harness.Msg(1, 0, new BsonDocument { ["find"] = "users", ["$db"] = "app" }));
        Harness.Feed(c.Rep, Harness.Msg(100, 1, Opened(7)));
        await h.UntilAsync(() => h.Observer.Cursors.Count == 1);

        // getMore fails with CursorKilled (237), which destroys the cursor
        Harness.Feed(c.Req, Harness.Msg(2, 0, new BsonDocument { ["getMore"] = 7L, ["collection"] = "users", ["$db"] = "app" }));
        Harness.Feed(c.Rep, Harness.Msg(101, 2, new BsonDocument { ["ok"] = 0.0, ["code"] = 237, ["codeName"] = "CursorKilled", ["errmsg"] = "killed" }));
        await h.UntilAsync(() => h.Observer.Cursors.Count == 0);

        // an exhaust cursor dies with its connection
        Harness.Feed(c.Req, Harness.Msg(3, 0, new BsonDocument { ["find"] = "users", ["$db"] = "app" }));
        Harness.Feed(c.Rep, Harness.Msg(102, 3, Opened(8), flags: 2u));
        await h.UntilAsync(() => h.Observer.Cursors.Count == 1);
        h.Close(c);
        await h.UntilAsync(() => h.Observer.Cursors.Count == 0);
    }

    // ---- PO-15: exhaust stream replies feed the error and size metrics ----

    [Fact]
    public async Task Stream_replies_count_documents_and_errors()
    {
        await using var h = new Harness();
        var c = h.Open();
        Harness.Feed(c.Req, Harness.Msg(1, 0, new BsonDocument { ["getMore"] = 9L, ["collection"] = "users", ["$db"] = "app" }));
        var batch = new BsonDocument { ["cursor"] = new BsonDocument { ["nextBatch"] = new BsonArray { 1, 2 }, ["id"] = 9L, ["ns"] = "app.users" }, ["ok"] = 1.0 };
        Harness.Feed(c.Rep, Harness.Msg(100, 1, batch, flags: 2u));
        Harness.Feed(c.Rep, Harness.Msg(101, 100, new BsonDocument { ["ok"] = 0.0, ["code"] = 175, ["codeName"] = "QueryPlanKilled", ["errmsg"] = "x" }));
        var errors = h.Observer.Commands.Errors.WithLabels("getMore", "app", "users", "", "QueryPlanKilled");
        await h.UntilAsync(() => errors.Value == 1);
        var docs = h.Observer.Commands.ReturnedDocuments.WithLabels("getMore", "app", "users", "");
        Assert.Equal(1, docs.Snapshot().Count); // the first reply's batch; the error reply has none
    }

    // ---- PO-6: label folding ----

    [Fact]
    public void Series_are_bounded_on_every_client_controlled_label()
    {
        var reg = new MetricsRegistry();
        var cm = new CommandMetrics(reg, maxSeries: 5);
        for (var i = 0; i < 1000; i++)
            cm.Series(cm.Errors, "cmd" + i, "db" + i, "coll" + i, "app" + i, "code" + i).Inc();
        for (var i = 0; i < 1000; i++)
            cm.Series(cm.CursorsOpen, "db" + i, "coll" + i).Inc();
        Assert.True(cm.Errors.SeriesCount <= 11, cm.Errors.SeriesCount.ToString());
        Assert.True(cm.CursorsOpen.SeriesCount <= 11, cm.CursorsOpen.SeriesCount.ToString());
        Assert.True(cm.Series(cm.Errors, "x", "x", "x", "x", "x").Value > 900); // the overflow series took the rest
        Assert.Contains(CommandMetrics.Other, reg.Render());

        // first fold level: the collection folds, command / database / app / code_name stay intact
        var reg2 = new MetricsRegistry();
        var cm2 = new CommandMetrics(reg2, maxSeries: 2);
        for (var i = 0; i < 4; i++)
            cm2.Series(cm2.Errors, "find", "db", "coll" + i, "app", "Boom").Inc();
        var text = reg2.Render();
        Assert.Contains("command=\"find\",database=\"db\",collection=\"__other__\",app=\"app\",code_name=\"Boom\"", text);
    }

    // ---- PO-16: le labels ----

    [Fact]
    public void Histogram_bucket_bounds_print_shortest_round_trip()
    {
        var reg = new MetricsRegistry();
        var cm = new CommandMetrics(reg, 100);
        cm.Series(cm.Duration, "find", "app", "users", "", "ok").Observe(0.001);
        var text = reg.Render();
        Assert.Contains("le=\"0.00025\"", text);
        Assert.Contains("le=\"0.1\"", text);
        Assert.Contains("le=\"2.5\"", text);
        Assert.DoesNotContain("0000000001", text);
    }
}
