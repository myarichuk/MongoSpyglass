using System.Diagnostics;
using System.IO.Compression;
using System.IO.Pipelines;
using System.Buffers;
using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoSpyglass.Proxy;
using MongoSpyglass.Proxy.Bson;
using MongoSpyglass.Proxy.Memory;
using MongoSpyglass.Proxy.Profiling;
using MongoSpyglass.Service.Analyzers;
using MongoSpyglass.Service.Analyzers.Rete;
using MongoSpyglass.Service.Data;
using NRules;
using NRules.Fluent;

long firstChance = 0;
AppDomain.CurrentDomain.FirstChanceException += (_, _) => Interlocked.Increment(ref firstChance);

var mode = args[0];
if (mode == "unit") { Unit.Run(); return; }

int fakePort = int.Parse(args[1]);
int proxyPort = fakePort + 1;
string variant = args[2]; // direct | bare | full

MongoDbProxy? proxy = null;
TrafficMonitorService? traffic = null;
ReteAnalyzerEngine? engine = null;
var timers = new List<TimingListener>();
var durationTap = new DurationTap();

if (variant != "direct")
{
    var listeners = new List<ITrafficListener> { durationTap };
    if (variant == "full")
    {
        var raven = new RavenStorageService(NullLogger<RavenStorageService>.Instance);
        var settings = new SettingsService(raven, new ConfigurationBuilder().Build());
        engine = new ReteAnalyzerEngine(raven, settings);
        await engine.StartAsync(CancellationToken.None);
        var hub = new NotificationHubService(new IAnalyzerPlugin[] { engine }, raven);
        var metrics = new MetricsService(engine, new CorrelationRingBuffer());
        traffic = new TrafficMonitorService(raven, hub, metrics);
        timers.Add(new TimingListener("TrafficMonitor", traffic));
        timers.Add(new TimingListener("ReteEngine", engine));
        listeners.AddRange(timers);
    }
    proxy = new MongoDbProxy(new Settings(fakePort, proxyPort), NullLogger<MongoDbProxy>.Instance, listeners);
    await proxy.StartAsync(CancellationToken.None);
    await Task.Delay(300);
}

int port = variant == "direct" ? fakePort : proxyPort;
var client = new MongoClient($"mongodb://127.0.0.1:{port}/?directConnection=true&maxPoolSize=64");
var db = client.GetDatabase("app");
var users = db.GetCollection<BsonDocument>("users");
var big = db.GetCollection<BsonDocument>("big");

// warmup
for (int i = 0; i < 1000; i++) users.Find(new BsonDocument("_id", i % 200)).FirstOrDefault();
firstChance = 0;
foreach (var t in timers) t.Reset();
durationTap.Reset();

// 1. sequential small finds (N+1 shaped: find by _id in a loop)
const int N = 5000;
var lat = new double[N];
for (int i = 0; i < N; i++)
{
    var sw = Stopwatch.StartNew();
    users.Find(new BsonDocument("_id", i % 200)).FirstOrDefault();
    lat[i] = sw.Elapsed.TotalMicroseconds;
}
Array.Sort(lat);
Console.WriteLine($"[{variant}] small find seq: p50={lat[N / 2]:F0}us p90={lat[N * 9 / 10]:F0}us p99={lat[N * 99 / 100]:F0}us mean={lat.Average():F0}us");
long exSmall = firstChance;

// 2. concurrent throughput, 32 workers, 5s
long ops = 0;
var stopAt = DateTime.UtcNow.AddSeconds(5);
await Task.WhenAll(Enumerable.Range(0, 32).Select(w => Task.Run(async () =>
{
    int i = w;
    while (DateTime.UtcNow < stopAt)
    {
        await users.Find(new BsonDocument("_id", i++ % 200)).FirstOrDefaultAsync();
        Interlocked.Increment(ref ops);
    }
})));
Console.WriteLine($"[{variant}] concurrent 32 workers: {ops / 5.0:F0} ops/s");

// 3. large responses (~4MB each)
const int L = 60;
var llat = new double[L];
for (int i = 0; i < L; i++)
{
    var sw = Stopwatch.StartNew();
    var docs = big.Find(FilterDefinition<BsonDocument>.Empty).ToList();
    llat[i] = sw.Elapsed.TotalMilliseconds;
    if (docs.Count != 1000) throw new Exception("bad count " + docs.Count);
}
Array.Sort(llat);
Console.WriteLine($"[{variant}] 4MB find: p50={llat[L / 2]:F1}ms p90={llat[L * 9 / 10]:F1}ms");

