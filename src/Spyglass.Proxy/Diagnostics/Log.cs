namespace Spyglass.Proxy.Diagnostics;

public enum LogLevel { Trace, Debug, Info, Warn, Error }

/// <summary>
/// Line-oriented logger to stderr. Never called per frame at Info or above.
/// Messages must not contain captured payloads (I7); redaction happens before anything is logged.
/// </summary>
public static class Log
{
    public static LogLevel Level { get; set; } = LogLevel.Info;
    private static readonly object Gate = new();

    public static bool IsEnabled(LogLevel level) => level >= Level;

    public static void Write(LogLevel level, string message)
    {
        if (level < Level) return;
        var line = $"{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ} {Tag(level)} {message}";
        lock (Gate)
            Console.Error.WriteLine(line);
    }

    public static void Trace(string m) => Write(LogLevel.Trace, m);
    public static void Debug(string m) => Write(LogLevel.Debug, m);
    public static void Info(string m) => Write(LogLevel.Info, m);
    public static void Warn(string m) => Write(LogLevel.Warn, m);
    public static void Error(string m) => Write(LogLevel.Error, m);

    public static LogLevel Parse(string s) => s switch
    {
        "trace" => LogLevel.Trace,
        "debug" => LogLevel.Debug,
        "warn" => LogLevel.Warn,
        "error" => LogLevel.Error,
        _ => LogLevel.Info
    };

    private static string Tag(LogLevel l) => l switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Info => "INF",
        LogLevel.Warn => "WRN",
        _ => "ERR"
    };
}
