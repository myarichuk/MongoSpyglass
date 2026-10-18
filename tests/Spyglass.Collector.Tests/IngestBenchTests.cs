using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using MongoDB.Bson;
using static Spyglass.Collector.Tests.Records;

namespace Spyglass.Collector.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class Isolated
{
    public const string Name = "isolated";
}

/// <summary>
/// 03 §11 on this machine: sustained ingest into the SQLite store over HTTP, and the time from a POST to
/// the op's live event. Prints the numbers; gates only on correctness (nothing lost or rejected at the
/// measured rate). Release only: Debug numbers mean nothing.
/// </summary>
[Collection(Isolated.Name)]
public sealed class IngestBenchTests
{
#if DEBUG
    private static readonly bool IsDebugBuild = true;
#else
    private static readonly bool IsDebugBuild = false;
#endif

    /// <summary>GC allocation ticks (one per ~100 KB allocated) by type: where the bytes go.</summary>
    private sealed class AllocListener : System.Diagnostics.Tracing.EventListener
    {
        public readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> Types = new();
        public volatile bool Recording;

        protected override void OnEventSourceCreated(System.Diagnostics.Tracing.EventSource source)
        {
            if (source.Name == "Microsoft-Windows-DotNETRuntime")
                EnableEvents(source, System.Diagnostics.Tracing.EventLevel.Verbose, (System.Diagnostics.Tracing.EventKeywords)0x1 /* GC */);
        }

        protected override void OnEventWritten(System.Diagnostics.Tracing.EventWrittenEventArgs e)
        {
            if (!Recording || e.EventName != "GCAllocationTick_V4" || e.Payload == null) return;
            var type = e.Payload[e.PayloadNames!.IndexOf("TypeName")] as string ?? "?";
            Types.AddOrUpdate(type, 1, (_, n) => n + 1);
        }

        public string Top(int n)
        {
            var total = (double)Types.Values.Sum();
            return string.Join("\n  ", Types.OrderByDescending(kv => kv.Value).Take(n).Select(kv => $"{kv.Value / total,6:P1} {kv.Key}"));
        }
    }

    /// <summary>CPU seconds per thread-name prefix (Linux /proc): which stage is the bottleneck.</summary>
    private static Dictionary<string, double> ThreadCpu()
    {
        var result = new Dictionary<string, double>();
        foreach (var dir in Directory.GetDirectories("/proc/self/task"))
        {
            try
            {
                var name = File.ReadAllText(Path.Combine(dir, "comm")).Trim();
                var stat = File.ReadAllText(Path.Combine(dir, "stat"));
                var fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
                var ticks = long.Parse(fields[11]) + long.Parse(fields[12]); // utime + stime
                var key = name.StartsWith("collector-") ? name : name.StartsWith(".NET TP") || name.StartsWith(".NET Tp") ? "thread pool (HTTP, ingest)" : "other";
                result[key] = result.GetValueOrDefault(key) + ticks / 100.0; // USER_HZ
            }
            catch (IOException) { }
        }
        return result;
    }

