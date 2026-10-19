namespace Spyglass.Collector.Analysis;

/// <summary>
/// 03 §6 latency distribution: a DDSketch with 1% relative accuracy over 1 µs – 60 s. Bucket i holds
/// values in (γ^(i-1), γ^i], γ = (1+α)/(1−α); a quantile is reported as the bucket's midpoint in
/// relative terms, so the error is ≤ α. Sparse: one shape's latencies occupy a few dozen buckets.
/// </summary>
public sealed class Sketch
{
    public const double Alpha = 0.01;
    private static readonly double Gamma = (1 + Alpha) / (1 - Alpha);
    private static readonly double LogGamma = Math.Log(Gamma);
    private const double Min = 1, Max = 60_000_000;
    private static readonly int MaxIndex = Index(Max);

    private readonly SortedDictionary<int, long> _bins = [];
    public long Count { get; private set; }
    public long ZeroCount { get; private set; }
    public double Sum { get; private set; }
    public double MaxValue { get; private set; }

    private static int Index(double v) => (int)Math.Ceiling(Math.Log(v) / LogGamma);

    public void Add(double us)
    {
        Count++;
        Sum += us;
        if (us > MaxValue) MaxValue = us;
        if (us < Min) { ZeroCount++; return; }
        var i = Math.Min(Index(us), MaxIndex);
        _bins[i] = _bins.GetValueOrDefault(i) + 1;
    }

    public void Merge(Sketch other)
    {
        Count += other.Count;
        ZeroCount += other.ZeroCount;
        Sum += other.Sum;
        MaxValue = Math.Max(MaxValue, other.MaxValue);
        foreach (var (i, n) in other._bins)
            _bins[i] = _bins.GetValueOrDefault(i) + n;
    }

    /// <summary>The value at quantile q in [0,1], within 1% relative error; 0 when empty.</summary>
    public double Quantile(double q)
    {
        if (Count == 0) return 0;
        var rank = (long)Math.Ceiling(q * Count);
        if (rank <= ZeroCount) return 0;
        var seen = ZeroCount;
        foreach (var (i, n) in _bins)
        {
            seen += n;
            if (seen >= rank)
                return 2 * Math.Pow(Gamma, i) / (Gamma + 1);
        }
        return MaxValue;
    }

    /// <summary>Compact form for storage: count of zeros, then (index, count) pairs.</summary>
    public byte[] Serialize()
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(Count); w.Write(ZeroCount); w.Write(Sum); w.Write(MaxValue); w.Write(_bins.Count);
        foreach (var (i, n) in _bins) { w.Write(i); w.Write(n); }
        return ms.ToArray();
    }

    public static Sketch Deserialize(byte[] data)
    {
        var s = new Sketch();
        using var r = new BinaryReader(new MemoryStream(data));
        s.Count = r.ReadInt64(); s.ZeroCount = r.ReadInt64(); s.Sum = r.ReadDouble(); s.MaxValue = r.ReadDouble();
        var n = r.ReadInt32();
        for (var k = 0; k < n; k++) s._bins[r.ReadInt32()] = r.ReadInt64();
        return s;
    }
}
