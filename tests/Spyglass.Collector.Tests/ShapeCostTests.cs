using System.Diagnostics;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using Spyglass.Collector.Shapes;
using Spyglass.Collector.Tests;

namespace Spyglass.Collector.Tests;

/// <summary>Per-op cost of stage 1's pieces on a driver-shaped find (Release only; prints, no gate).</summary>
[Collection(Isolated.Name)]
public sealed class ShapeCostTests
{
    [Fact]
    public void Stage_one_costs()
    {
#if DEBUG
        Assert.Skip("measurement needs a Release build");
#endif
        var cmd = new BsonDocument
        {
            ["find"] = "orders", ["filter"] = new BsonDocument { ["customerId"] = 42, ["status"] = "A", ["total"] = new BsonDocument("$gt", 100) },
            ["sort"] = new BsonDocument("total", -1), ["limit"] = 10, ["$db"] = "shop", ["lsid"] = new BsonDocument("id", "5b8516c3a99c21e7"),
        };
        var bytes = cmd.ToBson();
        const int n = 50_000;
        var allocs = new Dictionary<string, double>();
        double Time(Action a, [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(a))] string name = "")
        {
            for (var i = 0; i < 2000; i++) a();
            var b0 = GC.GetAllocatedBytesForCurrentThread();
            var sw = Stopwatch.StartNew();
            for (var i = 0; i < n; i++) a();
            allocs[name] = (GC.GetAllocatedBytesForCurrentThread() - b0) / (double)n;
            return sw.Elapsed.TotalMicroseconds / n;
        }
        var driverDecode = Time(() => BsonSerializer.Deserialize<BsonDocument>(bytes));
        var decode = Time(() => FastBson.Decode(bytes));
        var doc = BsonSerializer.Deserialize<BsonDocument>(bytes);
        var tree = Time(() => ReferenceShapeNormalizer.Normalize("find", "shop", "orders", FastBson.Decode(bytes), false, true));
        var validate = Time(() => BsonSpan.Validate(bytes));
        var known = new HashSet<string>();
        var both = Time(() => ShapeNormalizer.Normalize("find", "shop", "orders", bytes, false, true, known));
        var shapeOnly = Time(() => ShapeNormalizer.Normalize("find", "shop", "orders", bytes, false, false, known));
        both = Time(() => ShapeNormalizer.Normalize("find", "shop", "orders", bytes, false, true, known)); // again: tiered JIT had not settled
        var canonical = Time(() => ShapeNormalizer.Canonical(doc));
        var ulid = Time(() => Model.Ulid.New(1_700_000_000_000));
        var rnd = new Random(7);
        var benchCmds = Enumerable.Range(0, 2000).Select(_ =>
        {
            var d = new BsonDocument { ["find"] = "orders", ["filter"] = new BsonDocument { ["f" + rnd.Next(50)] = rnd.Next(1000), ["status"] = "A" }, ["limit"] = 10, ["lsid"] = new BsonDocument("id", "5b8516c3a99c21e7") };
            d["$db"] = "shop";
            return d.ToBson();
        }).ToArray();
        var k = 0;
        var known2 = new HashSet<string>();
        var benchNorm = Time(() => ShapeNormalizer.Normalize("find", "shop", "orders", benchCmds[k++ % benchCmds.Length], false, true, known2));
        var k2 = 0;
        var benchShape = Time(() => ShapeNormalizer.Normalize("find", "shop", "orders", benchCmds[k2++ % benchCmds.Length], false, false, known2));
        Console.WriteLine($"[stage1] the bench's commands: shape+value {benchNorm:F2} µs, shape {benchShape:F2} µs");
        // the same with another thread parsing records (nothing shared but the process): hardware or sharing?
        var neighbourLine = System.Text.Encoding.UTF8.GetBytes(Records.Op(1, "find", cmd, sid: 5, connSeq: 5, latencyUs: 300, nReturned: 3));
        var stop = false;
        var neighbour = new Thread(() => { while (!Volatile.Read(ref stop)) Ingest.RecordParser.Parse(neighbourLine, out _, out _); }) { IsBackground = true };
        neighbour.Start();
        var k3 = 0;
        var withNeighbour = Time(() => ShapeNormalizer.Normalize("find", "shop", "orders", benchCmds[k3++ % benchCmds.Length], false, true, known2));
        Volatile.Write(ref stop, true);
        neighbour.Join();
        Console.WriteLine($"[stage1] the same while another thread parses records: {withNeighbour:F2} µs");
        // and beside two threads streaming through 64 MB each: caches and memory bandwidth, no CPU sharing
        stop = false;
        var streamers = Enumerable.Range(0, 2).Select(_ => new Thread(() =>
        {
            var big = new long[8 << 20];
            long acc = 0;
            while (!Volatile.Read(ref stop)) for (var i = 0; i < big.Length; i += 8) { acc += big[i]; big[i] = acc; }
        }) { IsBackground = true }).ToList();
        streamers.ForEach(t => t.Start());
        var k4 = 0;
        var withStreamers = Time(() => ShapeNormalizer.Normalize("find", "shop", "orders", benchCmds[k4++ % benchCmds.Length], false, true, known2));
        Volatile.Write(ref stop, true);
        streamers.ForEach(t => t.Join());
        Console.WriteLine($"[stage1] the same beside two memory-streaming threads: {withStreamers:F2} µs");
        var line = System.Text.Encoding.UTF8.GetBytes(Records.Op(1, "find", cmd, sid: 5, connSeq: 5, latencyUs: 300, nReturned: 3));
        var parse = Time(() => Ingest.RecordParser.Parse(line, out _, out _));
        Console.WriteLine($"[stage1] parse one op record ({line.Length} B): {parse:F2} µs, {allocs.Last().Value:F0} B");
        Console.WriteLine("[stage1] bytes allocated per call: " + string.Join(", ", allocs.Select(kv => $"{kv.Key[..Math.Min(40, kv.Key.Length)]} {kv.Value:F0}")));
        Console.WriteLine($"[stage1] from the bytes: validate {validate:F2} µs, normalize shape+value {both:F2} µs, shape only {shapeOnly:F2} µs; " +
                          $"the tree way (decode + build): {tree:F2} µs (decode alone {decode:F2}, the driver's {driverDecode:F2}); canonical EJSON {canonical:F2} µs, ulid {ulid:F2} µs");
    }
}

