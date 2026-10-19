using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Spyglass.Proxy.Config;
using Spyglass.Proxy.Diagnostics;

namespace Spyglass.Proxy.Topology;

/// <summary>
/// In-tree RFC 1035 resolver for <c>mongodb+srv://</c> (01 §5.1 step 1): SRV <c>_mongodb._tcp.&lt;host&gt;</c>
/// and TXT <c>&lt;host&gt;</c> over UDP against the <c>/etc/resolv.conf</c> nameservers, with TCP on truncation.
/// Applies the initial-DNS-seedlist rules: SRV targets must share the parent domain of the host, and TXT
/// may only carry <c>replicaSet</c>, <c>authSource</c> and <c>loadBalanced</c>.
/// </summary>
public static class SrvResolver
{
    private const ushort TypeSrv = 33, TypeTxt = 16;

    /// <summary>Test hook: nameservers to use instead of /etc/resolv.conf.</summary>
    public static IReadOnlyList<IPEndPoint>? NameserversOverride { get; set; }

    public static async Task<IReadOnlyList<HostPort>> ResolveSeedsAsync(MongoUri uri, CancellationToken ct)
    {
        var host = uri.Hosts[0].Host;
        if (host.Count(c => c == '.') < 2)
            throw new ConfigException($"mongodb+srv host '{host}' must have at least three labels (seedlist spec)");
        var parent = host[(host.IndexOf('.') + 1)..];

        var srv = await QueryAsync("_mongodb._tcp." + host, TypeSrv, ct).ConfigureAwait(false);
        var seeds = new List<HostPort>();
        foreach (var rr in srv)
        {
            if (rr.Type != TypeSrv) continue;
            var target = rr.SrvTarget!.TrimEnd('.').ToLowerInvariant();
            if (!target.EndsWith("." + parent, StringComparison.Ordinal))
                throw new ConfigException($"SRV record {target} is not under {parent} (seedlist spec forbids it)");
            seeds.Add(new HostPort(target, rr.SrvPort));
        }
        if (seeds.Count == 0)
            throw new ConfigException($"no SRV records for _mongodb._tcp.{host}");

        var txt = await QueryAsync(host, TypeTxt, ct).ConfigureAwait(false);
        var txtRecords = txt.Where(r => r.Type == TypeTxt).ToList();
        if (txtRecords.Count > 1)
            throw new ConfigException($"{host} has more than one TXT record (seedlist spec forbids it)");
        if (txtRecords.Count == 1)
        {
            foreach (var kv in txtRecords[0].Txt!.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var key = kv.Split('=')[0].ToLowerInvariant();
                if (key is not ("replicaset" or "authsource" or "loadbalanced"))
                    throw new ConfigException($"TXT option '{key}' for {host} is not allowed (seedlist spec)");
                if (key == "loadbalanced" && kv.EndsWith("=true", StringComparison.OrdinalIgnoreCase))
                    throw new ConfigException("loadBalanced deployments are not supported in v1 (README §1.2)");
            }
        }

        Log.Info($"SRV {host}: {string.Join(", ", seeds)}");
        return seeds;
    }

    internal sealed record Rr(ushort Type, string? SrvTarget, ushort SrvPort, string? Txt);

