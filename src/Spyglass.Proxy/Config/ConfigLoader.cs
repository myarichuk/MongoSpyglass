using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Spyglass.Proxy.Config;

/// <summary>
/// file (YAML subset or JSON) → <c>SPYGLASS_*</c> environment → CLI flags, later wins (01 §2).
/// Every key is checked against a fixed table so a typo is a startup error naming the key,
/// not a silently ignored setting.
/// </summary>
public static class ConfigLoader
{
    public static ProxyConfig Load(string[] args, IDictionary<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariables()
            .Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(e => (string)e.Key, e => (string?)e.Value);

        var (configPath, cli) = ParseArgs(args);
        configPath ??= environment.TryGetValue("SPYGLASS_CONFIG", out var p) ? p : null;

        var values = new Dictionary<string, (string? Value, string Source)>(StringComparer.OrdinalIgnoreCase);

        if (configPath != null)
        {
            if (!File.Exists(configPath))
                throw new ConfigException($"config file '{configPath}' not found");
            var text = File.ReadAllText(configPath);
            var flat = configPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                ? FlattenJson(text)
                : MiniYaml.Parse(text);
            foreach (var (k, v) in flat)
                values[k] = (v, configPath);
        }

        foreach (var (k, v) in environment)
        {
            if (!k.StartsWith("SPYGLASS_", StringComparison.Ordinal) || ExporterOrVariableEnv.Contains(k))
                continue;
            values[k["SPYGLASS_".Length..].Replace("__", ".")] = (v, "environment " + k);
        }

        foreach (var (k, v) in cli)
            values[k] = (v, "--" + k);

        var config = new ProxyConfig();
        foreach (var (key, (raw, source)) in values)
        {
            if (!Binders.TryGetValue(key, out var bind))
                throw new ConfigException($"unknown configuration key '{key}' (from {source})");
            var expanded = raw == null ? null : ExpandVariables(raw, environment, key);
            try
            {
                bind(config, expanded);
            }
            catch (FormatException e)
            {
                throw new ConfigException($"invalid value for '{key}' (from {source}): {e.Message}");
            }
        }

        Validate(config);
        return config;
    }

    // Not proxy keys: SPYGLASS_TOKEN is referenced as ${SPYGLASS_TOKEN}; the rest belong to exporters
    // that may run in the same environment (04 §2, 05 §2).
    private static readonly HashSet<string> ExporterOrVariableEnv =
        ["SPYGLASS_CONFIG", "SPYGLASS_TOKEN", "SPYGLASS_INGEST", "SPYGLASS_DISABLED", "SPYGLASS_CALLSITES"];

