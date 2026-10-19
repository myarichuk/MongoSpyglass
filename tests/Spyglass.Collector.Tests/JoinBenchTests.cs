using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using MongoDB.Bson;
using Spyglass.Collector.Model;
using Spyglass.Collector.Pipeline;
using static Spyglass.Collector.Tests.Records;

namespace Spyglass.Collector.Tests;

/// <summary>
/// The M2 gate (README §4): the collector joins synthetic exporter records at ≥ 99.9% on the harness, and 03 §11's
/// join CPU ≤ 5 µs per op. Release only: Debug numbers mean nothing.
/// </summary>
[Collection(Isolated.Name)]
public sealed class JoinBenchTests
{
#if DEBUG
    private static readonly bool IsDebugBuild = true;
#else
    private static readonly bool IsDebugBuild = false;
#endif

    /// <summary>
    /// Two producers over HTTP, as deployed: the proxy flushing every ~50 ms, the exporter on its own schedule, so
    /// a cmd lands anywhere from before its op to seconds after it. 64 connections, 4 proxy POSTers.
    /// </summary>
    [Fact]
    public async Task Join_rate_on_the_harness()
    {
        if (IsDebugBuild) Assert.Skip("measurement needs a Release build");
        await using var server = await Server.StartAsync(new CollectorOptions
        {
            Bind = new IPEndPoint(IPAddress.Loopback, 0), Token = "t", Auth = AuthMode.None, Data = null,
        });
        using var http = new HttpClient { BaseAddress = server.BaseAddress };
        async Task Post(IEnumerable<string> lines, string producer)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "/ingest/v0") { Content = new ByteArrayContent(Batch([.. lines])) };
            req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-ndjson");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "t");
            req.Headers.Add("X-Spyglass-Producer", producer);
            using var r = await http.SendAsync(req);
            Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        }

        const int Connections = 64, PerTick = 500, Ticks = 100; // 50k ops at 10k ops/s
        await Post([Hello(), .. Enumerable.Range(1, Connections).Select(i => ExporterConn(i, "bench", sid: 1000 + i))], "proxy");
        var rnd = new Random(5);
        var sw = Stopwatch.StartNew();
        var pendingCmds = new List<(long DueMs, string Line)>();
        List<string> TakeDue()
        {
            var now = sw.ElapsedMilliseconds; // one reading: what is taken is exactly what is removed
            var due = pendingCmds.Where(c => c.DueMs <= now).Select(c => c.Line).ToList();
            pendingCmds.RemoveAll(c => c.DueMs <= now);
            return due;
        }
        var requestId = 0;
        for (var tick = 0; tick < Ticks; tick++)
        {
            var ops = new List<string>(PerTick);
            var early = new List<string>();
            for (var i = 0; i < PerTick; i++)
            {
                var conn = 1 + rnd.Next(Connections);
                var rid = ++requestId;
                var ts = T0 + rid * 100L;
                var cmd = new BsonDocument { ["find"] = "orders", ["filter"] = new BsonDocument("_id", rid) };
                ops.Add(Op(rid, "find", cmd, ts: ts, sid: 1000 + conn, connSeq: conn));
                // the exporter's view: 10 % flushed before the proxy's, the rest 0–3 s after
                var cmdLine = Cmd(rid, "find", ts: ts - 150, sid: 1000 + conn);
                if (rnd.Next(10) == 0) early.Add(cmdLine);
                else pendingCmds.Add((sw.ElapsedMilliseconds + rnd.Next(3000), cmdLine));
            }
            if (early.Count > 0) await Post(early, "spyglass-dotnet");
            await Post(ops, "proxy");
            foreach (var chunk in TakeDue().Chunk(2000)) await Post(chunk, "spyglass-dotnet");
            var next = (tick + 1) * 50;
            if (sw.ElapsedMilliseconds < next) await Task.Delay((int)(next - sw.ElapsedMilliseconds));
        }
        while (pendingCmds.Count > 0)
        {
            foreach (var chunk in TakeDue().Chunk(2000)) await Post(chunk, "spyglass-dotnet");
            await Task.Delay(50);
        }
        await server.Collector.DrainAsync(TimeSpan.FromSeconds(30));

        var j = server.Collector.Join;
        var total = Ticks * PerTick;
        Assert.Equal(total * 2 + Connections + 1, server.Collector.RecordsAccepted); // the harness delivered every record
        var joined = j.Joined + j.JoinedLate;
        var rate = joined / (double)total;
        Console.WriteLine($"[join] {total} ops / cmds in {sw.Elapsed.TotalSeconds:F1} s: joined {joined} ({rate:P3}): in time {j.Joined}, late {j.JoinedLate}; " +
                          $"orphans {j.OrphanExpired + j.OrphanNoKey}, conflicts {j.Conflicts}, evicted {j.EvictedOps}, waited wire-only {j.WaitedWireOnly}; " +
                          $"waiting ops {j.PendingOps} cmds {j.PendingCmds}, already joined {j.AlreadyJoined}, duplicates {j.Duplicates}, late discarded {j.LateDiscarded}; " +
                          $"accepted {server.Collector.RecordsAccepted}, rejected {server.Collector.RecordsRejected}, op duplicates {server.Collector.Partitions.Sum(p => p.Duplicates)}");
        Assert.True(rate >= 0.999, $"join rate {rate:P3}");
        Assert.Equal(0, j.Conflicts);
    }

    /// <summary>03 §11 join CPU per op: the join engine alone, an op and its cmd each, in both arrival orders.</summary>
    [Fact]
    public void Join_cpu_per_op()
    {
        if (IsDebugBuild) Assert.Skip("measurement needs a Release build");
        const int N = 200_000;
        var producers = new ProducerRegistry();
        producers.Touch(Exporter, null, 0);
        var counters = new JoinCounters();
        var engine = new JoinEngine(counters, producers, 1 << 20, 1 << 20, 1 << 17);
        var members = new[] { "localhost:27017", "localhost:27018" };
        var ops = new Operation[N];
        var cmds = new CmdRecord[N];
        for (var i = 0; i < N; i++)
        {
            var op = new OpRecord { Src = Records.Proxy, Member = members[i & 1], ServerConnectionId = 1000 + i % 64, RequestId = i, Command = "find", Db = "shop", Status = "ok" };
            ops[i] = new Operation { Id = Ulid.New(1), Op = op };
            cmds[i] = new CmdRecord { Src = Exporter, ProxyId = Records.Proxy, Member = op.Member, ServerConnectionId = op.ServerConnectionId, RequestId = i, Command = "find", Db = "shop", Status = "ok" };
        }
        var output = new PartitionOutput();
        // warm-up
        for (var i = 0; i < 1000; i++) { engine.OnOp(ops[i], 0, 10_000_000, true, output); engine.OnCmd(cmds[i], cmds[i].Key!.Value, 0, output); }
        output = new PartitionOutput();

        var cpu0 = Process.GetCurrentProcess().TotalProcessorTime;
        var alloc0 = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        for (var i = 1000; i < N; i++)
        {
            if ((i & 1) == 0) { engine.OnOp(ops[i], i, 10_000_000, true, output); engine.OnCmd(cmds[i], cmds[i].Key!.Value, i, output); }
            else { engine.OnCmd(cmds[i], cmds[i].Key!.Value, i, output); engine.OnOp(ops[i], i, 10_000_000, true, output); }
            if ((i & 1023) == 0) { engine.Expire(i, output); output = new PartitionOutput(); }
        }
        var us = sw.Elapsed.TotalMicroseconds / (N - 1000);
        var bytes = (GC.GetAllocatedBytesForCurrentThread() - alloc0) / (double)(N - 1000);
        Console.WriteLine($"[join] engine: {us:F2} µs and {bytes:F0} B per op+cmd pair (wall clock, one thread)");
        Assert.Equal(N, counters.Joined);
        Assert.True(us <= 5, $"join {us:F2} µs per op (03 §11: ≤ 5 µs)");
    }
}
