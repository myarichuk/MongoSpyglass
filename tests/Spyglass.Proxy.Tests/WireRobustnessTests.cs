using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Spyglass.Proxy.Config;
using Spyglass.Proxy.Diagnostics;
using Spyglass.Proxy.Forwarding;
using Spyglass.Proxy.Observation;
using Spyglass.Proxy.Tests.Infrastructure;
using Spyglass.Proxy.Wire;

namespace Spyglass.Proxy.Tests;

/// <summary>Frames and wire builders shared by the robustness tests.</summary>
internal static class WireFrames
{
    public static byte[] Msg(int requestId, Action<BsonWriter> body, uint flags = 0, int responseTo = 0)
    {
        var w = new BsonWriter(256);
        w.WriteRaw(new byte[FrameHeader.Size]);
        w.WriteInt32Raw((int)flags);
        w.WriteByteRaw(0);
        w.StartDocument();
        body(w);
        w.EndDocument();
        var frame = w.ToArray();
        new FrameHeader(frame.Length, requestId, responseTo, OpCodes.Msg).Write(frame);
        return frame;
    }

    public static byte[] Reply(int responseTo, Action<BsonWriter> body) => Msg(1, body, responseTo: responseTo);

    public static byte[] Ping(int requestId = 5) => Msg(requestId, w => { w.WriteInt32("ping", 1); w.WriteString("$db", "admin"); });

    /// <summary>An OP_MSG header + flags followed by raw section bytes.</summary>
    public static byte[] Raw(int declaredLength, params byte[][] sections)
    {
        var body = sections.SelectMany(s => s).ToArray();
        var frame = new byte[20 + body.Length];
        new FrameHeader(declaredLength, 1, 0, OpCodes.Msg).Write(frame);
        body.CopyTo(frame, 20);
        return frame;
    }

    public static byte[] Section(byte kind, int size, int extraBytes = 0)
    {
        var b = new byte[5 + extraBytes];
        b[0] = kind;
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(1), size);
        return b;
    }
}

/// <summary>A scripted mongod: answers hello (standalone or replica-set member), records every other frame.</summary>
internal sealed class FakeMongo : IAsyncDisposable
{
    private readonly string? _setName;
    private readonly Func<string[]> _hosts;
    private readonly List<Socket> _conns = [];
    private TcpListener? _listener;
    private CancellationTokenSource _cts = new();

    public FakeMongo(int port, string? setName = null, Func<string[]>? hosts = null)
    {
        Port = port;
        _setName = setName;
        _hosts = hosts ?? (() => []);
    }

    public int Port { get; }
    public string Address => $"127.0.0.1:{Port}";
    public ConcurrentQueue<byte[]> Frames { get; } = new();
    public int HelloCount;

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Loopback, Port);
        _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _listener.Start();
        var listener = _listener;
        var ct = _cts.Token;
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                Socket s;
                try { s = await listener.AcceptSocketAsync(ct); } catch { return; }
                lock (_conns) _conns.Add(s);
                _ = Task.Run(() => Serve(s, ct));
            }
        });
    }

    /// <summary>Stops listening and drops every connection: the upstream is down.</summary>
    public void Stop()
    {
        _cts.Cancel();
        _listener?.Stop();
        lock (_conns)
        {
            foreach (var c in _conns) c.Dispose();
            _conns.Clear();
        }
    }

    private async Task Serve(Socket socket, CancellationToken ct)
    {
        try
        {
            await using var stream = new NetworkStream(socket);
            var header = new byte[16];
            while (true)
            {
                await stream.ReadExactlyAsync(header, ct);
                var h = FrameHeader.Read(header);
                var frame = new byte[h.MessageLength];
                header.CopyTo(frame, 0);
                await stream.ReadExactlyAsync(frame.AsMemory(16), ct);
                if (RequestInspector.Classify(frame) == RequestKind.Hello)
                {
                    Interlocked.Increment(ref HelloCount);
                    await stream.WriteAsync(HelloReply(h.RequestId), ct);
                }
                else
                    Frames.Enqueue(frame);
            }
        }
        catch { /* closed */ }
    }

    private byte[] HelloReply(int responseTo) => WireFrames.Reply(responseTo, w =>
    {
        w.WriteBoolean("helloOk", true);
        w.WriteBoolean("isWritablePrimary", true);
        if (_setName != null)
        {
            w.WriteString("setName", _setName);
            w.StartArray("hosts");
            var i = 0;
            foreach (var h in _hosts()) w.WriteString((i++).ToString(), h);
            w.EndDocument();
            w.WriteString("me", Address);
        }
        w.WriteInt32("maxWireVersion", 21);
        w.WriteInt32("maxMessageSizeBytes", 48_000_000);
        w.WriteInt64("connectionId", 1);
        w.WriteDouble("ok", 1);
    });

    public async ValueTask DisposeAsync()
    {
        Stop();
        await Task.CompletedTask;
    }

    private static class Frames_
    {
        public static byte[] Reply(int responseTo, Action<BsonWriter> body) => WireFrames.Msg(1, body, responseTo: responseTo);
    }
}

