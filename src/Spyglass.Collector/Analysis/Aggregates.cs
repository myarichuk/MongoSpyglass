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
    /// <summary>Collector clock of the last time stale examples were dropped (in memory only).</summary>
    [System.Text.Json.Serialization.JsonIgnore] public long ExamplesPrunedUs;

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
public sealed class AggregateBook(TimeSpan? exampleRetention = null)
{
    public const int Examples = 10;
    /// <summary>The catalog's cap (03 §5: analyzers bound their state in total): past it the least recently seen shapes go.</summary>
    public const int MaxShapes = 100_000;
    /// <summary>A bucket that took nothing for this long (collector clock) is closed: the producer's clock decides which minute an op is in, not when it arrives.</summary>
    private const long BucketIdleUs = 120_000_000;

    /// <summary>Examples older than this are not kept (03 §7: ops are deleted by then, so such an example resolves to nothing).</summary>
    private readonly long _exampleRetentionUs = (long)(exampleRetention ?? TimeSpan.FromHours(24)).TotalMicroseconds;
    private readonly Dictionary<(string, long), (MinuteBucket Bucket, long UpdatedUs)> _open = [];
    private readonly Dictionary<string, ShapeInfo> _shapes = [];
    private readonly Dictionary<string, (long Hour, Sketch Current, Sketch? Previous)> _baseline = [];
    /// <summary>Shapes dropped from the catalog (retention or the cap) since the start.</summary>
    public long Evicted;
    private readonly HashSet<string> _dirtyShapes = [];
    /// <summary>Changes since the last <see cref="TakeDirty"/>: the store adds them, so a late op for a closed minute adds, not overwrites.</summary>
    private readonly Dictionary<(string, long), MinuteBucket> _delta = [];

    public IReadOnlyDictionary<string, ShapeInfo> Shapes => _shapes;

    /// <param name="nowUs">the collector's clock</param>
    public void Add(Operation op, long nowUs)
    {
        var r = op.Op;
        var minute = r.Ts / 60_000_000;
        ref var open = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(_open, (op.ShapeHash, minute), out var exists);
        if (!exists) open.Bucket = new MinuteBucket { ShapeHash = op.ShapeHash, Minute = minute };
        open.UpdatedUs = nowUs;
        var b = open.Bucket;
        if (!_delta.TryGetValue((op.ShapeHash, minute), out var d))
            _delta[(op.ShapeHash, minute)] = d = new MinuteBucket { ShapeHash = op.ShapeHash, Minute = minute };
        Count(b, op);
        Count(d, op);

        if (!_shapes.TryGetValue(op.ShapeHash, out var s))
        {
            if (_shapes.Count >= 2 * MaxShapes) EvictShapes(long.MinValue, MaxShapes); // a burst of new shapes between sweeps
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
        if (nowUs - s.ExamplesPrunedUs > 60_000_000) // slowest-10 is per retention window: an old incident must not shadow today (CA-7)
        {
            s.ExamplesPrunedUs = nowUs;
            for (var i = s.Slowest.Count - 1; i >= 0; i--)
                if (s.Slowest[i].Ts < nowUs - _exampleRetentionUs) s.Slowest.RemoveAt(i);
        }
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
        if (op.Failed) b.Errors++;
        if (r.ServerLatencyUs is { } lat && !op.AwaitsByDesign) b.Latency.Add(lat); // waiting by design is not latency
        b.SumRespBytes += r.RespBytes;
        b.SumReqBytes += r.ReqBytes;
        if (r.Reply?.NReturned is { } nr) { b.SumReturned += nr; b.MaxReturned = Math.Max(b.MaxReturned, nr); }
    }

    /// <summary>
    /// Closes buckets that took nothing for two minutes of the collector's clock: they leave memory (the store has them)
    /// and feed the baseline. Returns the shapes whose closed minute's p95 is ≥ 3× the previous hour's (03 §5.2).
    /// </summary>
    public List<(string Shape, double P95, double Baseline)> CloseIdle(long nowUs)
    {
        var regressions = new List<(string, double, double)>();
        foreach (var key in _open.Where(kv => nowUs - kv.Value.UpdatedUs > BucketIdleUs).Select(kv => kv.Key).ToList())
        {
            var b = _open[key].Bucket;
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

    /// <summary>
    /// Housekeeping, once a minute: drops shapes unused for <paramref name="retention"/> and, past the cap, the least recently seen;
    /// stale baselines go too. Returns the dropped hashes (their cursor stats go with them).
    /// </summary>
    public List<string> Expire(long nowUs, TimeSpan retention)
    {
        var dropped = EvictShapes(nowUs - (long)retention.TotalMicroseconds, MaxShapes);
        var hour = nowUs / 3_600_000_000;
        List<string>? stale = null;
        foreach (var (k, v) in _baseline)
            if (v.Hour < hour - 1) (stale ??= []).Add(k); // the baseline is the previous whole hour: older is of no use
        if (stale != null)
            foreach (var k in stale) _baseline.Remove(k);
        return dropped;
    }

    /// <summary>Removes shapes last seen before <paramref name="seenBeforeUs"/>, then the least recently seen until at most <paramref name="keep"/> remain.</summary>
    private List<string> EvictShapes(long seenBeforeUs, int keep)
    {
        var dropped = new List<string>();
        foreach (var (h, s) in _shapes)
            if (s.LastSeen < seenBeforeUs) dropped.Add(h);
        var over = _shapes.Count - dropped.Count - keep;
        if (over > 0)
        {
            var drop = new HashSet<string>(dropped);
            dropped.AddRange(_shapes.Values.Where(s => !drop.Contains(s.ShapeHash)).OrderBy(s => s.LastSeen).Take(over).Select(s => s.ShapeHash));
        }
        foreach (var h in dropped)
        {
            _shapes.Remove(h);
            _baseline.Remove(h);
            _dirtyShapes.Remove(h);
        }
        Evicted += dropped.Count;
        return dropped;
    }

    /// <summary>Puts back what a failed store write took (the next flush retries it).</summary>
    public void Requeue(List<MinuteBucket>? deltas, List<ShapeInfo>? shapes)
    {
        if (deltas != null)
            foreach (var d in deltas)
            {
                if (_delta.TryGetValue((d.ShapeHash, d.Minute), out var cur)) cur.Merge(d);
                else _delta[(d.ShapeHash, d.Minute)] = d;
            }
        if (shapes != null)
            foreach (var s in shapes) _dirtyShapes.Add(s.ShapeHash);
    }

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