// 4. regex query visibility + insert
users.Find(Builders<BsonDocument>.Filter.Regex("name", new BsonRegularExpression("^user1"))).ToList();
await Task.Delay(1500);

if (variant != "direct")
{
    Console.WriteLine($"[{variant}] first-chance exceptions during 5000 seq finds: {exSmall}  (total run: {firstChance})");
    foreach (var t in timers) Console.WriteLine($"[{variant}] listener {t.Name}: calls={t.Calls} mean={t.MeanUs:F1}us max={t.MaxUs:F0}us");
    Console.WriteLine($"[{variant}] proxy-measured durations: n={durationTap.Count} mean={durationTap.Mean:F0}us; missing(null) on responses={durationTap.Missing}");
}
// 5. clean accuracy phase: 1000 sequential finds, compare client vs proxy vs TrafficMonitor
durationTap.Reset();
double clientSum = 0;
for (int i = 0; i < 1000; i++) { var sw = Stopwatch.StartNew(); users.Find(new BsonDocument("_id", i % 200)).FirstOrDefault(); clientSum += sw.Elapsed.TotalMicroseconds; }
await Task.Delay(1000);
if (variant != "direct") Console.WriteLine($"[{variant}] accuracy phase: client-observed mean={clientSum / 1000:F0}us, proxy Stopwatch mean={durationTap.Mean:F0}us (n={durationTap.Count})");
if (traffic != null)
{
    var d = traffic.Traffic.Where(t => t.Command == "find" && t.DurationMs.HasValue).Take(1000).Select(t => t.DurationMs!.Value * 1000).OrderBy(x => x).ToList();
    Console.WriteLine($"[full] accuracy phase: TrafficMonitor-recorded (shown in UI/Raven/Prometheus) mean={d.Average():F0}us p50={d[d.Count / 2]:F0}us min={d[0]:F0}us max={d[^1]:F0}us");
}
if (traffic != null && engine != null)
{
    var snap = traffic.Traffic;
    var withDur = snap.Where(t => t.DurationMs.HasValue).Select(t => t.DurationMs!.Value * 1000).ToList();
    Console.WriteLine($"[full] TrafficMonitor buffer: count={snap.Count}, ThroughputOpsPerSec(last sample)={traffic.ThroughputOpsPerSec}, mean recorded duration={(withDur.Count > 0 ? withDur.Average() : 0):F0}us over {withDur.Count}");
    var regexSeen = snap.Where(t => t.SummaryJson.Contains("$regularExpression") || t.SummaryJson.Contains("^user1")).ToList();
    Console.WriteLine($"[full] regex find visible in feed: {regexSeen.Count} entries, commands: {string.Join(",", regexSeen.Select(r => r.Command + "/" + r.Collection))}");
    Console.WriteLine($"[full] engine healthy={engine.IsHealthy} droppedFacts={engine.DroppedFactCount} insights={engine.GetInsights().Count()}");
    foreach (var g in engine.GetInsights().GroupBy(i => i.Title)) Console.WriteLine($"[full]   insight '{g.Key}' x{g.Count()} e.g. {g.First().Description}");
    var cursorTask = Task.Run(() => engine.ActiveCursorsCount);
    Console.WriteLine($"[full] engine lock acquirable within 3s: {cursorTask.Wait(3000)}");
}
Environment.Exit(0);

class Settings(int target, int listen) : IProxySettingsProvider
{
    public event Action? OnSettingsChanged { add { } remove { } }
    public (IPEndPoint TargetServer, int IncomingPort) GetCurrentSettings() => (new IPEndPoint(IPAddress.Loopback, target), listen);
    public IPAddress GetBindAddress() => IPAddress.Loopback;
}

class TimingListener(string name, ITrafficListener inner) : ITrafficListener
{
    public string Name => name;
    public long Calls, Ticks, MaxTicks;
    public void Reset() { Calls = Ticks = MaxTicks = 0; }
    public double MeanUs => Calls == 0 ? 0 : Ticks * 1e6 / Stopwatch.Frequency / Calls;
    public double MaxUs => MaxTicks * 1e6 / Stopwatch.Frequency;
    public void OnMessage(in ObservedMessage msg)
    {
        var s = Stopwatch.GetTimestamp();
        inner.OnMessage(in msg);
        var d = Stopwatch.GetTimestamp() - s;
        Interlocked.Increment(ref Calls); Interlocked.Add(ref Ticks, d);
        if (d > MaxTicks) MaxTicks = d;
    }
    public void OnConnectionClosed(string c) => inner.OnConnectionClosed(c);
}