public sealed class WireRobustnessTests
{
    // ---- PW-3 / PW-13 / PO-9: one overflow-safe section walker ----------------------------------

    [Theory]
    [InlineData(int.MaxValue)]
    [InlineData(int.MaxValue - 1)]
    [InlineData(int.MaxValue - 4)]
    [InlineData(int.MinValue)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(4)]
    public void Section_walkers_never_throw_on_hostile_sizes(int size)
    {
        foreach (var kind in new byte[] { 0, 1, 2 })
        {
            // 41-byte frame: 20 header+flags, the hostile section, padding
            var frame = WireFrames.Raw(41, WireFrames.Section(kind, size, extraBytes: 16));
            Assert.Equal(41, frame.Length);

            Assert.Equal(RequestKind.Other, RequestInspector.Classify(frame));
            Assert.True(HelloRewriter.FirstDocument(frame).IsEmpty);
            Assert.Null(HelloRewriter.StripCompression(frame, out var failure));
            Assert.Equal(RewriteFailure.Malformed, failure);
            HelloRewriter.ExtractFacts(frame, new HelloReplyFacts());

            var docs = new List<(int, int, int, int)>();
            RequestParser.FirstSequenceDocuments(frame.AsSpan(20), docs); // PO-9 (sections start at kind byte)
            Assert.DoesNotContain(docs, d => d.Item4 > frame.Length);
        }
    }

    [Fact]
    public void Section_walker_reports_truncated_and_malformed_sections()
    {
        var doc = new byte[] { 5, 0, 0, 0, 0 };
        var good = WireFrames.Raw(0, [0, .. doc], [1, 9, 0, 0, 0, (byte)'a', 0, .. doc]);
        var it = MsgSections.Of(good, complete: true);
        Assert.True(it.MoveNext());
        Assert.Equal((0, 5), (it.Kind, it.Size));
        Assert.True(it.MoveNext());
        Assert.Equal((1, 9), (it.Kind, it.Size));
        Assert.False(it.MoveNext());
        Assert.False(it.Malformed);

        var cut = MsgSections.Of(good.AsSpan(0, good.Length - 3), complete: false);
        Assert.True(cut.MoveNext());
        Assert.True(cut.MoveNext());
        Assert.True(cut.Truncated);
        Assert.False(cut.MoveNext());

        var bad = MsgSections.Of(WireFrames.Raw(0, WireFrames.Section(7, 5)), complete: true);
        Assert.False(bad.MoveNext());
        Assert.True(bad.Malformed);
    }

    [Fact]
    public void Kind1_section_near_int_max_before_the_command_document_is_not_walked_past()
    {
        // a hello hidden behind a gigantic kind-1 section must neither throw nor be misread
        var doc = WireFrames.Msg(1, w => w.WriteInt32("hello", 1));
        var frame = WireFrames.Raw(doc.Length + 5, WireFrames.Section(1, int.MaxValue), doc[20..]);
        Assert.Equal(RequestKind.Other, RequestInspector.Classify(frame));
    }

    [Fact]
    public void Valid_hello_is_still_classified_behind_a_leading_sequence_section()
    {
        var doc = WireFrames.Msg(1, w => w.WriteInt32("hello", 1))[20..];
        byte[] seq = [1, 6, 0, 0, 0, (byte)'x', 0]; // kind 1, size 6 = 4 + "x\0", no documents
        var frame = WireFrames.Raw(20 + seq.Length + doc.Length, seq, doc);
        Assert.Equal(RequestKind.Hello, RequestInspector.Classify(frame));
    }

