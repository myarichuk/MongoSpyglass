using System.Globalization;
using System.Net;

namespace Spyglass.Collector;

public enum AuthMode { Password, Header, None }

/// <summary>
/// Collector settings: <c>SPYGLASS_*</c> environment variables (03 §9), overridden by <c>--name value</c>
/// arguments (same names, lower-case, without the prefix: <c>--bind 0.0.0.0:8080</c>).
/// </summary>
public sealed class CollectorOptions
{
    public IPEndPoint Bind { get; set; } = new(IPAddress.Loopback, 8080);
    /// <summary>Ingest bearer token (03 §9). Required unless <see cref="IngestInsecure"/>.</summary>
    public string? Token { get; set; }
    public bool IngestInsecure { get; set; }
    public AuthMode Auth { get; set; } = AuthMode.Password;
    public string? AdminPassword { get; set; }
    public string AuthHeader { get; set; } = "X-Forwarded-User";
    /// <summary>SQLite file directory; null keeps everything in memory (tests).</summary>
    public string? Data { get; set; } = "/data";

    // 03 §1, §3, §5, §7
    public long QueueBytes { get; set; } = 256L * 1024 * 1024;
    public int MaxBodyBytes { get; set; } = 16 * 1024 * 1024;
    public int Workers { get; set; } = Math.Max(1, Environment.ProcessorCount / 2);
    public TimeSpan OpRetention { get; set; } = TimeSpan.FromHours(24);
    public long OpStoreBytes { get; set; } = 5L * 1024 * 1024 * 1024;
    public TimeSpan CursorTimeout { get; set; } = TimeSpan.FromMinutes(10);
    public TimeSpan MaxFutureSkew { get; set; } = TimeSpan.FromMinutes(5);
    public int LiveBuffer { get; set; } = 1000;

    // 03 §5.2 thresholds
    public long SlowShapeUs { get; set; } = 100_000;
    public int NPlusOneCount { get; set; } = 10;
    public long NPlusOneGapUs { get; set; } = 5_000;
    public int RepeatedQueryCount { get; set; } = 3;

    public static CollectorOptions Load(string[] args, IDictionary<string, string?>? env = null)
    {
        env ??= Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(e => (string)e.Key, e => (string?)e.Value);
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in env)
            if (k.StartsWith("SPYGLASS_", StringComparison.Ordinal) && v != null)
                values[k["SPYGLASS_".Length..].Replace("_", "")] = v;
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length)
                throw new ArgumentException($"expected --name value, got '{args[i]}'");
            values[args[i][2..].Replace("-", "").Replace(".", "")] = args[++i];
        }

        var o = new CollectorOptions();
        foreach (var (k, v) in values)
        {
            switch (k.ToLowerInvariant())
            {
                case "bind": o.Bind = IPEndPoint.Parse(v); break;
                case "token": o.Token = v; break;
                case "ingestinsecure": o.IngestInsecure = v is "1" or "true"; break;
                case "auth": o.Auth = v.ToLowerInvariant() switch
                {
                    "password" => AuthMode.Password,
                    "header" => AuthMode.Header,
                    "none" => AuthMode.None,
                    _ => throw new ArgumentException($"SPYGLASS_AUTH: password | header | none, not '{v}'"),
                };
                    break;
                case "adminpassword": o.AdminPassword = v; break;
                case "authheader": o.AuthHeader = v; o.Auth = AuthMode.Header; break;
                case "data": o.Data = v is "" or ":memory:" ? null : v; break;
                case "queuebytes": o.QueueBytes = long.Parse(v, CultureInfo.InvariantCulture); break;
                case "maxbodybytes": o.MaxBodyBytes = int.Parse(v, CultureInfo.InvariantCulture); break;
                case "workers": o.Workers = int.Parse(v, CultureInfo.InvariantCulture); break;
                case "opretentionhours": o.OpRetention = TimeSpan.FromHours(double.Parse(v, CultureInfo.InvariantCulture)); break;
                case "opstorebytes": o.OpStoreBytes = long.Parse(v, CultureInfo.InvariantCulture); break;
                case "cursortimeoutseconds": o.CursorTimeout = TimeSpan.FromSeconds(double.Parse(v, CultureInfo.InvariantCulture)); break;
                case "slowshapems": o.SlowShapeUs = (long)(double.Parse(v, CultureInfo.InvariantCulture) * 1000); break;
                default: throw new ArgumentException($"unknown setting '{k}'");
            }
        }
        Validate(o);
        return o;
    }

    public static void Validate(CollectorOptions o)
    {
        if (o.Token is null && !o.IngestInsecure)
            throw new ArgumentException("SPYGLASS_TOKEN is required for ingest (or SPYGLASS_INGEST_INSECURE=1), 03 §9");
        if (o.Auth == AuthMode.Password && string.IsNullOrEmpty(o.AdminPassword))
            throw new ArgumentException("SPYGLASS_ADMIN_PASSWORD is required with SPYGLASS_AUTH=password, 03 §9");
        if (o.Auth == AuthMode.None && !IPAddress.IsLoopback(o.Bind.Address))
            throw new ArgumentException("SPYGLASS_AUTH=none is allowed only on a loopback bind, 03 §9");
        if (o.Workers < 1) throw new ArgumentException("workers must be >= 1");
    }
}
