namespace Spyglass.Proxy.Metrics;

/// <summary>Per-command families of 01 §12, updated by the observer (never the forwarding path).</summary>
public sealed class CommandMetrics
{
    public static readonly double[] DurationBuckets =
        [0.0001, 0.00025, 0.0005, 0.001, 0.0025, 0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10];

    public static readonly double[] ReplyBytesBuckets = Enumerable.Range(0, 9).Select(i => 256.0 * Math.Pow(4, i)).ToArray(); // 256 B … 16 MiB
    public static readonly double[] DocumentBuckets = [1, 10, 100, 1000, 10000, 100000];

    public const string Other = "__other__";

    private readonly int _maxSeries;
    public readonly HistogramFamily Duration;
    public readonly HistogramFamily ReplyBytes;
    public readonly HistogramFamily ReturnedDocuments;
    public readonly CounterFamily Errors;
    public readonly GaugeFamily CursorsOpen;

    public CommandMetrics(MetricsRegistry r, int maxSeries)
    {
        _maxSeries = maxSeries;
        Duration = r.Histogram("spyglass_command_duration_seconds",
            "Server latency per command: first reply byte minus last request byte, as seen by the proxy.",
            DurationBuckets, "command", "database", "collection", "app", "status");
        ReplyBytes = r.Histogram("spyglass_command_reply_bytes", "Reply size per command, all reply frames.",
            ReplyBytesBuckets, "command", "database", "collection", "app");
        ReturnedDocuments = r.Histogram("spyglass_command_returned_documents", "Documents returned per cursor batch.",
            DocumentBuckets, "command", "database", "collection", "app");
        Errors = r.Counter("spyglass_command_errors_total", "Commands that replied ok:0.",
            "command", "database", "collection", "app", "code_name");
        CursorsOpen = r.Gauge("spyglass_cursors_open", "Server cursors opened through the proxy and not yet exhausted, killed or timed out.",
            "database", "collection");
    }

    /// <summary>Folds the collection label to <see cref="Other"/> once a family exceeds <c>metrics.maxSeries</c>.</summary>
    public string Collection<T>(MetricFamily<T> family, string command, string db, string? coll, string app, string? extra = null) where T : class
    {
        coll ??= "";
        if (family.SeriesCount < _maxSeries)
            return coll;
        var key = extra == null ? new[] { command, db, coll, app } : [command, db, coll, app, extra];
        return family.Contains(key) ? coll : Other;
    }
}
