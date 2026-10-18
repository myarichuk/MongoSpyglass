using Spyglass.Collector.Model;

namespace Spyglass.Collector.Analysis;

/// <summary>03 §6: one <c>(shapeHash, minute)</c> bucket. Minute = µs since epoch / 60e6.</summary>
public sealed class MinuteBucket
{
    public required string ShapeHash;
    public required long Minute;
    public long Count, Errors;
    public Sketch Latency = new();
    public long SumRespBytes, SumReqBytes, SumReturned, MaxReturned;

    public void Merge(MinuteBucket o)
    {
        Count += o.Count;
        Errors += o.Errors;
        Latency.Merge(o.Latency);
        SumRespBytes += o.SumRespBytes;
        SumReqBytes += o.SumReqBytes;
        SumReturned += o.SumReturned;
        MaxReturned = Math.Max(MaxReturned, o.MaxReturned);
    }
}

public readonly record struct Example(string OpId, long LatencyUs, long Ts);

/// <summary>A shape's identity and examples (03 §8 <c>/shapes/{hash}</c>: slowest 10 plus latest 10).</summary>
public sealed class ShapeInfo
{
    public required string ShapeHash;
    public required string ShapeJson;
    public required string Command;
    public string? Db, Coll;
    public long FirstSeen, LastSeen, Count;
    public List<Example> Slowest = [];
    public List<Example> Latest = [];
    public HashSet<string> Apps = [];
    public int MaxInCardinality;

    public ShapeInfo Snapshot()
    {
        var s = (ShapeInfo)MemberwiseClone();
        s.Slowest = [.. Slowest];
        s.Latest = [.. Latest];
        s.Apps = [.. Apps];
        return s;
    }
}

/// <summary>
/// Aggregation state on the analysis thread: open minute buckets, the shape catalog, and the per-shape
/// baseline for the p95-regression half of <c>slow-shape</c>. The baseline is the previous whole hour
/// (two tumbling hourly sketches per shape) rather than a sliding 60-minute window, which would cost 60
/// sketches per shape.
/// </summary>
public sealed class AggregateBook
{
    public const int Examples = 10;

    private readonly Dictionary<(string, long), MinuteBucket> _open = [];
    private readonly Dictionary<string, ShapeInfo> _shapes = [];
    private readonly Dictionary<string, (long Hour, Sketch Current, Sketch? Previous)> _baseline = [];
    private readonly HashSet<string> _dirtyShapes = [];
    /// <summary>Changes since the last <see cref="TakeDirty"/>: the store adds them, so a late op for a closed minute adds, not overwrites.</summary>
    private readonly Dictionary<(string, long), MinuteBucket> _delta = [];

    public IReadOnlyDictionary<string, ShapeInfo> Shapes => _shapes;

    public void Add(Operation op)
    {
        var r = op.Op;
        var minute = r.Ts / 60_000_000;
        if (!_open.TryGetValue((op.ShapeHash, minute), out var b))
            _open[(op.ShapeHash, minute)] = b = new MinuteBucket { ShapeHash = op.ShapeHash, Minute = minute };
        if (!_delta.TryGetValue((op.ShapeHash, minute), out var d))
            _delta[(op.ShapeHash, minute)] = d = new MinuteBucket { ShapeHash = op.ShapeHash, Minute = minute };
        Count(b, op);
        Count(d, op);

        if (!_shapes.TryGetValue(op.ShapeHash, out var s))
        {
            _shapes[op.ShapeHash] = s = new ShapeInfo
            {
                ShapeHash = op.ShapeHash, ShapeJson = op.ShapeJson ?? "{}", Command = r.Command, Db = r.Db, Coll = r.Coll, FirstSeen = r.Ts,
            };
        }
        s.LastSeen = Math.Max(s.LastSeen, r.Ts);
        s.Count++;
        s.MaxInCardinality = Math.Max(s.MaxInCardinality, op.InCardinality);
        if (op.App != null && s.Apps.Count < 64) s.Apps.Add(op.App);
        var ex = new Example(op.Id, r.ServerLatencyUs ?? 0, r.Ts);
        s.Latest.Add(ex);
        if (s.Latest.Count > Examples) s.Latest.RemoveAt(0);
        if (s.Slowest.Count < Examples || ex.LatencyUs > s.Slowest[^1].LatencyUs)
        {
            s.Slowest.Add(ex);
            s.Slowest.Sort((x, y) => y.LatencyUs.CompareTo(x.LatencyUs));
            if (s.Slowest.Count > Examples) s.Slowest.RemoveAt(Examples);
        }
        _dirtyShapes.Add(op.ShapeHash);
    }

    private static void Count(MinuteBucket b, Operation op)
    {
        var r = op.Op;
        b.Count++;
        if (op.IsError) b.Errors++;
        if (r.ServerLatencyUs is { } lat) b.Latency.Add(lat);
        b.SumRespBytes += r.RespBytes;
        b.SumReqBytes += r.ReqBytes;
        if (r.Reply?.NReturned is { } nr) { b.SumReturned += nr; b.MaxReturned = Math.Max(b.MaxReturned, nr); }
    }

    /// <summary>
    /// Closes buckets older than <paramref name="beforeMinute"/>: they leave memory (the store has them) and
    /// feed the baseline. Returns the shapes whose closed minute's p95 is ≥ 3× the previous hour's (03 §5.2).
    /// </summary>
    public List<(string Shape, double P95, double Baseline)> CloseBefore(long beforeMinute)
    {
        var regressions = new List<(string, double, double)>();
        foreach (var key in _open.Keys.Where(k => k.Item2 < beforeMinute).ToList())
        {
            var b = _open[key];
            _open.Remove(key);
            var hour = b.Minute / 60;
            var (h, cur, prev) = _baseline.TryGetValue(b.ShapeHash, out var bl) ? bl : (hour, new Sketch(), null);
            if (hour > h) { prev = hour == h + 1 ? cur : null; cur = new Sketch(); h = hour; }
            if (prev is { Count: >= 50 } && b.Latency.Count >= 10)
            {
                var p95 = b.Latency.Quantile(0.95);
                var basis = prev.Quantile(0.95);
                if (basis > 0 && p95 >= 3 * basis) regressions.Add((b.ShapeHash, p95, basis));
            }
            cur.Merge(b.Latency);
            _baseline[b.ShapeHash] = (h, cur, prev);
        }
        return regressions;
    }

    public MinuteBucket? Bucket(string shape, long minute) => _open.GetValueOrDefault((shape, minute));

    /// <summary>Bucket deltas (the store merges them in) and shape snapshots (the store replaces) since the last call.</summary>
    public (List<MinuteBucket> Deltas, List<ShapeInfo> Shapes) TakeDirty()
    {
        var deltas = _delta.Values.ToList();
        _delta.Clear();
        var shapes = _dirtyShapes.Where(_shapes.ContainsKey).Select(h => _shapes[h].Snapshot()).ToList();
        _dirtyShapes.Clear();
        return (deltas, shapes);
    }

    public void Restore(ShapeInfo s) => _shapes[s.ShapeHash] = s;
}
