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
    /// <summary>
    /// Peers (CIDRs or single addresses) whose <c>X-Forwarded-User</c> (header auth) and <c>X-Forwarded-Proto</c> (session
    /// cookie <c>Secure</c>) are believed (03 §9). Loopback by default: the reverse proxy on the same host.
    /// </summary>
    public IReadOnlyList<IPNetwork> TrustedProxies { get; set; } = [new(IPAddress.Loopback, 8), new(IPAddress.IPv6Loopback, 128)];
    /// <summary>Non-fatal findings from <see cref="Load"/> (unknown settings), for the caller to print.</summary>
    public List<string> Warnings { get; } = [];
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

    // 02 §6.2 join windows and the bounds on join state (03 §11)
    public long JoinWaitUs { get; set; } = 2_000_000;
    public long JoinWaitExporterUs { get; set; } = 10_000_000;
    /// <summary>Ops waiting for context, over all partitions; past it the oldest is finalized early (counted).</summary>
    public int JoinMaxWaitingOps { get; set; } = 200_000;
    public int JoinMaxWaitingCmds { get; set; } = 200_000;
    /// <summary>Finalized ops a late cmd can still merge into, per partition (02 §6.2, bounded like dedup).</summary>
    public int JoinFinalizedCapacity { get; set; } = 1 << 17;

    // /healthz: a stage loop turns at least every 250 ms, so one that has not turned for this long is stuck
    public TimeSpan HealthStageStall { get; set; } = TimeSpan.FromSeconds(30);
    /// <summary>The writer's longest legitimate step is one retention chunk.</summary>
    public TimeSpan HealthWriterStall { get; set; } = TimeSpan.FromSeconds(120);
    /// <summary>Storage writes failing with no success for this long (a full disk, a broken file).</summary>
    public TimeSpan HealthStorageFailing { get; set; } = TimeSpan.FromSeconds(60);

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
        AuthMode? auth = null;
        string? authHeader = null;
        foreach (var (k, v) in values)
        {
            switch (k.ToLowerInvariant())
            {
                case "bind": o.Bind = IPEndPoint.Parse(v); break;
                case "token": o.Token = v; break;
                case "ingestinsecure": o.IngestInsecure = v is "1" or "true"; break;
                case "auth": auth = v.ToLowerInvariant() switch
                {
                    "password" => AuthMode.Password,
                    "header" => AuthMode.Header,
                    "none" => AuthMode.None,
                    _ => throw new ArgumentException($"SPYGLASS_AUTH: password | header | none, not '{v}'"),
                };
                    break;
                case "adminpassword": o.AdminPassword = v; break;
                case "authheader": authHeader = v; break;
                case "trustedproxies" or "authheadertrustedproxies": o.TrustedProxies = ParseNetworks(v); break;
                case "data": o.Data = v is "" or ":memory:" ? null : v; break;
                case "queuebytes": o.QueueBytes = long.Parse(v, CultureInfo.InvariantCulture); break;
                case "maxbodybytes": o.MaxBodyBytes = int.Parse(v, CultureInfo.InvariantCulture); break;
                case "workers": o.Workers = int.Parse(v, CultureInfo.InvariantCulture); break;
                case "opretentionhours": o.OpRetention = TimeSpan.FromHours(double.Parse(v, CultureInfo.InvariantCulture)); break;
                case "opstorebytes": o.OpStoreBytes = long.Parse(v, CultureInfo.InvariantCulture); break;
                case "cursortimeoutseconds": o.CursorTimeout = TimeSpan.FromSeconds(double.Parse(v, CultureInfo.InvariantCulture)); break;
                case "slowshapems": o.SlowShapeUs = (long)(double.Parse(v, CultureInfo.InvariantCulture) * 1000); break;
                // the proxy and the exporters read SPYGLASS_* from the same env file (SPYGLASS_UPSTREAM__URI, SPYGLASS_INGEST…)
                default: o.Warnings.Add($"ignoring unknown setting '{k}'"); break;
            }
        }
        // 03 §9: SPYGLASS_AUTH_HEADER alone selects header auth; with SPYGLASS_AUTH it must agree (no order-dependent result)
        if (authHeader != null)
        {
            if (auth is { } a && a != AuthMode.Header)
                throw new ArgumentException($"SPYGLASS_AUTH_HEADER is set, which means SPYGLASS_AUTH=header, but SPYGLASS_AUTH={a.ToString().ToLowerInvariant()}");
            o.AuthHeader = authHeader;
            auth = AuthMode.Header;
        }
        if (auth is { } mode) o.Auth = mode;
        Validate(o);
        return o;
    }

    /// <summary>Comma-separated CIDRs or single addresses.</summary>
    private static IPNetwork[] ParseNetworks(string v)
    {
        var list = new List<IPNetwork>();
        foreach (var part in v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (IPNetwork.TryParse(part, out var net)) list.Add(net);
            else if (IPAddress.TryParse(part, out var ip)) list.Add(new IPNetwork(ip, ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128));
            else throw new ArgumentException($"SPYGLASS_TRUSTED_PROXIES: '{part}' is not a CIDR or an address");
        }
        return [.. list];
    }

    public static void Validate(CollectorOptions o)
    {
        if (o.Token is { Length: 0 })
            throw new ArgumentException("SPYGLASS_TOKEN is empty; set a token or unset it (and SPYGLASS_INGEST_INSECURE=1 to run without), 03 §9");
        if (o.Auth == AuthMode.Header && string.IsNullOrWhiteSpace(o.AuthHeader))
            throw new ArgumentException("SPYGLASS_AUTH_HEADER is empty");
        if (o.Token is null && !o.IngestInsecure)
            throw new ArgumentException("SPYGLASS_TOKEN is required for ingest (or SPYGLASS_INGEST_INSECURE=1), 03 §9");
        if (o.Auth == AuthMode.Password && string.IsNullOrEmpty(o.AdminPassword))
            throw new ArgumentException("SPYGLASS_ADMIN_PASSWORD is required with SPYGLASS_AUTH=password, 03 §9");
        if (o.Auth == AuthMode.None && !IPAddress.IsLoopback(o.Bind.Address))
            throw new ArgumentException("SPYGLASS_AUTH=none is allowed only on a loopback bind, 03 §9");
        if (o.Workers < 1) throw new ArgumentException("workers must be >= 1");
    }
}
