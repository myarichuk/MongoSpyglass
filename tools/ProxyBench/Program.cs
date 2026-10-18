// A/B performance harness for the 01 §14 budget (06 §4): real driver → (direct | proxy modes) → real mongod.
// mongod, the proxy and this client are separate processes.
//
//   dotnet run -c Release -- --mongod <bin dir> --proxy <spyglass-proxy.dll> [--rounds 2] [--json out.json]
//
// Modes:
//   direct    driver → mongod
//   bare      driver → proxy with observation off (limits.observeQueueBytes=0): forwarding path only
//   observe   driver → proxy, headless (metrics, no export)
//   export    driver → proxy exporting every record to a local sink that answers 204

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;

var opts = ParseArgs(args);
var mongodBin = opts.GetValueOrDefault("mongod") ?? throw new ArgumentException("--mongod <bin dir>");
var proxyDll = opts.GetValueOrDefault("proxy") ?? throw new ArgumentException("--proxy <spyglass-proxy.dll>");
var rounds = int.Parse(opts.GetValueOrDefault("rounds") ?? "2");
var latencyOps = int.Parse(opts.GetValueOrDefault("latencyOps") ?? "20000");
var throughputSeconds = int.Parse(opts.GetValueOrDefault("seconds") ?? "8");
var modes = (opts.GetValueOrDefault("modes") ?? "direct,bare,observe,export").Split(',');
// e.g. --proxyEnv DOTNET_SYSTEM_NET_SOCKETS_INLINE_COMPLETIONS=1;DOTNET_ThreadPool_UnfairSemaphoreSpinLimit=0
// --trace <dir>: CPU-sample the proxy (dotnet-trace, cpu-sampling) over each throughput window
var traceDir = opts.GetValueOrDefault("trace");
var proxyEnv = (opts.GetValueOrDefault("proxyEnv") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)
    .Select(kv => kv.Split('=', 2)).ToDictionary(kv => kv[0], kv => kv[1]);

var root = Path.Combine(Path.GetTempPath(), "spyglass-bench-" + Guid.NewGuid().ToString("N")[..6]);
Directory.CreateDirectory(root);
var mongoPort = FreePort();
using var mongod = Start(Path.Combine(mongodBin, "mongod"), new Dictionary<string, string>(),
    ["--port", mongoPort.ToString(), "--bind_ip", "127.0.0.1", "--dbpath", root, "--logpath", Path.Combine(root, "mongod.log"),
     "--wiredTigerCacheSizeGB", "0.5", "--setParameter", "diagnosticDataCollectionEnabled=false"]);
await WaitPort(mongoPort);

// fixtures: small docs for point lookups, 4 KB docs for the 4 MiB reply
var direct = new MongoClient($"mongodb://127.0.0.1:{mongoPort}/?directConnection=true");
var db = direct.GetDatabase("bench");
await db.DropCollectionAsync("small");
await db.DropCollectionAsync("big");
await db.GetCollection<BsonDocument>("small").InsertManyAsync(Enumerable.Range(0, 1000).Select(i => new BsonDocument { ["_id"] = i, ["name"] = "user" + i, ["v"] = i % 13 }));
await db.GetCollection<BsonDocument>("big").InsertManyAsync(Enumerable.Range(0, 1024).Select(i => new BsonDocument { ["_id"] = i, ["p"] = new string('x', 4000) }));

using var sink = new Sink();
var results = new List<Dictionary<string, object>>();
for (var r = 0; r < rounds; r++)
{
    foreach (var mode in modes)
    {
        Process? proxy = null;
        var port = mongoPort;
        int? metricsPort = null;
        if (mode != "direct")
        {
            port = FreePort();
            metricsPort = FreePort();
            var extra = mode switch
            {
                "bare" => new[] { "--limits.observeQueueBytes", "0" },
                "export" => ["--collector.endpoint", $"http://127.0.0.1:{sink.Port}/ingest/v0"],
                _ => []
            };
            proxy = Start("dotnet", proxyEnv, [proxyDll, "--upstream.uri", $"mongodb://127.0.0.1:{mongoPort}/", "--listen.basePort", port.ToString(),
                "--listen.portRange", $"{port},{port}", "--metrics.bind", $"127.0.0.1:{metricsPort}", "--logLevel", "warn", .. extra]);
            await WaitPort(port);
            await WaitPort(metricsPort.Value);
        }

        try
        {
            var result = await RunMode(mode, port, metricsPort, proxy?.Id, r);
            result["round"] = r;
            results.Add(result);
            Console.WriteLine(string.Join("  ", result.Select(kv => $"{kv.Key}={Fmt(kv.Value)}")));
        }
        finally
        {
            if (proxy != null)
            {
                proxy.Kill(true);
                await proxy.WaitForExitAsync();
                proxy.Dispose();
            }
        }
    }
}

