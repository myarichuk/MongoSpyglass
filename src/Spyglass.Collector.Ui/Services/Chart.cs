namespace Spyglass.Collector.Ui.Services;

/// <summary>One drawn column: <paramref name="Step"/> minutes starting at <paramref name="Minute"/>.</summary>
public readonly record struct Column(long Minute, long Count, long Errors, double P95Us);

/// <summary>The per-minute series laid out on a real time axis (UD-10): idle minutes stay empty, long ranges are merged to at most <c>maxColumns</c>.</summary>
public sealed record SeriesLayout(long FirstMinute, int Step, long TotalMinutes, Column[] Columns)
{
    public static readonly SeriesLayout Empty = new(0, 1, 1, []);

    public static SeriesLayout Of(SeriesPoint[] series, int maxColumns = 300)
    {
        if (series.Length == 0) return Empty;
        var first = series.Min(p => p.Minute);
        var last = series.Max(p => p.Minute);
        var span = last - first + 1;
        var step = (int)Math.Max(1, (span + maxColumns - 1) / maxColumns);
        var total = (span + step - 1) / step * step;
        var cols = series.GroupBy(p => (p.Minute - first) / step).OrderBy(g => g.Key)
            // merged minutes: counts add up, p95 is the worst of them (the true merged p95 is not recoverable from the points)
            .Select(g => new Column(first + g.Key * step, g.Sum(p => p.Count), g.Sum(p => p.Errors), g.Max(p => p.P95Us)))
            .ToArray();
        return new SeriesLayout(first, step, total, cols);
    }

    public double X(long minute) => 1000.0 * (minute - FirstMinute) / TotalMinutes;
    public double Width => 1000.0 * Step / TotalMinutes;

    /// <summary>Runs of adjacent columns: a gap in the data breaks the line instead of being bridged.</summary>
    public IEnumerable<Column[]> Runs()
    {
        var run = new List<Column>();
        foreach (var c in Columns)
        {
            if (run.Count > 0 && c.Minute - run[^1].Minute != Step) { yield return run.ToArray(); run.Clear(); }
            run.Add(c);
        }
        if (run.Count > 0) yield return run.ToArray();
    }
}