/// <summary>
/// Stage 1 on its own: the bench's records through Ingest (no HTTP, no client), one partition, its CPU per record, in four
/// arrangements. On the 4-vCPU development VM: ~6 µs a record when the partition runs alone or beside one other stage,
/// ~19 µs when ingest, the partition and analysis all run at once. Not GC (no collections), not shared counters (batched,
/// no change), not CPU time (ALU burners do nothing): the normalizer slows 2.7x beside two memory-streaming threads on
/// this machine, and the stages move enough memory to do that to each other. Measure on dedicated cores.
/// </summary>
[Collection(Isolated.Name)]
public sealed class PartitionCostTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false, true)]
    public async Task Stage_one_per_record(bool ingestFirst, bool alternate, bool parkAnalysis = false)
    {
#if DEBUG
        Assert.Skip("measurement needs a Release build");
#endif
        var store = new Storage.MemoryStore(maxOps: 1000);
        await using var c = new Collector(new CollectorOptions { IngestInsecure = true, Auth = AuthMode.None, Data = null, Workers = 1, QueueBytes = 1L << 34 }, store, store, store);
        if (!ingestFirst) c.Start();
        var rnd = new Random(7);
        var batches = new List<byte[]>();
        var rid = 0;
        for (var b = 0; b < 40; b++)
            batches.Add(Records.Batch([.. Enumerable.Range(0, 2000).Select(_ =>
            {
                var cmd = new BsonDocument { ["find"] = "orders", ["filter"] = new BsonDocument { ["f" + rnd.Next(50)] = rnd.Next(1000), ["status"] = "A" }, ["limit"] = 10, ["lsid"] = new BsonDocument("id", "5b8516c3a99c21e7") };
                return Records.Op(++rid, "find", cmd, ts: Records.T0 + rid * 20L, sid: 1 + rid % 64, connSeq: 1 + rid % 64, latencyUs: 200 + rnd.Next(5000), nReturned: rnd.Next(10));
            })]));
        if (ingestFirst) c.Start();
        foreach (var b in batches.Take(10)) c.Ingest(b, null, "proxy"); // warm-up
        await c.DrainAsync(TimeSpan.FromSeconds(60));
        var gate = new TaskCompletionSource();
        if (ingestFirst || parkAnalysis) _ = c.Analysis.Invoke(() => { gate.Task.Wait(); return 0; }); // hold analysis
        var p = c.Partitions[0];
        double PartitionCpu()
        {
            foreach (var dir in Directory.GetDirectories("/proc/self/task"))
                if (File.ReadAllText(Path.Combine(dir, "comm")).Trim().StartsWith("collector-parti"))
                {
                    var stat = File.ReadAllText(Path.Combine(dir, "stat"));
                    var f = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
                    return (long.Parse(f[11]) + long.Parse(f[12])) / 100.0;
                }
            return 0;
        }
        var cpu0 = PartitionCpu();
        var (g0, g1, g2, pause0) = (GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2), GC.GetTotalPauseDuration());
        var (busy0, n0) = (p.BusyTicks, p.Processed);
        var ingestTicks = 0L;
        if (ingestFirst)
        {
            // parse everything while analysis is parked (the partition drains into its queue), then let all run
            foreach (var b in batches.Skip(10))
            {
                var t = Stopwatch.GetTimestamp();
                c.Ingest(b, null, "proxy");
                ingestTicks += Stopwatch.GetTimestamp() - t;
            }
            gate.SetResult();
        }
        else
            foreach (var b in batches.Skip(10))
            {
                var t = Stopwatch.GetTimestamp();
                c.Ingest(b, null, "proxy");
                ingestTicks += Stopwatch.GetTimestamp() - t;
                if (alternate) await c.DrainAsync(TimeSpan.FromSeconds(60)); // the partition reads fresh records, ingest idle
            }
        if (parkAnalysis)
        {
            while (p.Processed - n0 < 60_000) await Task.Delay(5); // ingest + partition only; analysis parked
            gate.SetResult();
        }
        await c.DrainAsync(TimeSpan.FromSeconds(60));
        var n = p.Processed - n0;
        Console.WriteLine($"[stage1] ingest first: {ingestFirst}, alternate: {alternate}, analysis parked: {parkAnalysis}; GCs {GC.CollectionCount(0) - g0}/{GC.CollectionCount(1) - g1}/{GC.CollectionCount(2) - g2}, pause {(GC.GetTotalPauseDuration() - pause0).TotalMilliseconds:F0} ms, server GC {System.Runtime.GCSettings.IsServerGC}");
        Console.WriteLine($"[stage1] partition thread CPU {(PartitionCpu() - cpu0) * 1e6 / n:F2} µs per record (the busy figure below is wall clock)");
        Console.WriteLine($"[stage1] {n} records: ingest (parse + route) {ingestTicks * 1e6 / Stopwatch.Frequency / n:F2} µs, partition busy {(p.BusyTicks - busy0) * 1e6 / Stopwatch.Frequency / n:F2} µs per record");
    }
}