class DurationTap : ITrafficListener
{
    public long Count, Missing; double _sum;
    public void Reset() { Count = Missing = 0; _sum = 0; }
    public double Mean => Count == 0 ? 0 : _sum / Count;
    public bool NeedsFullDocument => false;
    public void OnMessage(in ObservedMessage msg)
    {
        if (msg.Tag != "from") return;
        lock (this) { if (msg.DurationMs is { } d) { Count++; _sum += d * 1000; } else Missing++; }
    }
}

static class Unit
{
    static NRules.ISession NewSession()
    {
        var repo = new RuleRepository();
        repo.Load(x => x.From(typeof(TrackRequestRule).Assembly));
        return repo.Compile().CreateSession();
    }

    static void Base(NRules.ISession s, bool withSession, double slowMs = 100)
    {
        s.Insert(new TimeTick());
        s.Insert(new N1DetectionThresholdFact());
        s.Insert(new CursorLeakAlertThresholdFact());
        s.Insert(new SettingsSnapshotFact { SlowQueryThresholdMs = slowMs });
        s.Insert(new CursorStatsFact());
        if (withSession) s.Insert(new SessionFact { Id = "sessions/1" });
        s.Fire();
    }

    const int Cap = 2000;
    static string Fired(int n) => n >= Cap ? $"{n} (HIT CAP -> unbounded loop)" : n.ToString();