    [Fact]
    public async Task Sustained_ingest_and_live_visibility()
    {
        if (IsDebugBuild) Assert.Skip("measurement needs a Release build");
        var dir = Path.Combine(Path.GetTempPath(), "spyglass-bench-" + Guid.NewGuid().ToString("N")[..8]);
        await using var server = await Server.StartAsync(new CollectorOptions
        {
            Bind = new IPEndPoint(IPAddress.Loopback, 0), Token = "t", Auth = AuthMode.None, Data = dir,
        });
        try
        {
            // 60 batches × 2000 records, 50 shapes, unique keys (dedup must not flatter the rate)
            var rnd = new Random(7);
            var fields = Enumerable.Range(0, 50).Select(i => "f" + i).ToArray();
            var batches = new List<byte[]>();
            var rid = 0;
            var ts = T0;
            for (var b = 0; b < 60; b++)
            {
                var lines = new string[2000];
                for (var i = 0; i < lines.Length; i++)
                {
                    var f = fields[rnd.Next(fields.Length)];
                    var cmd = new BsonDocument { ["find"] = "orders", ["filter"] = new BsonDocument { [f] = rnd.Next(1000), ["status"] = "A" }, ["limit"] = 10, ["lsid"] = new BsonDocument("id", "5b8516c3a99c21e7") };
                    lines[i] = Op(++rid, "find", cmd, ts: ts += 20, sid: 1 + rid % 64, connSeq: 1 + rid % 64, latencyUs: 200 + rnd.Next(5000), nReturned: rnd.Next(10));
                }
                batches.Add(Batch(lines));
            }
            var total = batches.Count * 2000;
            var bytes = batches.Sum(b => (long)b.Length);

            using var http = new HttpClient { BaseAddress = server.BaseAddress };
            async Task<HttpStatusCode> Post(byte[] body)
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, "/ingest/v0") { Content = new ByteArrayContent(body) };
                req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-ndjson");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "t");
                using var r = await http.SendAsync(req);
                return r.StatusCode;
            }

            await Post(batches[0]); // warm-up: JIT, SQLite, pools
            await server.Collector.DrainAsync(TimeSpan.FromSeconds(30));
            var accepted0 = server.Collector.RecordsAccepted;
            var stored0 = server.Collector.Writer.Written;

            // live visibility: one subscriber, marker ops timed from POST start to event
            var seen = new System.Collections.Concurrent.ConcurrentDictionary<int, long>();
            using var sub = server.Collector.Live.Subscribe(e => e.Op?.Op.Command == "ping");
            var watcher = Task.Run(async () =>
            {
                await foreach (var e in sub.Reader.ReadAllAsync())
                    seen.TryAdd(e.Op!.Op.RequestId, Stopwatch.GetTimestamp());
            });

