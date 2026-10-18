using System.Diagnostics;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using Spyglass.Collector.Shapes;

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
        var both = Time(() => ShapeNormalizer.Normalize("find", "shop", "orders", doc, false, true));
        var shapeOnly = Time(() => ShapeNormalizer.Normalize("find", "shop", "orders", doc, false, false));
        var canonical = Time(() => ShapeNormalizer.Canonical(doc));
        var ulid = Time(() => Model.Ulid.New(1_700_000_000_000));
        Console.WriteLine("[stage1] bytes allocated per call: " + string.Join(", ", allocs.Select(kv => $"{kv.Key[..Math.Min(40, kv.Key.Length)]} {kv.Value:F0}")));
        Console.WriteLine($"[stage1] decode {decode:F2} µs (driver's {driverDecode:F2}), normalize shape+value {both:F2} µs, shape only {shapeOnly:F2} µs, canonical EJSON of the command {canonical:F2} µs, ulid {ulid:F2} µs");
    }
}
