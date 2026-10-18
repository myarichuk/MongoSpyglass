using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace Spyglass.Proxy.Metrics;

/// <summary>
/// In-tree Prometheus text-format metrics (01 §1: no dependencies, §12).
/// Hot-path code holds direct references to <see cref="Counter"/>/<see cref="Histogram"/> children
/// so a sample is one interlocked operation; label lookup happens once, at connection or listener setup.
/// </summary>
public sealed class MetricsRegistry
{
    private readonly List<IMetricFamily> _families = [];

    public CounterFamily Counter(string name, string help, params string[] labels) => Add(new CounterFamily(name, help, labels));
    public GaugeFamily Gauge(string name, string help, params string[] labels) => Add(new GaugeFamily(name, help, labels));
    public HistogramFamily Histogram(string name, string help, double[] buckets, params string[] labels) => Add(new HistogramFamily(name, help, buckets, labels));

    private T Add<T>(T f) where T : IMetricFamily
    {
        lock (_families) _families.Add(f);
        return f;
    }

    public string Render()
    {
        var sb = new StringBuilder(16 * 1024);
        IMetricFamily[] families;
        lock (_families) families = _families.ToArray();
        foreach (var f in families)
            f.Render(sb);
        return sb.ToString();
    }
}

internal interface IMetricFamily
{
    void Render(StringBuilder sb);
}

public abstract class MetricFamily<T> : IMetricFamily where T : class
{
    private readonly ConcurrentDictionary<string, (string[] Values, T Child)> _children = new();
    public string Name { get; }
    public string Help { get; }
    public string[] LabelNames { get; }
    protected abstract string Type { get; }

    protected MetricFamily(string name, string help, string[] labels)
    {
        Name = name;
        Help = help;
        LabelNames = labels;
    }

    public int SeriesCount => _children.Count;

    public T WithLabels(params string[] values)
    {
        if (values.Length != LabelNames.Length)
            throw new ArgumentException($"{Name} expects {LabelNames.Length} labels");
        var key = values.Length == 0 ? "" : string.Join('\u0001', values);
        if (_children.TryGetValue(key, out var existing))
            return existing.Child;
        return _children.GetOrAdd(key, _ => ((string[])values.Clone(), Create())).Child;
    }

    public bool Contains(params string[] values) => _children.ContainsKey(string.Join('\u0001', values));

    protected abstract T Create();
    protected abstract void RenderChild(StringBuilder sb, string[] values, T child);

    public void Render(StringBuilder sb)
    {
        sb.Append("# HELP ").Append(Name).Append(' ').Append(Help).Append('\n');
        sb.Append("# TYPE ").Append(Name).Append(' ').Append(Type).Append('\n');
        foreach (var (_, (values, child)) in _children.OrderBy(k => k.Key, StringComparer.Ordinal))
            RenderChild(sb, values, child);
    }

    protected string FormatLabels(string[] values, string? extraName = null, string? extraValue = null)
    {
        if (values.Length == 0 && extraName == null) return "";
        var sb = new StringBuilder("{");
        for (var i = 0; i < values.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(LabelNames[i]).Append("=\"");
            Escape(sb, values[i]);
            sb.Append('"');
        }
        if (extraName != null)
        {
            if (values.Length > 0) sb.Append(',');
            sb.Append(extraName).Append("=\"").Append(extraValue).Append('"');
        }
        return sb.Append('}').ToString();
    }

    private static void Escape(StringBuilder sb, string v)
    {
        foreach (var c in v)
        {
            switch (c)
            {
                case '\\': sb.Append(@"\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append(@"\n"); break;
                default: sb.Append(c); break;
            }
        }
    }

    protected static string Num(double d) =>
        double.IsPositiveInfinity(d) ? "+Inf" : d.ToString("G17", CultureInfo.InvariantCulture);
}

public sealed class Counter
{
    private long _value;
    public void Inc() => Interlocked.Increment(ref _value);
    public void Add(long n) => Interlocked.Add(ref _value, n);
    public long Value => Volatile.Read(ref _value);
}

public sealed class Gauge
{
    private long _value;
    public void Inc() => Interlocked.Increment(ref _value);
    public void Dec() => Interlocked.Decrement(ref _value);
    public void Add(long n) => Interlocked.Add(ref _value, n);
    public void Set(long v) => Volatile.Write(ref _value, v);
    public long Value => Volatile.Read(ref _value);
}

public sealed class Histogram
{
    private readonly double[] _bounds;
    private readonly long[] _counts; // per bucket (not cumulative); last = +Inf
    private long _sumBits;           // double, updated by CAS

    public Histogram(double[] bounds)
    {
        _bounds = bounds;
        _counts = new long[bounds.Length + 1];
    }

    public void Observe(double v)
    {
        var i = Array.BinarySearch(_bounds, v);
        if (i < 0) i = ~i; // first bound >= v (le semantics)
        Interlocked.Increment(ref _counts[i]);
        long seen, next;
        do
        {
            seen = Volatile.Read(ref _sumBits);
            next = BitConverter.DoubleToInt64Bits(BitConverter.Int64BitsToDouble(seen) + v);
        } while (Interlocked.CompareExchange(ref _sumBits, next, seen) != seen);
    }

    internal (double[] Bounds, long[] Cumulative, long Count, double Sum) Snapshot()
    {
        var cum = new long[_counts.Length];
        long running = 0;
        for (var i = 0; i < _counts.Length; i++)
        {
            running += Volatile.Read(ref _counts[i]);
            cum[i] = running;
        }
        return (_bounds, cum, running, BitConverter.Int64BitsToDouble(Volatile.Read(ref _sumBits)));
    }
}

public sealed class CounterFamily(string name, string help, string[] labels) : MetricFamily<Counter>(name, help, labels)
{
    protected override string Type => "counter";
    protected override Counter Create() => new();
    protected override void RenderChild(StringBuilder sb, string[] v, Counter c) =>
        sb.Append(Name).Append(FormatLabels(v)).Append(' ').Append(c.Value.ToString(CultureInfo.InvariantCulture)).Append('\n');
}

public sealed class GaugeFamily(string name, string help, string[] labels) : MetricFamily<Gauge>(name, help, labels)
{
    protected override string Type => "gauge";
    protected override Gauge Create() => new();
    protected override void RenderChild(StringBuilder sb, string[] v, Gauge g) =>
        sb.Append(Name).Append(FormatLabels(v)).Append(' ').Append(g.Value.ToString(CultureInfo.InvariantCulture)).Append('\n');
}

public sealed class HistogramFamily(string name, string help, double[] buckets, string[] labels) : MetricFamily<Histogram>(name, help, labels)
{
    protected override string Type => "histogram";
    protected override Histogram Create() => new(buckets);

    protected override void RenderChild(StringBuilder sb, string[] values, Histogram h)
    {
        var (bounds, cum, count, sum) = h.Snapshot();
        var l = FormatLabels(values);
        for (var i = 0; i < bounds.Length; i++)
            sb.Append(Name).Append("_bucket").Append(FormatLabels(values, "le", Num(bounds[i]))).Append(' ').Append(cum[i]).Append('\n');
        sb.Append(Name).Append("_bucket").Append(FormatLabels(values, "le", "+Inf")).Append(' ').Append(count).Append('\n');
        sb.Append(Name).Append("_sum").Append(l).Append(' ').Append(Num(sum)).Append('\n');
        sb.Append(Name).Append("_count").Append(l).Append(' ').Append(count).Append('\n');
    }
}