    internal static async Task<List<Rr>> QueryAsync(string name, ushort type, CancellationToken ct)
    {
        var servers = NameserversOverride ?? ReadResolvConf();
        Exception? last = null;
        foreach (var ns in servers)
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    var id = (ushort)Random.Shared.Next(ushort.MaxValue);
                    var query = BuildQuery(id, name, type);
                    var response = await UdpAsync(ns, query, ct).ConfigureAwait(false);
                    if (IsTruncated(response))
                        response = await TcpAsync(ns, query, ct).ConfigureAwait(false);
                    return Parse(response, id);
                }
                catch (Exception e) when (e is SocketException or TimeoutException or FormatException or IOException)
                {
                    last = e;
                }
            }
        }
        throw new ConfigException($"DNS query {name} failed: {last?.Message ?? "no nameservers"}");
    }

    private static List<IPEndPoint> ReadResolvConf()
    {
        var result = new List<IPEndPoint>();
        if (File.Exists("/etc/resolv.conf"))
            foreach (var line in File.ReadLines("/etc/resolv.conf"))
            {
                var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && parts[0] == "nameserver" && IPAddress.TryParse(parts[1].Split('%')[0], out var ip))
                    result.Add(new IPEndPoint(ip, 53));
            }
        if (result.Count == 0)
            result.Add(new IPEndPoint(IPAddress.Loopback, 53));
        return result;
    }

    internal static byte[] BuildQuery(ushort id, string name, ushort type)
    {
        var ms = new MemoryStream();
        Span<byte> h = stackalloc byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(h, id);
        BinaryPrimitives.WriteUInt16BigEndian(h[2..], 0x0100); // RD
        BinaryPrimitives.WriteUInt16BigEndian(h[4..], 1);      // QDCOUNT
        ms.Write(h);
        foreach (var label in name.TrimEnd('.').Split('.'))
        {
            var b = Encoding.ASCII.GetBytes(label);
            if (b.Length is 0 or > 63) throw new ConfigException($"invalid DNS name {name}");
            ms.WriteByte((byte)b.Length);
            ms.Write(b);
        }
        ms.WriteByte(0);
        Span<byte> q = stackalloc byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(q, type);
        BinaryPrimitives.WriteUInt16BigEndian(q[2..], 1); // IN
        ms.Write(q);
        return ms.ToArray();
    }

    private static bool IsTruncated(byte[] r) => r.Length >= 4 && (r[2] & 0x02) != 0;

    private static async Task<byte[]> UdpAsync(IPEndPoint ns, byte[] query, CancellationToken ct)
    {
        using var udp = new Socket(ns.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        await udp.ConnectAsync(ns, ct).ConfigureAwait(false);
        await udp.SendAsync(query, SocketFlags.None, ct).ConfigureAwait(false);
        var buf = new byte[65535];
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            var n = await udp.ReceiveAsync(buf, SocketFlags.None, timeout.Token).ConfigureAwait(false);
            return buf[..n];
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"no answer from {ns}");
        }
    }

    private static async Task<byte[]> TcpAsync(IPEndPoint ns, byte[] query, CancellationToken ct)
    {
        using var tcp = new Socket(ns.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        await tcp.ConnectAsync(ns, timeout.Token).ConfigureAwait(false);
        await using var s = new NetworkStream(tcp);
        var framed = new byte[query.Length + 2];
        BinaryPrimitives.WriteUInt16BigEndian(framed, (ushort)query.Length);
        query.CopyTo(framed, 2);
        await s.WriteAsync(framed, timeout.Token).ConfigureAwait(false);
        var len = new byte[2];
        await s.ReadExactlyAsync(len, timeout.Token).ConfigureAwait(false);
        var resp = new byte[BinaryPrimitives.ReadUInt16BigEndian(len)];
        await s.ReadExactlyAsync(resp, timeout.Token).ConfigureAwait(false);
        return resp;
    }

    internal static List<Rr> Parse(byte[] m, ushort id)
    {
        if (m.Length < 12) throw new FormatException("short DNS response");
        if (BinaryPrimitives.ReadUInt16BigEndian(m) != id) throw new FormatException("DNS response id mismatch");
        var rcode = m[3] & 0x0F;
        if (rcode == 3) return []; // NXDOMAIN
        if (rcode != 0) throw new FormatException($"DNS rcode {rcode}");
        var qd = BinaryPrimitives.ReadUInt16BigEndian(m.AsSpan(4));
        var an = BinaryPrimitives.ReadUInt16BigEndian(m.AsSpan(6));
        var p = 12;
        for (var i = 0; i < qd; i++)
        {
            ReadName(m, ref p);
            p += 4;
        }
        var result = new List<Rr>();
        for (var i = 0; i < an; i++)
        {
            ReadName(m, ref p);
            if (p + 10 > m.Length) throw new FormatException("truncated RR");
            var type = BinaryPrimitives.ReadUInt16BigEndian(m.AsSpan(p));
            var rdlen = BinaryPrimitives.ReadUInt16BigEndian(m.AsSpan(p + 8));
            p += 10;
            if (p + rdlen > m.Length) throw new FormatException("truncated RDATA");
            var rd = p;
            if (type == TypeSrv)
            {
                var port = BinaryPrimitives.ReadUInt16BigEndian(m.AsSpan(rd + 4));
                var q = rd + 6;
                result.Add(new Rr(type, ReadName(m, ref q), port, null));
            }
            else if (type == TypeTxt)
            {
                var sb = new StringBuilder();
                var q = rd;
                while (q < rd + rdlen)
                {
                    var l = m[q++];
                    sb.Append(Encoding.ASCII.GetString(m, q, Math.Min(l, rd + rdlen - q)));
                    q += l;
                }
                result.Add(new Rr(type, null, 0, sb.ToString()));
            }
            p += rdlen;
        }
        return result;
    }

    private static string ReadName(byte[] m, ref int p)
    {
        var labels = new List<string>();
        var jumped = false;
        var q = p;
        for (var guard = 0; guard < 128; guard++)
        {
            if (q >= m.Length) throw new FormatException("name past end");
            var len = m[q];
            if (len == 0)
            {
                if (!jumped) p = q + 1;
                return string.Join('.', labels);
            }
            if ((len & 0xC0) == 0xC0)
            {
                if (q + 1 >= m.Length) throw new FormatException("bad pointer");
                var target = ((len & 0x3F) << 8) | m[q + 1];
                if (!jumped) p = q + 2;
                jumped = true;
                q = target;
                continue;
            }
            if (q + 1 + len > m.Length) throw new FormatException("label past end");
            labels.Add(Encoding.ASCII.GetString(m, q + 1, len));
            q += 1 + len;
        }
        throw new FormatException("name compression loop");
    }
}
