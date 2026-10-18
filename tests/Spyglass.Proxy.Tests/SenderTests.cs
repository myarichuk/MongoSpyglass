using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Spyglass.Proxy.Config;
using Spyglass.Proxy.Export;
using Spyglass.Proxy.Metrics;

namespace Spyglass.Proxy.Tests;

/// <summary>
/// 02 §5 delivery rules of <see cref="CollectorSender"/>, against an in-memory handler: batch limits,
/// 413 split, 429 pause, the buffer bound, oversized records. Invariant throughout: every appended
/// record is delivered or counted as dropped, and one producer's records arrive in order.
/// </summary>
public sealed class SenderTests
{
    private sealed class Handler(Func<List<string>, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public readonly ConcurrentQueue<List<string>> Delivered = new();
        public readonly ConcurrentQueue<int> PostSizes = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = await request.Content!.ReadAsStringAsync(ct);
            Assert.Equal(Encoding.UTF8.GetByteCount(body), request.Content.Headers.ContentLength);
            Assert.EndsWith("\n", body);
            var lines = body.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
            PostSizes.Enqueue(lines.Count);
            var resp = respond(lines);
            if (resp.IsSuccessStatusCode) Delivered.Enqueue(lines);
            return resp;
        }

        public List<string> All => Delivered.SelectMany(l => l).ToList();
    }

    private static CollectorConfig Config(int maxRecords = 2000, int maxBytes = 1024 * 1024, long buffer = 16L * 1024 * 1024) => new()
    {
        Endpoint = "http://collector.invalid/ingest/v0",
        BatchMaxRecords = maxRecords,
        BatchMaxBytes = maxBytes,
        BufferBytes = buffer,
        FlushIntervalMs = 20,
    };

    private static string Line(int i, int pad = 0) => $"{{\"i\":{i}{(pad > 0 ? ",\"p\":\"" + new string('x', pad) + "\"" : "")}}}";

    private static async Task Until(Func<bool> cond)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!cond())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out");
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task Batches_respect_record_and_byte_limits_and_keep_order()
    {
        var h = new Handler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        var dropped = new Counter();
        await using var s = new CollectorSender(Config(maxRecords: 10, maxBytes: 2048), "src", dropped, h);
        var buf = s.CreateBuffer();
        s.Start();
        for (var i = 0; i < 1000; i++) buf.Append(Encoding.UTF8.GetBytes(Line(i, pad: i % 7 * 20)));
        await Until(() => h.All.Count == 1000);

        Assert.Equal(Enumerable.Range(0, 1000).Select(i => Line(i, i % 7 * 20)), h.All);
        Assert.All(h.PostSizes, n => Assert.InRange(n, 1, 10));
        Assert.All(h.Delivered, b => Assert.True(b.Sum(l => l.Length + 1) <= 2048 || b.Count == 1));
        Assert.Equal(0, dropped.Value);
        Assert.Equal(1000, Interlocked.Read(ref s.SentRecords));
    }

    [Fact]
    public async Task Status_413_splits_once_then_drops()
    {
        // accept at most 3 records per post: a batch of 8 splits into 4 + 4, which are still too big → dropped;
        // a batch of 6 splits into 3 + 3 → delivered
        var h = new Handler(lines => new HttpResponseMessage(lines.Count > 3 ? HttpStatusCode.RequestEntityTooLarge : HttpStatusCode.NoContent));
        var dropped = new Counter();
        await using (var s = new CollectorSender(Config(maxRecords: 6), "src", dropped, h))
        {
            var buf = s.CreateBuffer();
            for (var i = 0; i < 60; i++) buf.Append(Encoding.UTF8.GetBytes(Line(i))); // before Start: full batches only
            s.Start();
            await Until(() => h.All.Count + dropped.Value == 60);
        }
        Assert.Equal(60, h.All.Count);
        Assert.Equal(0, dropped.Value);
        Assert.All(h.Delivered, b => Assert.Equal(3, b.Count));

        var h2 = new Handler(lines => new HttpResponseMessage(lines.Count > 3 ? HttpStatusCode.RequestEntityTooLarge : HttpStatusCode.NoContent));
        var dropped2 = new Counter();
        await using (var s = new CollectorSender(Config(maxRecords: 8), "src", dropped2, h2))
        {
            var buf = s.CreateBuffer();
            for (var i = 0; i < 64; i++) buf.Append(Encoding.UTF8.GetBytes(Line(i)));
            s.Start();
            await Until(() => h2.All.Count + dropped2.Value == 64);
        }
        Assert.Empty(h2.All);
        Assert.Equal(64, dropped2.Value);
    }

    [Fact]
    public async Task Status_413_split_across_several_chunks_keeps_order()
    {
        // chunks sealed every 10 records (as the sender's flush does to partial chunks), so a 40-record batch
        // is 4 segments; 413 above 25 records → split by segments into 20 + 20
        var h = new Handler(lines => new HttpResponseMessage(lines.Count > 25 ? HttpStatusCode.RequestEntityTooLarge : HttpStatusCode.NoContent));
        var dropped = new Counter();
        await using var s = new CollectorSender(Config(maxRecords: 40), "src", dropped, h);
        var buf = s.CreateBuffer();
        for (var i = 0; i < 400; i++)
        {
            buf.Append(Encoding.UTF8.GetBytes(Line(i)));
            if (i % 10 == 9) buf.Flush();
        }
        s.Start();
        await Until(() => h.All.Count + dropped.Value == 400);
        Assert.Equal(Enumerable.Range(0, 400).Select(i => Line(i)), h.All);
        Assert.All(h.Delivered, b => Assert.Equal(20, b.Count));
        Assert.Equal(0, dropped.Value);
    }

    [Fact]
    public async Task Status_429_pauses_appends_for_retry_after()
    {
        var reject = 1;
        var h = new Handler(_ =>
        {
            if (Interlocked.Exchange(ref reject, 0) == 1)
            {
                var r = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(1));
                return r;
            }
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        var dropped = new Counter();
        await using var s = new CollectorSender(Config(), "src", dropped, h);
        var buf = s.CreateBuffer();
        s.Start();
        buf.Append("{\"first\":1}"u8);
        await Until(() => dropped.Value == 1); // the 429'd batch is dropped
        Assert.True(s.Paused);
        buf.Append("{\"during\":1}"u8);   // dropped at append: paused
        Assert.Equal(2, dropped.Value);
        await Until(() => !s.Paused);
        buf.Append("{\"after\":1}"u8);
        await Until(() => h.All.Count == 1);
        Assert.Equal(["{\"after\":1}"], h.All);
    }

    [Fact]
    public async Task Buffer_bound_drops_and_counts_never_blocks()
    {
        var h = new Handler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        var dropped = new Counter();
        // 1 KiB chunks, 4 KiB bound: with the sender not running, at most 4 chunks are held
        await using var s = new CollectorSender(Config(maxBytes: 1024, buffer: 4096), "src", dropped, h);
        var buf = s.CreateBuffer();
        for (var i = 0; i < 200; i++) buf.Append(Encoding.UTF8.GetBytes(Line(i, pad: 80)));
        Assert.True(s.ChargedBytes <= 4096, $"{s.ChargedBytes}");
        var kept = 200 - dropped.Value;
        Assert.InRange(kept, 30, 50); // ~10 lines of ~90 B per 1 KiB chunk

        s.Start();
        await Until(() => h.All.Count == kept);
        Assert.Equal(Enumerable.Range(0, (int)kept).Select(i => Line(i, 80)), h.All); // the first ones, in order
        await Until(() => s.ChargedBytes == 0);    // every chunk released after the send
    }

    [Fact]
    public async Task Record_larger_than_a_chunk_is_sent_alone()
    {
        var h = new Handler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        var dropped = new Counter();
        await using var s = new CollectorSender(Config(maxBytes: 1024), "src", dropped, h);
        var buf = s.CreateBuffer();
        s.Start();
        buf.Append(Encoding.UTF8.GetBytes(Line(1)));
        buf.Append(Encoding.UTF8.GetBytes(Line(2, pad: 5000)));
        buf.Append(Encoding.UTF8.GetBytes(Line(3)));
        await Until(() => h.All.Count == 3);
        Assert.Equal([Line(1), Line(2, 5000), Line(3)], h.All);
        Assert.Equal(0, dropped.Value);
    }

    [Fact]
    public async Task Several_producers_all_delivered_each_in_order()
    {
        var h = new Handler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        var dropped = new Counter();
        await using var s = new CollectorSender(Config(maxRecords: 50, maxBytes: 4096), "src", dropped, h);
        s.Start();
        const int producers = 4, each = 5000;
        await Task.WhenAll(Enumerable.Range(0, producers).Select(p => Task.Run(() =>
        {
            var buf = s.CreateBuffer();
            for (var i = 0; i < each; i++) buf.Append(Encoding.UTF8.GetBytes($"{{\"p\":{p},\"i\":{i}}}"));
        })));
        await Until(() => h.All.Count == producers * each);
        foreach (var g in h.All.Select(l => System.Text.Json.JsonDocument.Parse(l).RootElement)
                     .GroupBy(e => e.GetProperty("p").GetInt32()))
            Assert.Equal(Enumerable.Range(0, each), g.Select(e => e.GetProperty("i").GetInt32()));
        Assert.Equal(0, dropped.Value);
    }
}
