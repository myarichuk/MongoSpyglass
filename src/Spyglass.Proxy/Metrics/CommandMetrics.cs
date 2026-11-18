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

    /// <summary>
    /// The series for <paramref name="labels"/> (in the family's label order), bounded by <c>metrics.maxSeries</c>
    /// (01 §12): past it a new label set folds its collection to <see cref="Other"/>; if even that series would be new and the
    /// family is at twice the bound, every label but <c>status</c> (a closed set) folds, into one overflow series.
    /// <c>command</c>, <c>database</c>, <c>app</c> and <c>code_name</c> come from clients and servers, so they can't be left open.
    /// </summary>
    public T Series<T>(MetricFamily<T> family, params string[] labels) where T : class
    {
        if (family.SeriesCount < _maxSeries || family.Contains(labels))
            return family.WithLabels(labels);
        var ci = Array.IndexOf(family.LabelNames, "collection");
        if (ci >= 0 && labels[ci] != Other)
        {
            var folded = (string[])labels.Clone();
            folded[ci] = Other;
            if (family.SeriesCount < 2L * _maxSeries || family.Contains(folded))
                return family.WithLabels(folded);
        }
        var overflow = new string[labels.Length];
        for (var i = 0; i < overflow.Length; i++)
            overflow[i] = family.LabelNames[i] == "status" ? labels[i] : Other;
        return family.WithLabels(overflow);
    }
}
