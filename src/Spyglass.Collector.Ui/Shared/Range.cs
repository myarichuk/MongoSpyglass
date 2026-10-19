namespace Spyglass.Collector.Ui.Shared;

/// <summary>Time ranges offered by the aggregate pages, as from/to query strings for /api/v1.</summary>
public static class TimeRange
{
    public static readonly (string Key, string Label, TimeSpan Span)[] Options =
    [
        ("15m", "15 min", TimeSpan.FromMinutes(15)), ("1h", "1 hour", TimeSpan.FromHours(1)),
        ("6h", "6 hours", TimeSpan.FromHours(6)), ("24h", "24 hours", TimeSpan.FromHours(24)), ("7d", "7 days", TimeSpan.FromDays(7)),
    ];

    public static string Query(string? key)
    {
        var span = Options.FirstOrDefault(o => o.Key == key).Span;
        if (span == default) span = TimeSpan.FromHours(1);
        var to = Services.Fmt.NowUs();
        return $"from={to - (long)span.TotalMicroseconds}&to={to}";
    }
}
