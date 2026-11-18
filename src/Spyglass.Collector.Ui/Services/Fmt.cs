namespace Spyglass.Collector.Ui.Services;

public static class Fmt
{
    /// <summary>Server clock minus browser clock, in µs (UD-3): every timestamp the UI compares against is the collector's.</summary>
    public static long ServerOffsetUs { get; set; }

    public static string Ns(string? db, string? coll) => coll == null ? db ?? "" : $"{db}.{coll}";

    public static string Us(double? us) => us switch
    {
        null => "–",
        < 1000 => $"{us:F0} µs",
        < 1_000_000 => $"{us / 1000:F1} ms",
        _ => $"{us / 1_000_000:F2} s",
    };

    public static string Bytes(long b) => b switch
    {
        < 1024 => $"{b} B",
        < 1024 * 1024 => $"{b / 1024.0:F1} KiB",
        _ => $"{b / 1048576.0:F1} MiB",
    };

    // All times are shown in UTC and labelled so: the WASM runtime ships without time zone data (03 §10, no external fetches).
    public static DateTime Time(long us) => DateTime.UnixEpoch.AddTicks(us * 10);
    public static string Clock(long us) => Time(us).ToString("HH:mm:ss.fff");
    public static string Stamp(long us) => Time(us).ToString("yyyy-MM-dd HH:mm:ss") + " UTC";

    public static string Ago(long us, long nowUs)
    {
        var s = Math.Max(0, (nowUs - us) / 1_000_000);
        return s switch { < 60 => $"{s}s", < 3600 => $"{s / 60}m", < 86400 => $"{s / 3600}h", _ => $"{s / 86400}d" };
    }

    /// <summary>The collector's "now" as far as it can be told from here.</summary>
    public static long NowUs() => (DateTime.UtcNow - DateTime.UnixEpoch).Ticks / 10 + ServerOffsetUs;

    /// <summary>The offset implied by an HTTP <c>Date</c> (whole seconds): differences under 2 s are noise, not a skewed clock.</summary>
    public static long OffsetFromDate(DateTimeOffset serverDate, DateTime browserUtcNow)
    {
        var off = (serverDate.UtcTicks - browserUtcNow.Ticks) / 10;
        return Math.Abs(off) < 2_000_000 ? 0 : off;
    }
}
