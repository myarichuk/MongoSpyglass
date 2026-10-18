using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Spyglass.Proxy.Config;
using Spyglass.Proxy.Topology;

namespace Spyglass.Proxy.Tests;

/// <summary>
/// 01 §5.1 step 1 / 06 §2.2: the in-tree resolver against an in-test DNS server (UDP, TCP fallback on
/// truncation, name compression) and the initial-DNS-seedlist rules.
/// </summary>
[Collection("srv")] // SrvResolver.NameserversOverride is process-wide
public sealed class SrvTests
{
    private sealed class FakeDns : IDisposable
    {
        private readonly UdpClient _udp;
        private readonly TcpListener _tcp;
        private readonly CancellationTokenSource _cts = new();
        public IPEndPoint Endpoint { get; }
        public Dictionary<(string Name, ushort Type), List<byte[]>> Answers { get; } = new();
        public bool TruncateUdp { get; set; }
        public int TcpQueries;

        public FakeDns()
        {
            _udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            var port = ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;
            _tcp = new TcpListener(IPAddress.Loopback, port);
            _tcp.Start();
            Endpoint = new IPEndPoint(IPAddress.Loopback, port);
            _ = Task.Run(UdpLoop);
            _ = Task.Run(TcpLoop);
        }

        private async Task UdpLoop()
        {
            while (!_cts.IsCancellationRequested)
            {
                UdpReceiveResult r;
                try { r = await _udp.ReceiveAsync(_cts.Token); } catch { return; }
                await _udp.SendAsync(Answer(r.Buffer, TruncateUdp), r.RemoteEndPoint);
            }
        }

        private async Task TcpLoop()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient c;
                try { c = await _tcp.AcceptTcpClientAsync(_cts.Token); } catch { return; }
                Interlocked.Increment(ref TcpQueries);
                using (c)
                {
                    var s = c.GetStream();
                    var len = new byte[2];
                    await s.ReadExactlyAsync(len);
                    var q = new byte[BinaryPrimitives.ReadUInt16BigEndian(len)];
                    await s.ReadExactlyAsync(q);
                    var a = Answer(q, truncate: false);
                    var framed = new byte[a.Length + 2];
                    BinaryPrimitives.WriteUInt16BigEndian(framed, (ushort)a.Length);
                    a.CopyTo(framed, 2);
                    await s.WriteAsync(framed);
                }
            }
        }

        private byte[] Answer(byte[] q, bool truncate)
        {
            // parse question name/type
            var p = 12;
            var labels = new List<string>();
            while (q[p] != 0) { labels.Add(Encoding.ASCII.GetString(q, p + 1, q[p])); p += 1 + q[p]; }
            var qEnd = p + 1 + 4;
            var type = BinaryPrimitives.ReadUInt16BigEndian(q.AsSpan(p + 1));
            var rrs = Answers.GetValueOrDefault((string.Join('.', labels), type)) ?? [];
            var ms = new MemoryStream();
            ms.Write(q, 0, 2);
            ms.Write([(byte)(0x81 | (truncate ? 0x02 : 0)), 0x80]); // QR|RD, (TC), RA
            ms.Write([0, 1, 0, (byte)(truncate ? 0 : rrs.Count), 0, 0, 0, 0]);
            ms.Write(q, 12, qEnd - 12);
            if (!truncate)
                foreach (var rd in rrs)
                {
                    ms.Write([0xC0, 12]); // pointer to the question name
                    var h = new byte[10];
                    BinaryPrimitives.WriteUInt16BigEndian(h, type);
                    BinaryPrimitives.WriteUInt16BigEndian(h.AsSpan(2), 1);
                    BinaryPrimitives.WriteInt32BigEndian(h.AsSpan(4), 60);
                    BinaryPrimitives.WriteUInt16BigEndian(h.AsSpan(8), (ushort)rd.Length);
                    ms.Write(h);
                    ms.Write(rd);
                }
            return ms.ToArray();
        }

        public static byte[] Srv(string target, ushort port)
        {
            var ms = new MemoryStream();
            ms.Write([0, 0, 0, 0]); // priority, weight
            ms.Write([(byte)(port >> 8), (byte)port]);
            foreach (var l in target.Split('.')) { ms.WriteByte((byte)l.Length); ms.Write(Encoding.ASCII.GetBytes(l)); }
            ms.WriteByte(0);
            return ms.ToArray();
        }

        public static byte[] Txt(string text)
        {
            var b = Encoding.ASCII.GetBytes(text);
            return [(byte)b.Length, .. b];
        }

        public void Dispose()
        {
            _cts.Cancel();
            _udp.Dispose();
            _tcp.Stop();
        }
    }

    private static async Task<IReadOnlyList<HostPort>> Resolve(FakeDns dns, string uri)
    {
        SrvResolver.NameserversOverride = [dns.Endpoint];
        try { return await SrvResolver.ResolveSeedsAsync(MongoUri.Parse(uri), CancellationToken.None); }
        finally { SrvResolver.NameserversOverride = null; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Resolves_srv_and_txt(bool truncated)
    {
        using var dns = new FakeDns { TruncateUdp = truncated };
        dns.Answers[("_mongodb._tcp.cluster0.example.net", 33)] = [FakeDns.Srv("a.example.net", 27017), FakeDns.Srv("B.Example.Net", 27018)];
        dns.Answers[("cluster0.example.net", 16)] = [FakeDns.Txt("replicaSet=rs0&authSource=admin")];
        var seeds = await Resolve(dns, "mongodb+srv://cluster0.example.net/");
        Assert.Equal(["a.example.net:27017", "b.example.net:27018"], seeds.Select(s => s.ToString()));
        Assert.Equal(truncated ? 2 : 0, dns.TcpQueries);
    }

    [Fact]
    public async Task Rejects_targets_outside_the_parent_domain()
    {
        using var dns = new FakeDns();
        dns.Answers[("_mongodb._tcp.cluster0.example.net", 33)] = [FakeDns.Srv("evil.attacker.org", 27017)];
        var e = await Assert.ThrowsAsync<ConfigException>(() => Resolve(dns, "mongodb+srv://cluster0.example.net/"));
        Assert.Contains("not under example.net", e.Message);
    }

    [Theory]
    [InlineData("ssl=false", "not allowed")]
    [InlineData("loadBalanced=true", "loadBalanced")]
    public async Task Rejects_disallowed_txt_options(string txt, string expected)
    {
        using var dns = new FakeDns();
        dns.Answers[("_mongodb._tcp.cluster0.example.net", 33)] = [FakeDns.Srv("a.example.net", 27017)];
        dns.Answers[("cluster0.example.net", 16)] = [FakeDns.Txt(txt)];
        var e = await Assert.ThrowsAsync<ConfigException>(() => Resolve(dns, "mongodb+srv://cluster0.example.net/"));
        Assert.Contains(expected, e.Message);
    }

    [Fact]
    public async Task Requires_three_labels_and_records()
    {
        using var dns = new FakeDns();
        await Assert.ThrowsAsync<ConfigException>(() => Resolve(dns, "mongodb+srv://example.net/"));
        var e = await Assert.ThrowsAsync<ConfigException>(() => Resolve(dns, "mongodb+srv://cluster0.example.net/"));
        Assert.Contains("no SRV records", e.Message);
    }
}
