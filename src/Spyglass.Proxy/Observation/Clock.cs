using System.Diagnostics;

namespace Spyglass.Proxy.Observation;

/// <summary>Converts monotonic <see cref="Stopwatch"/> ticks to wall-clock µs since the Unix epoch (02 §4).</summary>
internal static class Clock
{
    private static readonly long Origin = Stopwatch.GetTimestamp();
    private static readonly long OriginUnixMicros = (DateTime.UtcNow - DateTime.UnixEpoch).Ticks / 10;

    public static long ToUnixMicros(long timestamp) =>
        OriginUnixMicros + (long)((timestamp - Origin) * (1_000_000.0 / Stopwatch.Frequency));

    public static long Micros(long fromTimestamp, long toTimestamp) =>
        (long)((toTimestamp - fromTimestamp) * (1_000_000.0 / Stopwatch.Frequency));

    public static long NowUnixMicros() => ToUnixMicros(Stopwatch.GetTimestamp());
}
