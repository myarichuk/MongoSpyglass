using System.Net;
using System.Text.Json;
using Spyglass.Collector.Model;
using Spyglass.Collector.Storage;
using static Spyglass.Collector.Tests.Records;

namespace Spyglass.Collector.Tests;

/// <summary><c>/healthz</c> (liveness, the container HEALTHCHECK), <c>/readyz</c>, and <c>spyglass-collector healthcheck</c>.</summary>
public sealed class HealthTests
{
    /// <summary>A store whose writes fail, or block, on demand.</summary>
    private sealed class FaultyStore : IOperationStore
    {
        private readonly MemoryStore _inner = new();
        public volatile bool Fail;
        public readonly ManualResetEventSlim Gate = new(true);
        public void Append(IReadOnlyList<Operation> ops)
        {
            Gate.Wait();
            if (Fail) throw new IOException("disk full");
            _inner.Append(ops);
        }
        public Operation? Get(string id) => _inner.Get(id);
        public List<Operation> Query(OpQuery q) => _inner.Query(q);
        public void Update(IReadOnlyList<OpUpdate> updates) => _inner.Update(updates);
        public int Prune(long olderThanUs, long maxBytes, Action? progress = null) => 0;
        public long Count => _inner.Count;
    }

    private static Collector Start(FaultyStore store, CollectorOptions? o = null)
    {
        var agg = new MemoryStore();
        var c = new Collector(o ?? new CollectorOptions { IngestInsecure = true, Auth = AuthMode.None, Data = null, Workers = 2 }, store, agg, agg, callsites: agg);
        c.Start();
        return c;
    }

    private static CheckView Check(HealthReport h, string name) => new(h.Checks.Single(c => c.Name == name));
    private readonly record struct CheckView(HealthCheck C) { public bool Ok => C.Ok; public string Detail => C.Detail; }

    [Fact]
    public async Task A_running_collector_is_live_and_ready()
    {
        await using var c = Start(new FaultyStore());
        await Task.Delay(300);
        var h = c.Health();
        Assert.True(h.Live && h.Ready, string.Join("; ", h.Checks.Where(x => !x.Ok)));
        Assert.Contains(h.Checks, x => x.Name == "partition-1");
        Assert.Contains("last turn", Check(h, "analysis").Detail);
    }

    [Fact]
    public async Task Failing_storage_fails_liveness()
    {
        var store = new FaultyStore { Fail = true };
        await using var c = Start(store, new CollectorOptions { IngestInsecure = true, Auth = AuthMode.None, Data = null, Workers = 1, HealthStorageFailing = TimeSpan.Zero });
        c.Ingest(Batch(Op(1, "find")), null, null);
        await c.DrainAsync(TimeSpan.FromSeconds(5));
        var h = c.Health();
        Assert.False(h.Live);
        Assert.Contains("disk full", Check(h, "storage").Detail);
        store.Fail = false; // one good write clears it
        c.Ingest(Batch(Op(2, "find")), null, null);
        await c.DrainAsync(TimeSpan.FromSeconds(5));
        Assert.True(c.Health().Live);
    }

    [Fact]
    public async Task A_stuck_stage_fails_liveness()
    {
        var store = new FaultyStore();
        store.Gate.Reset(); // the writer blocks inside its next write
        await using var c = Start(store, new CollectorOptions
        {
            IngestInsecure = true, Auth = AuthMode.None, Data = null, Workers = 1, HealthWriterStall = TimeSpan.FromMilliseconds(500),
        });
        c.Ingest(Batch(Op(1, "find")), null, null);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (c.Health().Live && DateTime.UtcNow < deadline) await Task.Delay(100);
        var h = c.Health();
        Assert.False(h.Live);
        Assert.False(Check(h, "storage-writer").Ok);
        Assert.True(Check(h, "analysis").Ok);
        store.Gate.Set();
    }

    [Fact]
    public async Task A_saturated_ingest_queue_fails_readiness_only()
    {
        await using var c = Start(new FaultyStore(), new CollectorOptions { IngestInsecure = true, Auth = AuthMode.None, Data = null, Workers = 1, QueueBytes = 20_000 });
        using var gate = new ManualResetEventSlim();
        var parked = c.Analysis.Invoke(() => { gate.Wait(); return 0; }); // records stay charged until analysed
        var rid = 0;
        while (c.QueuedBytes <= 18_000) Assert.Equal(204, c.Ingest(Batch(Op(++rid, "find")), null, null).Status);
        Assert.Equal(429, c.Ingest(Batch(Op(++rid, "find")), null, null).Status);
        var h = c.Health();
        Assert.True(h.Live);                                 // shedding load is not a reason to restart
        Assert.False(h.Ready);
        Assert.False(Check(h, "ingest-queue").Ok);
        gate.Set();
        await parked;
        await c.DrainAsync(TimeSpan.FromSeconds(5));
        Assert.True(c.Health().Ready);
    }

    [Fact]
    public async Task Endpoints_and_the_probe()
    {
        await using var s = await Server.StartAsync(new CollectorOptions
        {
            Bind = new IPEndPoint(IPAddress.Loopback, 0), Token = "t", Auth = AuthMode.Password, AdminPassword = "pw", Data = null, Workers = 1,
        });
        using var http = new HttpClient { BaseAddress = s.BaseAddress };
        foreach (var path in (string[])["/healthz", "/readyz"])
        {
            var r = await http.GetAsync(path); // no auth (03 §8)
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
            Assert.Equal("ok", doc.GetProperty("status").GetString());
            Assert.True(doc.GetProperty("checks").GetProperty("storage").GetProperty("ok").GetBoolean());
        }
        var bind = $"127.0.0.1:{s.BaseAddress.Port}";
        Assert.Equal(0, await HealthProbe.RunAsync(["--bind", bind]));
        Assert.Equal(0, await HealthProbe.RunAsync(["--bind", $"0.0.0.0:{s.BaseAddress.Port}"])); // a wildcard bind is probed on loopback
        Assert.Equal(1, await HealthProbe.RunAsync(["--bind", bind, "--path", "/nope"]));
        Assert.Equal(1, await HealthProbe.RunAsync(["--bind", $"127.0.0.1:{Ports()}"]));      // nothing listening
    }

    private static int Ports()
    {
        using var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }
}