            using var allocs = new AllocListener();
            var analysed0 = server.Collector.Analysis.Processed;
            var proc = Process.GetCurrentProcess();
            var cpu0 = proc.TotalProcessorTime;
            var threads0 = ThreadCpu();
            var alloc0 = GC.GetTotalAllocatedBytes(precise: false);
            var busy0 = server.Collector.Partitions.Sum(p => p.BusyTicks);
            var gc0 = (GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
            var sw = Stopwatch.StartNew();
            allocs.Recording = true;
            var statuses = new System.Collections.Concurrent.ConcurrentBag<HttpStatusCode>();
            var sentAt = new System.Collections.Concurrent.ConcurrentDictionary<int, long>();
            var next = 0;
            await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
            {
                int i;
                while ((i = Interlocked.Increment(ref next)) < batches.Count)
                {
                    sentAt[1_000_000 + i] = Stopwatch.GetTimestamp();
                    statuses.Add(await Post(Batch(Op(1_000_000 + i, "ping", db: "admin", coll: null, ts: T0 + i))));
                    statuses.Add(await Post(batches[i]));
                }
            })));
            var postSeconds = sw.Elapsed.TotalSeconds;
            // analysed: through stage 2 (DrainAsync would also wait for storage)
            var analysedTarget = total - 2000 + batches.Count - 1;
            while (server.Collector.Analysis.Processed - analysed0 < analysedTarget && sw.Elapsed < TimeSpan.FromSeconds(120)) await Task.Delay(5);
            var analysedSeconds = sw.Elapsed.TotalSeconds;
            await server.Collector.DrainAsync(TimeSpan.FromSeconds(60));
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (server.Collector.Writer.Written - stored0 < total - 2000 + batches.Count - 1 && DateTime.UtcNow < deadline)
                await Task.Delay(20);
            var storedSeconds = sw.Elapsed.TotalSeconds;
            allocs.Recording = false;
            proc.Refresh();
            var cpu = (proc.TotalProcessorTime - cpu0).TotalMicroseconds;
            var threads = ThreadCpu();
            var alloc = GC.GetTotalAllocatedBytes(precise: false) - alloc0;
            var gcs = (GC.CollectionCount(0) - gc0.Item1, GC.CollectionCount(1) - gc0.Item2, GC.CollectionCount(2) - gc0.Item3);

            var records = server.Collector.RecordsAccepted - accepted0;
            var visibility = sentAt.Where(kv => seen.ContainsKey(kv.Key))
                .Select(kv => Stopwatch.GetElapsedTime(kv.Value, seen[kv.Key]).TotalMilliseconds).Order().ToArray();
            var p95 = visibility.Length > 0 ? visibility[(int)(visibility.Length * 0.95)] : double.NaN;
            Console.WriteLine($"[ingest] {records} records ({bytes / 1048576.0:F0} MiB) in {postSeconds:F2} s posted = {records / postSeconds:F0} rec/s; " +
                              $"analysed {records / analysedSeconds:F0} rec/s; stored {records / storedSeconds:F0} rec/s; " +
                              $"collector+client CPU {cpu / records:F1} µs/record; live visibility p50 {visibility.ElementAtOrDefault(visibility.Length / 2):F0} ms p95 {p95:F0} ms; " +
                              $"429s {statuses.Count(s => s == HttpStatusCode.TooManyRequests)}; sampled {server.Collector.Writer.Sampled}; store {server.Collector.Ops.Count} ops");
            Console.WriteLine($"[ingest] {(System.Runtime.GCSettings.IsServerGC ? "server" : "workstation")} GC: {alloc / (double)records:F0} B allocated/record, gen0/1/2 collections {gcs.Item1}/{gcs.Item2}/{gcs.Item3}, GC pause {GC.GetTotalPauseDuration().TotalMilliseconds:F0} ms total");
            Console.WriteLine($"[ingest] partitions busy {(server.Collector.Partitions.Sum(p => p.BusyTicks) - busy0) * 1e6 / Stopwatch.Frequency / records:F1} µs/record (work, not waiting)");
            Console.WriteLine("[ingest] allocated by type (share of bytes):\n  " + allocs.Top(25));
            Console.WriteLine("[ingest] CPU µs/record by thread: " + string.Join(", ",
                threads.Select(kv => (kv.Key, Us: (kv.Value - threads0.GetValueOrDefault(kv.Key)) * 1e6 / records)).OrderByDescending(x => x.Us).Select(x => $"{x.Key} {x.Us:F1}")));

            // 03 §11 visibility is about a load the collector keeps up with: ~10k records/s, 500 per POST every
            // 50 ms (the proxy flushes every 500 ms or 2000 records), a marker in each
            await server.Collector.DrainAsync(TimeSpan.FromSeconds(30));
            var paced = new List<double>();
            for (var i = 0; i < 40; i++)
            {
                var id = 2_000_000 + i;
                var body = Batch([Op(id, "ping", db: "admin", coll: null, ts: T0 + id), .. Enumerable.Range(0, 499).Select(k => Op(3_000_000 + i * 1000 + k, "find", ts: T0 + 3_000_000 + i * 1000 + k, sid: 1 + k % 64, connSeq: 1 + k % 64))]);
                var t0 = Stopwatch.GetTimestamp();
                await Post(body);
                var until = DateTime.UtcNow.AddSeconds(5);
                while (!seen.ContainsKey(id) && DateTime.UtcNow < until) await Task.Delay(1);
                if (seen.TryGetValue(id, out var t1)) paced.Add(Stopwatch.GetElapsedTime(t0, t1).TotalMilliseconds);
                await Task.Delay(50);
            }
            paced.Sort();
            Console.WriteLine($"[ingest] paced ~10k rec/s: POST → live event p50 {paced[paced.Count / 2]:F1} ms, p95 {paced[(int)(paced.Count * 0.95)]:F1} ms, max {paced[^1]:F1} ms");
            Assert.Equal(40, paced.Count);

            Assert.All(statuses, s => Assert.Equal(HttpStatusCode.NoContent, s));
            Assert.Equal(0, server.Collector.RecordsRejected);
            Assert.Equal(batches.Count - 1, visibility.Length);   // every marker reached the live feed
            sub.Dispose();
            await watcher;
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }
}
