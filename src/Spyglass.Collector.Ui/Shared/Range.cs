namespace Spyglass.Collector.Ui.Shared;

/// <summary>Time ranges offered by the aggregate pages, as from/to query strings for /api/v1.</summary>
public static class TimeRange
{
    public const string Default = "1h";

    public static readonly (string Key, string Label, TimeSpan Span)[] Options =
    [
        ("15m", "15 min", TimeSpan.FromMinutes(15)), ("1h", "1 hour", TimeSpan.FromHours(1)),
        ("6h", "6 hours", TimeSpan.FromHours(6)), ("24h", "24 hours", TimeSpan.FromHours(24)), ("7d", "7 days", TimeSpan.FromDays(7)),
    ];

    /// <summary>The key of a known range, else <see cref="Default"/>.</summary>
    public static string Normalize(string? key) => Options.Any(o => o.Key == key) ? key! : Default;

    /// <summary>Relative to the collector's clock (<see cref="Services.Fmt.NowUs"/>), not the browser's.</summary>
    public static string Query(string? key, long? nowUs = null)
    {
        var span = Options.First(o => o.Key == Normalize(key)).Span;
        var to = nowUs ?? Services.Fmt.NowUs();
        return $"from={to - (long)span.TotalMicroseconds}&to={to}";
    }
}