Console.WriteLine();
Console.WriteLine("median over rounds; deltas vs direct");
Console.WriteLine($"{"mode",-8} {"p50 µs",8} {"p99 µs",8} {"+p50",7} {"+p99",7} {"ops/s",8} {"thr %",6} {"4MiB ms",8} {"4MiB %",7} {"B/frame",8} {"cpu µs/op",9}");
var byMode = results.GroupBy(x => (string)x["mode"]).ToDictionary(g => g.Key, g => g.ToList());
double Med(string mode, string key) => byMode[mode].Select(x => Convert.ToDouble(x[key])).Order().ElementAt(byMode[mode].Count / 2);
foreach (var mode in modes)
{
    var p50 = Med(mode, "p50_us");
    var p99 = Med(mode, "p99_us");
    var thr = Med(mode, "ops_per_s");
    var big = Med(mode, "big_ms");
    var hasDirect = byMode.ContainsKey("direct");
    var bpf = byMode[mode][0].ContainsKey("alloc_bytes_per_frame") ? Med(mode, "alloc_bytes_per_frame").ToString("F1") : "-";
    var cpu = byMode[mode][0].ContainsKey("proxy_cpu_us_per_op") ? Med(mode, "proxy_cpu_us_per_op").ToString("F1") : "-";
    Console.WriteLine($"{mode,-8} {p50,8:F0} {p99,8:F0} {(hasDirect ? p50 - Med("direct", "p50_us") : 0),7:F0} {(hasDirect ? p99 - Med("direct", "p99_us") : 0),7:F0} " +
                      $"{thr,8:F0} {(hasDirect ? 100 * thr / Med("direct", "ops_per_s") : 100),6:F1} {big,8:F2} {(hasDirect ? 100 * big / Med("direct", "big_ms") : 100),7:F1} {bpf,8} {cpu,9}");
}

if (opts.GetValueOrDefault("json") is { } jsonPath)
    await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));

mongod.Kill(true);
try { Directory.Delete(root, true); } catch (IOException) { }
return 0;

// ---------------------------------------------------------------------------------------

async Task<Dictionary<string, object>> RunMode(string mode, int port, int? metricsPort, int? proxyPid, int round)
{
    var settings = MongoClientSettings.FromConnectionString($"mongodb://127.0.0.1:{port}/?directConnection=true&maxPoolSize=64");
    var client = new MongoClient(settings);
    var small = client.GetDatabase("bench").GetCollection<BsonDocument>("small");
    var big = client.GetDatabase("bench").GetCollection<BsonDocument>("big");

    // warm-up (JIT, pools, the proxy's tiered compilation)
    for (var i = 0; i < 3000; i++)
        await small.Find(new BsonDocument("_id", i % 1000)).FirstOrDefaultAsync();

    // 1. sequential latency of point lookups
    var lat = new double[latencyOps];
    for (var i = 0; i < latencyOps; i++)
    {
        var t = Stopwatch.GetTimestamp();
        await small.Find(new BsonDocument("_id", i % 1000)).FirstOrDefaultAsync();
        lat[i] = Stopwatch.GetElapsedTime(t).TotalMicroseconds;
    }
    Array.Sort(lat);

    // 2. throughput, 32 concurrent clients; proxy CPU and allocation over the same window
    var before = metricsPort is { } mp ? await Scrape(mp) : null;
    Process? trace = null;
    if (traceDir != null && proxyPid is { } pid)
    {
        Directory.CreateDirectory(traceDir);
        var tool = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", "tools", "dotnet-trace");
        trace = Start(tool, new Dictionary<string, string> { ["DOTNET_ROOT"] = Path.GetDirectoryName(Environment.ProcessPath)! },
            ["collect", "-p", pid.ToString(), "--profile", "dotnet-sampled-thread-time", "--duration", TimeSpan.FromSeconds(Math.Max(1, throughputSeconds - 2)).ToString(@"hh\:mm\:ss"),
             "-o", Path.Combine(traceDir, $"{mode}-r{round}.nettrace")]);
    }
    long ops = 0;
    using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(throughputSeconds));
    var sw = Stopwatch.StartNew();
    await Task.WhenAll(Enumerable.Range(0, 32).Select(async w =>
    {
        var i = w;
        while (!stop.IsCancellationRequested)
        {
            await small.Find(new BsonDocument("_id", i++ % 1000)).FirstOrDefaultAsync();
            Interlocked.Increment(ref ops);
        }
    }));
    var elapsed = sw.Elapsed.TotalSeconds;
    var after = metricsPort is { } mp2 ? await Scrape(mp2) : null;
    if (trace != null)
    {
        await trace.WaitForExitAsync();
        trace.Dispose();
    }

    // 3. ~4 MiB single reply: 1024 × 4 KB in one batch
    var bigMs = new List<double>();
    for (var i = 0; i < 40; i++)
    {
        var t = Stopwatch.GetTimestamp();
        var docs = await big.Find("{}").ToListAsync(); // default first batch is 101; force one batch below
        bigMs.Add(Stopwatch.GetElapsedTime(t).TotalMilliseconds);
        if (docs.Count != 1024) throw new InvalidOperationException("fixture");
    }
    var oneBatch = new List<double>();
    for (var i = 0; i < 40; i++)
    {
        var t = Stopwatch.GetTimestamp();
        using var cursor = await big.FindAsync("{}", new FindOptions<BsonDocument> { BatchSize = 1024 });
        await cursor.MoveNextAsync();
        oneBatch.Add(Stopwatch.GetElapsedTime(t).TotalMilliseconds);
    }
    oneBatch.Sort();

    var result = new Dictionary<string, object>
    {
        ["mode"] = mode,
        ["p50_us"] = lat[lat.Length / 2],
        ["p99_us"] = lat[(int)(lat.Length * 0.99)],
        ["p999_us"] = lat[(int)(lat.Length * 0.999)],
        ["ops_per_s"] = ops / elapsed,
        ["big_ms"] = oneBatch[oneBatch.Count / 2],
    };
    if (before != null && after != null)
    {
        var frames = after["frames"] - before["frames"];
        result["alloc_bytes_per_frame"] = (after["allocated"] - before["allocated"]) / Math.Max(1, frames);
        result["proxy_cpu_us_per_op"] = (after["cpu"] - before["cpu"]) / Math.Max(1, ops);
        result["proxy_rss_mb"] = after["rss"] / 1024 / 1024;
        result["dropped_no_slot"] = after["no_slot"];
    }
    return result;
}