    // ---- topology / forwarding integration against a scripted upstream --------------------------

    private static async Task<(ProxyHost Proxy, FakeMongo Mongo)> StandaloneAsync(Action<Dictionary<string, string>>? configure = null)
    {
        var mongo = new FakeMongo(Ports.FreeRange(1));
        mongo.Start();
        var proxy = await ProxyHost.StartAsync($"mongodb://{mongo.Address}", listeners: 1, configure);
        return (proxy, mongo);
    }

    private static async Task<Socket> ConnectAsync(ProxyHost proxy)
    {
        var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        await s.ConnectAsync(new IPEndPoint(IPAddress.Loopback, proxy.BasePort));
        return s;
    }

    private static async Task<bool> ClosedAsync(Socket s, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            var buf = new byte[16];
            while (await s.ReceiveAsync(buf, SocketFlags.None, cts.Token) > 0) { }
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (SocketException) { return true; }
    }

    private static async Task<byte[][]> WaitFramesAsync(FakeMongo mongo, int count)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (mongo.Frames.Count < count)
        {
            Assert.True(DateTime.UtcNow < deadline, $"upstream saw {mongo.Frames.Count} of {count} frames");
            await Task.Delay(20);
        }
        return mongo.Frames.ToArray();
    }

    [Fact]
    public async Task Hostile_section_sizes_do_not_kill_the_connection()
    {
        var (proxy, mongo) = await StandaloneAsync();
        await using var _ = proxy;
        await using var __ = mongo;
        using var client = await ConnectAsync(proxy);

        var hostile = WireFrames.Raw(41, WireFrames.Section(1, int.MaxValue, extraBytes: 16));
        var ping = WireFrames.Ping();
        await client.SendAsync(hostile);
        await client.SendAsync(ping);

        // both frames reach the server, in order, and the connection stays open
        var seen = await WaitFramesAsync(mongo, 2);
        Assert.Equal(hostile, seen[0]);
        Assert.Equal(ping, seen[1]);
        Assert.False(await ClosedAsync(client, TimeSpan.FromMilliseconds(300)));
    }

    [Fact]
    public async Task Valid_frames_before_an_invalid_header_in_the_same_read_are_forwarded()
    {
        var (proxy, mongo) = await StandaloneAsync();
        await using var _ = proxy;
        await using var __ = mongo;
        using var client = await ConnectAsync(proxy);

        var ping = WireFrames.Ping();
        var bad = new byte[16];
        BinaryPrimitives.WriteInt32LittleEndian(bad, 3); // messageLength < 16
        await client.SendAsync(ping.Concat(bad).ToArray());

        var seen = await WaitFramesAsync(mongo, 1);
        Assert.Equal(ping, seen[0]);
        Assert.True(await ClosedAsync(client, TimeSpan.FromSeconds(5)));
        var closed = await proxy.WaitRecordsAsync(r => r.GetProperty("type").GetString() == "conn"
            && r.TryGetProperty("closeReason", out var c) && c.GetString() == "invalid_frame", 1);
        Assert.NotEmpty(closed);
    }

    [Fact]
    public async Task Hello_over_the_buffer_budget_passes_through_unmodified()
    {
        var (proxy, mongo) = await StandaloneAsync(c =>
        {
            c["limits.helloBufferBytes"] = "4096";
            c["limits.helloRewriteMaxBytes"] = "8192";
        });
        await using var _ = proxy;
        await using var __ = mongo;
        using var client = await ConnectAsync(proxy);

        // a hello with a compression array (R3 would strip it) that is bigger than the whole budget
        var hello = WireFrames.Msg(9, w =>
        {
            w.WriteInt32("hello", 1);
            w.WriteString("pad", new string('x', 5000));
            w.StartArray("compression");
            w.WriteString("0", "zlib");
            w.EndDocument();
            w.WriteString("$db", "admin");
        });
        await client.SendAsync(hello);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (mongo.HelloCount == 0)
        {
            Assert.True(DateTime.UtcNow < deadline);
            await Task.Delay(20);
        }
        var metrics = await proxy.HttpGetAsync("/metrics");
        Assert.Contains("rewrite_skipped_total{reason=\"budget\"} 1", metrics);
        Assert.Equal(0, proxy.Server.HelloBuffers.Used);
    }

    // ---- PW-1: a total outage must not tear the proxy down --------------------------------------

    [Fact]
    public async Task Total_outage_keeps_listeners_and_monitors_and_the_set_is_rediscovered()
    {
        var ports = Ports.FreeRange(2);
        var hosts = new[] { $"127.0.0.1:{ports}", $"127.0.0.1:{ports + 1}" };
        var members = new[] { new FakeMongo(ports, "rs0", () => hosts), new FakeMongo(ports + 1, "rs0", () => hosts) };
        foreach (var m in members) m.Start();

        var basePort = Ports.FreeRange(6);
        var args = new Dictionary<string, string>
        {
            ["upstream.uri"] = $"mongodb://{hosts[0]},{hosts[1]}/?replicaSet=rs0",
            ["listen.basePort"] = basePort.ToString(),
            ["listen.portRange"] = $"{basePort},{basePort + 5}",
            ["metrics.bind"] = "127.0.0.1:0",
            ["logLevel"] = "warn",
        };
        var config = ConfigLoader.Load(args.SelectMany(kv => new[] { "--" + kv.Key, kv.Value }).ToArray(), new Dictionary<string, string?>());
        await using var server = new ProxyServer(config);
        server.Topology.MonitorInterval = TimeSpan.FromMilliseconds(100);
        server.Topology.AbsentListenerTtl = TimeSpan.FromMilliseconds(300);
        await server.StartAsync();
        try
        {
            Assert.Equal(2, server.Topology.Members.Count);
            await WaitAsync(() => server.Topology.AnyMonitorSucceededWithin(TimeSpan.FromSeconds(1)));

            foreach (var m in members) m.Stop();
            await Task.Delay(1500); // many monitor iterations, every one failing, TTL long exceeded

            Assert.Equal(2, server.Topology.Members.Count);
            using (var probe = new TcpClient())
                await probe.ConnectAsync(IPAddress.Loopback, basePort); // the listener is still open

            foreach (var m in members) m.Start();
            await WaitAsync(() => server.Topology.AnyMonitorSucceededWithin(TimeSpan.FromMilliseconds(500)));
            Assert.Equal(2, server.Topology.Members.Count);
        }
        finally
        {
            foreach (var m in members) await m.DisposeAsync();
        }
    }

    [Fact]
    public async Task Replica_set_name_in_the_uri_must_match_and_direct_connection_pins_one_member()
    {
        var ports = Ports.FreeRange(2);
        var hosts = new[] { $"127.0.0.1:{ports}", $"127.0.0.1:{ports + 1}" };
        await using var a = new FakeMongo(ports, "rs0", () => hosts);
        await using var b = new FakeMongo(ports + 1, "rs0", () => hosts);
        a.Start();
        b.Start();

        var e = await Assert.ThrowsAsync<ConfigException>(() => ProxyHost.StartAsync($"mongodb://{hosts[0]}/?replicaSet=other", listeners: 2));
        Assert.Contains("replicaSet=other", e.Message);

        await using var direct = await ProxyHost.StartAsync($"mongodb://{hosts[0]}/?directConnection=true", listeners: 2);
        Assert.Single(direct.Server.Topology.Members);
        Assert.Equal(TopologyKind.Standalone, direct.Server.Topology.Kind);
    }

    // ---- PW-5: configuration errors found at construction ---------------------------------------

    [Fact]
    public void Unreadable_pem_files_are_config_errors()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var junk = Path.Combine(dir, "junk.pem");
        File.WriteAllText(junk, "-----BEGIN CERTIFICATE-----\nnot base64!!\n-----END CERTIFICATE-----\n");
        var e1 = Assert.Throws<ConfigException>(() => new DownstreamTls(new DownstreamTlsConfig { Mode = DownstreamTlsMode.Tls, CertFile = junk, KeyFile = junk }, "localhost"));
        Assert.Contains("listen.tls.certFile", e1.Message);

        var config = ConfigLoader.Load(["--upstream.uri", "mongodb://h:1/", "--upstream.tls.enabled", "true", "--upstream.tls.caFile", junk], new Dictionary<string, string?>());
        var e2 = Assert.Throws<ConfigException>(() => new UpstreamConnector(config, MongoUri.Parse("mongodb://h:1/")));
        Assert.Contains("upstream.tls.caFile", e2.Message);
    }

    [Fact]
    public async Task Port_in_use_is_a_config_error_naming_the_key()
    {
        await using var mongo = new FakeMongo(Ports.FreeRange(1));
        mongo.Start();
        var basePort = Ports.FreeRange(1);
        using var squatter = new TcpListener(IPAddress.Loopback, basePort);
        squatter.Start();
        var config = ConfigLoader.Load(["--upstream.uri", $"mongodb://{mongo.Address}", "--listen.basePort", basePort.ToString(),
            "--listen.portRange", $"{basePort},{basePort + 3}", "--metrics.bind", "127.0.0.1:0"], new Dictionary<string, string?>());
        await using var server = new ProxyServer(config);
        var e = await Assert.ThrowsAsync<ConfigException>(() => server.StartAsync());
        Assert.Contains("listen.basePort", e.Message);
    }

    [Fact]
    public void Srv_uri_with_tls_false_is_a_config_error()
    {
        var e = Assert.Throws<ConfigException>(() =>
            ConfigLoader.Load(["--upstream.uri", "mongodb+srv://c.example.net/?tls=false"], new Dictionary<string, string?>()));
        Assert.Contains("tls=false", e.Message);
    }

    // ---- PW-14 -----------------------------------------------------------------------------------

    [Fact]
    public async Task Healthcheck_reads_metrics_bind_from_the_config_file()
    {
        using var http = new TcpListener(IPAddress.Loopback, 0);
        http.Start();
        _ = Task.Run(async () =>
        {
            using var c = await http.AcceptTcpClientAsync();
            var s = c.GetStream();
            _ = await s.ReadAsync(new byte[1024]);
            await s.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"u8.ToArray());
        });
        var file = Path.Combine(Directory.CreateTempSubdirectory().FullName, "c.yaml");
        File.WriteAllText(file, $"upstream:\n  uri: \"mongodb://h:1/\"\nmetrics:\n  bind: 127.0.0.1:{((IPEndPoint)http.LocalEndpoint).Port}\n");
        Assert.Equal(0, await HealthProbe.RunAsync(["--config", file]));
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "condition not met");
            await Task.Delay(50);
        }
    }
}