    public static unsafe void Run()
    {
        // A1: one request, distinct shape/value hashes
        var s = NewSession(); Base(s, false);
        s.Insert(new RequestObservedFact { RequestId = 1, ConnectionId = "A", Collection = "users", Command = "find", ShapeHash = "S", ValueHash = "V" });
        int fired = s.Fire(Cap);
        Console.WriteLine($"A1 single request (shape!=value): rules fired={Fired(fired)}; windows={string.Join(",", s.Query<RequestWindowFact>().Select(w => $"{w.Hash}:{w.Count}"))}");

        // A1b: ShapeHash == ValueHash (what ComputeQueryHashes yields for {_id: ObjectId/int})
        s = NewSession(); Base(s, false);
        s.Insert(new RequestObservedFact { RequestId = 1, ConnectionId = "A", Collection = "users", Command = "find", ShapeHash = "H", ValueHash = "H" });
        fired = s.Fire(Cap);
        Console.WriteLine($"A1b single request (shape==value): rules fired={Fired(fired)}; windows={string.Join(",", s.Query<RequestWindowFact>().Select(w => $"{w.Hash}:{w.Count}"))}; insights={s.Query<Insight>().Count()}");

        // A2: cursor lifecycle
        s = NewSession(); Base(s, true);
        s.Insert(new ResponseObservedFact { RequestId = 1, ConnectionId = "A", CursorId = 42, Namespace = "app.users", DurationMs = 1 });
        s.Fire(Cap);
        Console.WriteLine($"A2 after find response: open cursors={s.Query<CursorFact>().Count(c => !c.IsClosed)}");
        s.Insert(new KillCursorsRequestedFact { CursorId = 42, ConnectionId = "A" });
        fired = s.Fire(Cap);
        Console.WriteLine($"A2 after killCursors: fired={Fired(fired)} open cursors={s.Query<CursorFact>().Count(c => !c.IsClosed)} (expected 0)");
        s.Insert(new ConnectionClosedFact { ConnectionId = "A" });
        fired = s.Fire(Cap);
        Console.WriteLine($"A2 after connection close: fired={Fired(fired)} cursorFacts={s.Query<CursorFact>().Count()} stats.TotalClosed={s.Query<CursorStatsFact>().First().TotalClosedCount}");

        // A3: retention of ingest facts after 500 complete request/response pairs (no N+1 shape overlap)
        s = NewSession(); Base(s, false);
        for (int i = 0; i < 20; i++)
        {
            s.Insert(new RequestObservedFact { RequestId = i, ConnectionId = "A", Collection = "c" + i, Command = "find", ShapeHash = "S" + i, ValueHash = "V" + i });
            s.Insert(new ResponseObservedFact { RequestId = i, ConnectionId = "A", DurationMs = 1 });
            s.Fire(Cap);
        }
        Console.WriteLine($"A3 after 20 completed req/resp: RequestObserved={s.Query<RequestObservedFact>().Count()} ResponseObserved={s.Query<ResponseObservedFact>().Count()} PendingRequest={s.Query<PendingRequestFact>().Count()} Windows={s.Query<RequestWindowFact>().Count()}");
        var sw = Stopwatch.StartNew();
        var tick = s.Query<TimeTick>().First(); tick.CurrentTime = DateTime.UtcNow; s.Update(tick); s.Fire(Cap);
        Console.WriteLine($"A3 one TimeTick update+Fire with 20 pairs resident: {sw.Elapsed.TotalMilliseconds:F1}ms");

        // A4: slow threshold 500ms, response 200ms
        s = NewSession(); Base(s, false, slowMs: 500);
        s.Insert(new RequestObservedFact { RequestId = 7, ConnectionId = "A", Collection = "c", Command = "find", ShapeHash = "x", ValueHash = "y" });
        s.Fire(Cap);
        s.Insert(new ResponseObservedFact { RequestId = 7, ConnectionId = "A", DurationMs = 200 });
        s.Fire(Cap);
        Console.WriteLine($"A4 threshold=500, response=200ms: PendingRequestFact left={s.Query<PendingRequestFact>().Count()} (expected 0)");

        // B: BSON regex / javascript / undefined in a command
        var arena = new SharpArena.Allocators.ArenaAllocator();
        foreach (var (label, val) in new (string, BsonValue)[] {
            ("string", "abc"), ("regex", new BsonRegularExpression("^a", "i")), ("javascript", new BsonJavaScript("this.a>1")), ("undefined", BsonUndefined.Value) })
        {
            var cmd = new BsonDocument { { "find", "users" }, { "filter", new BsonDocument("name", val) }, { "$db", "app" } };
            var d = ArenaBsonReader.Read(cmd.ToBson(), arena);
            var f = d.IsDefault ? default : d.GetDocument("filter", arena);
            Console.WriteLine($"B filter value {label,-10}: command parse ok={!d.IsDefault}, filter subdoc parse ok={!f.IsDefault}");
        }

        // C: MongoDB zlib compressor emits RFC1950 (zlib) streams
        var raw = new byte[4096]; new Random(1).NextBytes(raw); Array.Fill(raw, (byte)'a', 0, 2048);
        var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Fastest, true)) z.Write(raw);
        var zbytes = ms.ToArray();
        string deflateResult;
        try { using var ds = new DeflateStream(new MemoryStream(zbytes), CompressionMode.Decompress); var o = new MemoryStream(); ds.CopyTo(o); deflateResult = $"ok len={o.Length} equal={o.ToArray().AsSpan().SequenceEqual(raw)}"; }
        catch (Exception e) { deflateResult = "THROWS " + e.GetType().Name + ": " + e.Message; }
        Console.WriteLine($"C zlib payload (header {zbytes[0]:X2} {zbytes[1]:X2}) via DeflateStream (what proxy uses): {deflateResult}");

        // D: PipeWriter.WriteAsync per segment
        var cs = new CountingStream();
        var pw = PipeWriter.Create(cs);
        for (int i = 0; i < 8; i++) pw.WriteAsync(new byte[200]).AsTask().Wait();
        pw.FlushAsync().AsTask().Wait();
        Console.WriteLine($"D 8x PipeWriter.WriteAsync(200B) + 1 FlushAsync => underlying stream writes={cs.Writes}");
        var cs2 = new CountingStream(); var pw2 = PipeWriter.Create(cs2);
        for (int i = 0; i < 8; i++) ((IBufferWriter<byte>)pw2).Write(new byte[200]);
        pw2.FlushAsync().AsTask().Wait();
        Console.WriteLine($"D 8x PipeWriter.Write(200B) + 1 FlushAsync  => underlying stream writes={cs2.Writes}");
    }
}

class CountingStream : Stream
{
    public int Writes;
    public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => true;
    public override long Length => 0; public override long Position { get; set; }
    public override void Flush() { }
    public override int Read(byte[] b, int o, int c) => 0;
    public override long Seek(long o, SeekOrigin s) => 0;
    public override void SetLength(long v) { }
    public override void Write(byte[] b, int o, int c) => Writes++;
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> m, CancellationToken ct = default) { Writes++; return ValueTask.CompletedTask; }
    public override Task WriteAsync(byte[] b, int o, int c, CancellationToken ct) { Writes++; return Task.CompletedTask; }
}