    private static (string? ConfigPath, List<(string Key, string? Value)> Values) ParseArgs(string[] args)
    {
        string? configPath = null;
        var list = new List<(string, string?)>();
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (!a.StartsWith("--", StringComparison.Ordinal))
                throw new ConfigException($"unexpected argument '{a}'; flags are --key=value or --key value");
            var body = a[2..];
            string key;
            string? value;
            var eq = body.IndexOf('=');
            if (eq >= 0)
            {
                key = body[..eq];
                value = body[(eq + 1)..];
            }
            else
            {
                if (i + 1 >= args.Length)
                    throw new ConfigException($"flag '{a}' needs a value");
                key = body;
                value = args[++i];
            }
            if (key.Equals("config", StringComparison.OrdinalIgnoreCase))
                configPath = value;
            else
                list.Add((key.Replace(':', '.'), value));
        }
        return (configPath, list);
    }

    private static readonly Regex Variable = new(@"\$\{([A-Za-z_][A-Za-z0-9_]*)\}", RegexOptions.Compiled);

    private static string? ExpandVariables(string raw, IDictionary<string, string?> env, string key)
    {
        // A value that is exactly one unset variable becomes null (e.g. token: "${SPYGLASS_TOKEN}" with no token).
        var whole = Variable.Match(raw);
        if (whole.Success && whole.Length == raw.Length)
            return env.TryGetValue(whole.Groups[1].Value, out var v) && !string.IsNullOrEmpty(v) ? v : null;

        return Variable.Replace(raw, m =>
            env.TryGetValue(m.Groups[1].Value, out var v) && v != null
                ? v
                : throw new ConfigException($"'{key}' references unset environment variable {m.Groups[1].Value}"));
    }

    private static Dictionary<string, string?> FlattenJson(string text)
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        void Walk(JsonElement e, string prefix)
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var p in e.EnumerateObject())
                        Walk(p.Value, prefix.Length == 0 ? p.Name : prefix + "." + p.Name);
                    break;
                case JsonValueKind.Array:
                    result[prefix] = string.Join(',', e.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : x.GetRawText()));
                    break;
                case JsonValueKind.Null:
                    result[prefix] = null;
                    break;
                case JsonValueKind.String:
                    result[prefix] = e.GetString();
                    break;
                default:
                    result[prefix] = e.GetRawText();
                    break;
            }
        }
        Walk(doc.RootElement, "");
        return result;
    }

    // ---- binding ---------------------------------------------------------------------------

    private static readonly Dictionary<string, Action<ProxyConfig, string?>> Binders =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["upstream.uri"] = (c, v) => c.Upstream.Uri = v,
            ["upstream.topology"] = (c, v) => c.Upstream.Topology = Enum<TopologyKind>(v),
            ["upstream.tls.enabled"] = (c, v) => c.Upstream.Tls.Enabled = v?.ToLowerInvariant() switch
            {
                "auto" or null => TriState.Auto,
                "true" => TriState.True,
                "false" => TriState.False,
                _ => throw new FormatException("expected auto, true or false")
            },
            ["upstream.tls.caFile"] = (c, v) => c.Upstream.Tls.CaFile = v,
            ["upstream.tls.allowInvalidCertificates"] = (c, v) => c.Upstream.Tls.AllowInvalidCertificates = Bool(v),
            ["upstream.tls.allowInvalidHostnames"] = (c, v) => c.Upstream.Tls.AllowInvalidHostnames = Bool(v),

            ["listen.bindAddress"] = (c, v) => c.Listen.BindAddress = Required(v),
            ["listen.advertiseHost"] = (c, v) => c.Listen.AdvertiseHost = Required(v),
            ["listen.basePort"] = (c, v) => c.Listen.BasePort = Int(v, 1, 65535),
            ["listen.portRange"] = (c, v) =>
            {
                var parts = Required(v).Split(',', StringSplitOptions.TrimEntries);
                if (parts.Length != 2) throw new FormatException("expected [first, last]");
                c.Listen.PortRangeStart = Int(parts[0], 1, 65535);
                c.Listen.PortRangeEnd = Int(parts[1], 1, 65535);
            },
            ["listen.tls.mode"] = (c, v) => c.Listen.Tls.Mode = Enum<DownstreamTlsMode>(v),
            ["listen.tls.certFile"] = (c, v) => c.Listen.Tls.CertFile = v,
            ["listen.tls.keyFile"] = (c, v) => c.Listen.Tls.KeyFile = v,

            ["compression"] = (c, v) => c.Compression = Enum<CompressionMode>(v),

            ["capture.commandBytes"] = (c, v) => c.Capture.CommandBytes = Int(v, 0, 16 * 1024 * 1024),
            ["capture.replyBytes"] = (c, v) => c.Capture.ReplyBytes = Int(v, 0, 16 * 1024 * 1024),
            ["capture.values"] = (c, v) => c.Capture.Values = Enum<ValueMode>(v),
            ["capture.truncateStringsAt"] = (c, v) => c.Capture.TruncateStringsAt = Int(v, 1, 1 << 20),
            ["capture.redactFieldPattern"] = (c, v) =>
            {
                var pattern = Required(v);
                try { _ = new Regex(pattern); }
                catch (ArgumentException e) { throw new FormatException(e.Message); }
                c.Capture.RedactFieldPattern = pattern;
            },

            ["limits.maxClientConnections"] = (c, v) => c.Limits.MaxClientConnections = Int(v, 1, 1 << 20),
            ["limits.observeQueueBytes"] = (c, v) => c.Limits.ObserveQueueBytes = Long(v, 0), // 0 = observe nothing (forwarding-only A/B runs)
            ["limits.maxPendingPerConnection"] = (c, v) => c.Limits.MaxPendingPerConnection = Int(v, 1, 1 << 20),
            ["limits.helloRewriteMaxBytes"] = (c, v) => c.Limits.HelloRewriteMaxBytes = Int(v, 4096, 48_000_000),
            ["limits.readBufferBytes"] = (c, v) => c.Limits.ReadBufferBytes = Int(v, 4096, 16 * 1024 * 1024),

            ["collector.endpoint"] = (c, v) => c.Collector.Endpoint = HttpUrl(v),
            ["collector.token"] = (c, v) => c.Collector.Token = v,
            ["collector.batchMaxRecords"] = (c, v) => c.Collector.BatchMaxRecords = Int(v, 1, 2000),
            ["collector.batchMaxBytes"] = (c, v) => c.Collector.BatchMaxBytes = Int(v, 4096, 1024 * 1024),
            ["collector.flushIntervalMs"] = (c, v) => c.Collector.FlushIntervalMs = Int(v, 10, 60_000),
            ["collector.bufferBytes"] = (c, v) => c.Collector.BufferBytes = Long(v, 64 * 1024),

            ["discovery.advertiseIngest"] = (c, v) => c.Discovery.AdvertiseIngest = HttpUrl(v),
            ["discovery.advertiseToken"] = (c, v) => c.Discovery.AdvertiseToken = Bool(v),

            ["metrics.bind"] = (c, v) => c.Metrics.Bind = v,
            ["metrics.maxSeries"] = (c, v) => c.Metrics.MaxSeries = Int(v, 1, 1_000_000),
            ["metrics.commandLabels"] = (_, v) =>
            {
                // The label set is fixed in v1; accept only the spec value so configs written against the spec load.
                var labels = Required(v).Split(',', StringSplitOptions.TrimEntries);
                if (!labels.SequenceEqual(["command", "database", "collection", "app"]))
                    throw new FormatException("v1 supports only [command, database, collection, app]");
            },

            ["logLevel"] = (c, v) => c.LogLevel = Required(v) is "trace" or "debug" or "info" or "warn" or "error"
                ? v!
                : throw new FormatException("expected trace, debug, info, warn or error"),
        };

    private static string Required(string? v) => v ?? throw new FormatException("value is required");

    private static bool Bool(string? v) => Required(v).ToLowerInvariant() switch
    {
        "true" or "yes" or "1" => true,
        "false" or "no" or "0" => false,
        _ => throw new FormatException("expected true or false")
    };

    private static int Int(string? v, int min, int max)
    {
        if (!int.TryParse(Required(v), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i))
            throw new FormatException($"'{v}' is not an integer");
        if (i < min || i > max)
            throw new FormatException($"{i} is outside [{min}, {max}]");
        return i;
    }

    private static long Long(string? v, long min)
    {
        if (!long.TryParse(Required(v), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i))
            throw new FormatException($"'{v}' is not an integer");
        if (i < min)
            throw new FormatException($"{i} is below {min}");
        return i;
    }

    private static T Enum<T>(string? v) where T : struct, System.Enum =>
        System.Enum.TryParse<T>(Required(v), ignoreCase: true, out var r) && System.Enum.IsDefined(r)
            ? r
            : throw new FormatException($"expected one of {string.Join(", ", System.Enum.GetNames<T>().Select(n => n.ToLowerInvariant()))}");

    private static string? HttpUrl(string? v)
    {
        if (string.IsNullOrEmpty(v)) return null;
        if (!Uri.TryCreate(v, UriKind.Absolute, out var u) || (u.Scheme != "http" && u.Scheme != "https"))
            throw new FormatException($"'{v}' is not an http(s) URL");
        return v;
    }

    // ---- validation ------------------------------------------------------------------------

    private static void Validate(ProxyConfig c)
    {
        if (string.IsNullOrWhiteSpace(c.Upstream.Uri))
            throw new ConfigException("'upstream.uri' is required");

        MongoUri parsed;
        try { parsed = MongoUri.Parse(c.Upstream.Uri); }
        catch (FormatException e) { throw new ConfigException($"invalid 'upstream.uri': {e.Message}"); }

        if (parsed.IsSrv && c.Upstream.Tls.Enabled == TriState.False)
            throw new ConfigException("'upstream.tls.enabled' is false but 'upstream.uri' is mongodb+srv:// (SRV implies TLS)");

        if (!IPAddress.TryParse(c.Listen.BindAddress, out _))
            throw new ConfigException($"'listen.bindAddress' must be an IP address, got '{c.Listen.BindAddress}'");

        if (c.Listen.PortRangeStart > c.Listen.PortRangeEnd)
            throw new ConfigException("'listen.portRange' first port is greater than last");
        if (c.Listen.BasePort < c.Listen.PortRangeStart || c.Listen.BasePort > c.Listen.PortRangeEnd)
            throw new ConfigException("'listen.basePort' must lie inside 'listen.portRange'");

        if ((c.Listen.Tls.CertFile == null) != (c.Listen.Tls.KeyFile == null))
            throw new ConfigException("'listen.tls.certFile' and 'listen.tls.keyFile' must be set together");

        if (c.Metrics.Bind != null && !IPEndPoint.TryParse(c.Metrics.Bind, out _))
            throw new ConfigException($"'metrics.bind' must be ip:port, got '{c.Metrics.Bind}'");

        if (c.Collector.BatchMaxBytes > c.Collector.BufferBytes)
            throw new ConfigException("'collector.batchMaxBytes' must not exceed 'collector.bufferBytes'");
    }

    /// <summary>Warnings that do not stop startup (01 §2).</summary>
    public static IEnumerable<string> Warnings(ProxyConfig c)
    {
        if (IPAddress.TryParse(c.Listen.BindAddress, out var ip) && !IPAddress.IsLoopback(ip) && c.Listen.Tls.Mode == DownstreamTlsMode.None)
            yield return $"listen.bindAddress={c.Listen.BindAddress} with listen.tls.mode=none: client traffic, including credentials exchanged during authentication, is plaintext on the network";
        if (c.Upstream.Tls.AllowInvalidCertificates)
            yield return "upstream.tls.allowInvalidCertificates=true: upstream certificates are not validated";
        if (c.Upstream.Tls.AllowInvalidHostnames)
            yield return "upstream.tls.allowInvalidHostnames=true: upstream certificate hostnames are not checked";
    }
}
