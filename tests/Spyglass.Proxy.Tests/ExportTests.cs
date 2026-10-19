using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using Spyglass.Proxy.Tests.Infrastructure;

namespace Spyglass.Proxy.Tests;

/// <summary>An HTTP endpoint that records ingest batches and answers with a configurable status.</summary>
public sealed class FakeCollector : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly Task _loop;
    public int Port { get; }
    public string Endpoint => $"http://127.0.0.1:{Port}/ingest/v0";
    public ConcurrentQueue<(Dictionary<string, string> Headers, List<JsonElement> Records)> Batches { get; } = new();
    public int Requests;
    public Func<int> Status { get; set; } = () => 204;

    public FakeCollector()
    {
        Port = Ports.FreeRange(1);
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _listener.Start();
        _loop = Task.Run(LoopAsync);
    }

    public IEnumerable<JsonElement> All => Batches.SelectMany(b => b.Records);

    private async Task LoopAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch (Exception) { return; }
            Interlocked.Increment(ref Requests);
            using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
            var body = await reader.ReadToEndAsync();
            var status = Status();
            if (status is >= 200 and < 300)
            {
                var headers = ctx.Request.Headers.AllKeys.ToDictionary(k => k!, k => ctx.Request.Headers[k]!);
                var records = body.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonDocument.Parse(l).RootElement.Clone()).ToList();
                Batches.Enqueue((headers, records));
            }
            ctx.Response.StatusCode = status;
            ctx.Response.Close();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _listener.Stop();
        _listener.Close();
        await _loop;
    }
}

/// <summary>01 §11 / 02 §5 export, against a fake collector.</summary>
public sealed class ExportTests(StandaloneFixture f) : IClassFixture<StandaloneFixture>
{
    [Fact]
    public async Task Records_are_posted_as_ndjson_with_headers_and_advertised_in_hello()
    {
        await using var collector = new FakeCollector();
        await using var proxy = await ProxyHost.StartAsync(f.Mongo.DirectUri, listeners: 1, configure: a =>
        {
            a["collector.endpoint"] = collector.Endpoint;
            a["collector.token"] = "ingest-token-1";
            a["collector.flushIntervalMs"] = "50";
        });

        var client = new MongoClient(proxy.Uri());
        var hello = await client.GetDatabase("admin").RunCommandAsync<BsonDocument>(new BsonDocument("hello", 1));
        Assert.Equal(collector.Endpoint, hello["$spyglass"]["ingest"].AsString);
        Assert.Equal("ingest-token-1", hello["$spyglass"]["token"].AsString);

        var coll = client.GetDatabase("exp").GetCollection<BsonDocument>("c");
        for (var i = 0; i < 50; i++)
            await coll.InsertOneAsync(new BsonDocument("i", i));

        await ReplicaSetTests.WaitUntil(() => collector.All.Count(r => r.GetProperty("type").GetString() == "op" && r.GetProperty("command").GetString() == "insert") >= 50, TimeSpan.FromSeconds(10));
        var (headers, _) = collector.Batches.First();
        Assert.Equal("application/x-ndjson", headers["Content-Type"]);
        Assert.Equal("Bearer ingest-token-1", headers["Authorization"]);
        Assert.Equal("proxy", headers["X-Spyglass-Producer"]);
        Assert.Equal(proxy.Server.ProxyId.ToString(), headers["X-Spyglass-Src"]);

        var proxyRecord = collector.All.First(r => r.GetProperty("type").GetString() == "proxy");
        Assert.Equal("standalone", proxyRecord.GetProperty("topology").GetString());
        Assert.Equal(1, proxyRecord.GetProperty("schema").GetInt32());
        Assert.All(collector.All, r => Assert.True(r.GetProperty("ts").GetInt64() > 1_700_000_000_000_000)); // µs since epoch
    }

    [Fact]
    public async Task Failing_collector_never_affects_forwarding()
    {
        await using var collector = new FakeCollector { Status = () => 500 };
        await using var proxy = await ProxyHost.StartAsync(f.Mongo.DirectUri, listeners: 1, configure: a =>
        {
            a["collector.endpoint"] = collector.Endpoint;
            a["collector.flushIntervalMs"] = "20";
        });
        var coll = new MongoClient(proxy.Uri()).GetDatabase("exp").GetCollection<BsonDocument>("fail" + Guid.NewGuid().ToString("N")[..6]);
        for (var i = 0; i < 200; i++)
            await coll.InsertOneAsync(new BsonDocument("i", i));
        Assert.Equal(200, await coll.CountDocumentsAsync("{}"));

        await ReplicaSetTests.WaitUntil(() => Volatile.Read(ref collector.Requests) >= 2, TimeSpan.FromSeconds(10));
        var metrics = await proxy.HttpGetAsync("/metrics");
        var dropped = metrics.Split('\n').Single(l => l.StartsWith("spyglass_proxy_observation_dropped_total{reason=\"export_dropped\"}"));
        Assert.True(long.Parse(dropped.Split(' ')[1]) > 0, dropped);
        Assert.Empty(collector.Batches);
    }

    [Fact]
    public async Task Collector_down_entirely()
    {
        var deadPort = Ports.FreeRange(1);
        await using var proxy = await ProxyHost.StartAsync(f.Mongo.DirectUri, listeners: 1, configure: a =>
            a["collector.endpoint"] = $"http://127.0.0.1:{deadPort}/ingest/v0");
        var coll = new MongoClient(proxy.Uri()).GetDatabase("exp").GetCollection<BsonDocument>("down");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 100; i++)
            await coll.InsertOneAsync(new BsonDocument("i", i));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), sw.Elapsed.ToString());
    }
}