static async Task<Dictionary<string, double>> Scrape(int port)
{
    using var http = new HttpClient();
    var text = await http.GetStringAsync($"http://127.0.0.1:{port}/metrics");
    var d = new Dictionary<string, double> { ["frames"] = 0 };
    foreach (var line in text.Split('\n'))
    {
        if (line.StartsWith("spyglass_proxy_frames_total{")) d["frames"] += double.Parse(line.Split(' ')[1]);
        else if (line.StartsWith("spyglass_proxy_process_allocated_bytes ")) d["allocated"] = double.Parse(line.Split(' ')[1]);
        else if (line.StartsWith("spyglass_proxy_process_cpu_microseconds ")) d["cpu"] = double.Parse(line.Split(' ')[1]);
        else if (line.StartsWith("spyglass_proxy_process_resident_bytes ")) d["rss"] = double.Parse(line.Split(' ')[1]);
        else if (line.StartsWith("spyglass_proxy_observation_dropped_total{reason=\"no_slot\"}")) d["no_slot"] = double.Parse(line.Split(' ')[1]);
    }
    d.TryAdd("no_slot", 0);
    return d;
}

static Process Start(string file, Dictionary<string, string> env, IEnumerable<string> args)
{
    var psi = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    foreach (var (k, v) in env) psi.Environment[k] = v;
    foreach (var a in args) psi.ArgumentList.Add(a);
    var p = Process.Start(psi)!;
    p.OutputDataReceived += (_, _) => { };
    p.ErrorDataReceived += (_, e) => { if (e.Data != null && e.Data.Contains(" ERR ")) Console.Error.WriteLine(e.Data); };
    p.BeginOutputReadLine();
    p.BeginErrorReadLine();
    return p;
}

static async Task WaitPort(int port)
{
    for (var i = 0; i < 300; i++)
    {
        try
        {
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await s.ConnectAsync(IPAddress.Loopback, port);
            return;
        }
        catch (SocketException) { await Task.Delay(100); }
    }
    throw new TimeoutException($"port {port}");
}

static int FreePort()
{
    using var l = new TcpListener(IPAddress.Loopback, 0);
    l.Start();
    return ((IPEndPoint)l.LocalEndpoint).Port;
}

static Dictionary<string, string> ParseArgs(string[] a)
{
    var d = new Dictionary<string, string>();
    for (var i = 0; i + 1 < a.Length; i += 2) d[a[i].TrimStart('-')] = a[i + 1];
    return d;
}

static string Fmt(object o) => o is double d ? d.ToString("F1") : o.ToString()!;

/// <summary>Ingest sink: accepts and discards, 204.</summary>
sealed class Sink : IDisposable
{
    private readonly HttpListener _l = new();
    public int Port { get; } = FreePortStatic();

    public Sink()
    {
        _l.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _l.Start();
        _ = Task.Run(async () =>
        {
            while (_l.IsListening)
            {
                try
                {
                    var ctx = await _l.GetContextAsync();
                    await ctx.Request.InputStream.CopyToAsync(Stream.Null);
                    ctx.Response.StatusCode = 204;
                    ctx.Response.Close();
                }
                catch (Exception) { return; }
            }
        });
    }

    private static int FreePortStatic()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }

    public void Dispose() => _l.Close();
}
