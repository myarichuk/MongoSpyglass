using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace Spyglass.Proxy.Config;

/// <summary>
/// The parts of a MongoDB connection string the proxy needs: seeds, SRV, TLS options and
/// <c>replicaSet</c>. Credentials and the default database are ignored: the proxy never
/// authenticates (monitor hellos need no auth) and client connections bring their own.
/// </summary>
public sealed record MongoUri(
    bool IsSrv,
    IReadOnlyList<HostPort> Hosts,
    IReadOnlyDictionary<string, string> Options)
{
    public string? ReplicaSet => Options.GetValueOrDefault("replicaset");

    public bool? Tls =>
        Options.TryGetValue("tls", out var t) || Options.TryGetValue("ssl", out t)
            ? t.Equals("true", StringComparison.OrdinalIgnoreCase)
            : null;

    public bool DirectConnection => Options.GetValueOrDefault("directconnection") is { } d && d.Equals("true", StringComparison.OrdinalIgnoreCase);

    public static MongoUri Parse(string uri)
    {
        bool srv;
        string rest;
        if (uri.StartsWith("mongodb+srv://", StringComparison.Ordinal))
        {
            srv = true;
            rest = uri["mongodb+srv://".Length..];
        }
        else if (uri.StartsWith("mongodb://", StringComparison.Ordinal))
        {
            srv = false;
            rest = uri["mongodb://".Length..];
        }
        else
            throw new FormatException("scheme must be mongodb:// or mongodb+srv://");

        // userinfo: '/', '?' and '@' must be percent-encoded inside it (connection string spec),
        // so the last '@' before the first '/' or '?' ends it.
        var userEnd = IndexOfHostEnd(rest);
        var at = userEnd == 0 ? -1 : rest.LastIndexOf('@', userEnd - 1);
        if (at >= 0)
            rest = rest[(at + 1)..];

        var hostEnd = IndexOfHostEnd(rest);
        var hostPart = rest[..hostEnd];
        var tail = rest[hostEnd..];

        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var q = tail.IndexOf('?');
        if (q >= 0)
        {
            foreach (var kv in tail[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = kv.IndexOf('=');
                if (eq <= 0) throw new FormatException($"malformed option '{kv}'");
                options[Uri.UnescapeDataString(kv[..eq]).ToLowerInvariant()] = Uri.UnescapeDataString(kv[(eq + 1)..]);
            }
        }

        if (hostPart.Length == 0)
            throw new FormatException("no host");

        var hosts = new List<HostPort>();
        foreach (var h in hostPart.Split(','))
            hosts.Add(HostPort.Parse(Uri.UnescapeDataString(h), defaultPort: 27017));

        if (srv)
        {
            if (hosts.Count != 1)
                throw new FormatException("mongodb+srv:// takes exactly one host");
            if (hostPart.Contains(':') && !hostPart.StartsWith('['))
                throw new FormatException("mongodb+srv:// host must not have a port");
        }

        return new MongoUri(srv, hosts, options);
    }

    private static int IndexOfHostEnd(string s)
    {
        var slash = s.IndexOf('/');
        var q = s.IndexOf('?');
        if (slash < 0) return q < 0 ? s.Length : q;
        return q < 0 ? slash : Math.Min(slash, q);
    }
}

/// <summary>A normalized endpoint (02 §3.2): lower-case host, no trailing dot, IPv6 in brackets, explicit port.</summary>
public readonly record struct HostPort(string Host, int Port)
{
    public override string ToString() =>
        Host.Contains(':') ? $"[{Host}]:{Port.ToString(CultureInfo.InvariantCulture)}" : $"{Host}:{Port.ToString(CultureInfo.InvariantCulture)}";

    public static HostPort Parse(string s, int defaultPort = 27017)
    {
        if (!TryParse(s, out var hp, defaultPort))
            throw new FormatException($"'{s}' is not host[:port]");
        return hp;
    }

    public static bool TryParse(string s, out HostPort result, int defaultPort = 27017)
    {
        result = default;
        s = s.Trim();
        if (s.Length == 0) return false;

        string host;
        string? port = null;
        if (s[0] == '[')
        {
            var close = s.IndexOf(']');
            if (close < 0) return false;
            host = s[1..close];
            var after = s[(close + 1)..];
            if (after.Length > 0)
            {
                if (after[0] != ':') return false;
                port = after[1..];
            }
        }
        else
        {
            var colons = s.Count(c => c == ':');
            if (colons > 1)
            {
                host = s; // bare IPv6 literal without port
            }
            else if (colons == 1)
            {
                var c = s.IndexOf(':');
                host = s[..c];
                port = s[(c + 1)..];
            }
            else host = s;
        }

        var p = defaultPort;
        if (port != null && (!int.TryParse(port, NumberStyles.None, CultureInfo.InvariantCulture, out p) || p is < 1 or > 65535))
            return false;

        result = new HostPort(NormalizeHost(host), p);
        return result.Host.Length > 0;
    }

    public static string NormalizeHost(string host)
    {
        host = host.TrimEnd('.').ToLowerInvariant();
        if (host.Contains(':') && IPAddress.TryParse(host, out var ip) && ip.AddressFamily == AddressFamily.InterNetworkV6)
            return ip.ToString(); // compressed form, no brackets (added by ToString)
        return host;
    }
}