/// <summary>Alone: shortens the process-wide hello buffering deadline.</summary>
[Collection(Isolated.Name)]
public sealed class HelloBufferingTests
{
    [Fact]
    public async Task A_stalled_hello_frame_is_closed_and_its_budget_released()
    {
        var old = Pump.HelloBufferTimeout;
        Pump.HelloBufferTimeout = TimeSpan.FromMilliseconds(500);
        var mongo = new FakeMongo(Ports.FreeRange(1));
        mongo.Start();
        await using var _ = mongo;
        await using var proxy = await ProxyHost.StartAsync($"mongodb://{mongo.Address}", listeners: 1);
        try
        {
            using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await client.ConnectAsync(new IPEndPoint(IPAddress.Loopback, proxy.BasePort));

            // the first 300 bytes of a 1 MiB hello, then silence: slowloris
            var hello = WireFrames.Msg(3, w =>
            {
                w.WriteInt32("hello", 1);
                w.WriteString("pad", new string('x', 400));
            });
            var prefix = hello[..300];
            BinaryPrimitives.WriteInt32LittleEndian(prefix, 1024 * 1024);
            await client.SendAsync(prefix);
            await Task.Delay(100);
            Assert.True(proxy.Server.HelloBuffers.Used >= 1024 * 1024);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var n = await client.ReceiveAsync(new byte[16], SocketFlags.None, cts.Token);
            Assert.Equal(0, n);
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (proxy.Server.HelloBuffers.Used != 0 && DateTime.UtcNow < deadline)
                await Task.Delay(20);
            Assert.Equal(0, proxy.Server.HelloBuffers.Used);
            Assert.Empty(mongo.Frames); // nothing of the stalled frame went upstream
        }
        finally
        {
            Pump.HelloBufferTimeout = old;
        }
    }
}
