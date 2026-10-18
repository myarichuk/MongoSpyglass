using System.Buffers.Binary;
using System.Diagnostics;
using MongoDB.Bson;
using Spyglass.Proxy.Config;
using Spyglass.Proxy.Forwarding;
using Spyglass.Proxy.Observation;
using Spyglass.Proxy.Topology;
using Spyglass.Proxy.Tests.Infrastructure;
using Spyglass.Proxy.Wire;

namespace Spyglass.Proxy.Tests;

/// <summary>
/// 01 §14: cost of observation per operation, isolated from sockets, mongod and the client. Driver-shaped
/// frames go through the real <see cref="CaptureTap"/> → worker channel → correlation → metrics (→ record)
/// path; process CPU time and allocated bytes over the window are divided by the operations observed.
/// Allocation is gated; CPU is printed (it depends on the machine).
/// </summary>
[Collection(Isolated.Name)]
public sealed class ObserverCostTests
{
#if DEBUG
    private static readonly bool IsDebugBuild = true;
#else
    private static readonly bool IsDebugBuild = false;
#endif

    private struct TimeSpec { public long Sec, Nsec; }

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "clock_gettime")]
    private static extern int ClockGetTime(int clock, out TimeSpec ts);

    /// <summary>CPU time of the calling thread (Linux CLOCK_THREAD_CPUTIME_ID): not wall time, which preemption inflates.</summary>
    private static double ThreadCpuMicros() => ClockGetTime(3, out var ts) == 0 ? ts.Sec * 1e6 + ts.Nsec / 1e3 : double.NaN;

    /// <summary>
    /// Zero-allocation ingest sink: HTTP/1.1 keep-alive on one blocking thread per connection, a reused
    /// buffer, a fixed 204. (HttpListener allocates per request and would be charged to the proxy.)
    /// </summary>
    private sealed class RawSink : IDisposable
    {
        private readonly System.Net.Sockets.Socket _listener = new(System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
        public long Records;
        public string Endpoint { get; }

        public RawSink()
        {
            _listener.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
            _listener.Listen();
            Endpoint = $"http://127.0.0.1:{((System.Net.IPEndPoint)_listener.LocalEndPoint!).Port}/ingest/v0";
            new Thread(() =>
            {
                try
                {
                    while (true)
                    {
                        var c = _listener.Accept();
                        new Thread(() => Serve(c)) { IsBackground = true }.Start();
                    }
                }
                catch (System.Net.Sockets.SocketException) { }
                catch (ObjectDisposedException) { }
            }) { IsBackground = true }.Start();
        }

        private void Serve(System.Net.Sockets.Socket c)
        {
            var buf = new byte[256 * 1024];
            var response = "HTTP/1.1 204 No Content\r\nContent-Length: 0\r\n\r\n"u8.ToArray();
            int have = 0;
            try
            {
                while (true)
                {
                    int headerEnd;
                    while ((headerEnd = buf.AsSpan(0, have).IndexOf("\r\n\r\n"u8)) < 0)
                    {
                        var n = c.Receive(buf, have, buf.Length - have, System.Net.Sockets.SocketFlags.None);
                        if (n == 0) return;
                        have += n;
                    }
                    var headers = buf.AsSpan(0, headerEnd);
                    var cl = headers.IndexOf("Content-Length: "u8);
                    var lenSpan = headers[(cl + "Content-Length: "u8.Length)..];
                    var eol = lenSpan.IndexOf((byte)'\r');
                    System.Buffers.Text.Utf8Parser.TryParse(eol < 0 ? lenSpan : lenSpan[..eol], out long remaining, out _);
                    var bodyStart = headerEnd + 4;
                    var inBuf = Math.Min(remaining, have - bodyStart);
                    Interlocked.Add(ref Records, buf.AsSpan(bodyStart, (int)inBuf).Count((byte)'\n'));
                    remaining -= inBuf;
                    var leftover = have - bodyStart - (int)inBuf;
                    Buffer.BlockCopy(buf, bodyStart + (int)inBuf, buf, 0, leftover);
                    have = leftover;
                    while (remaining > 0)
                    {
                        var n = c.Receive(buf, 0, (int)Math.Min(buf.Length, remaining), System.Net.Sockets.SocketFlags.None);
                        if (n == 0) return;
                        Interlocked.Add(ref Records, buf.AsSpan(0, n).Count((byte)'\n'));
                        remaining -= n;
                    }
                    c.Send(response);
                }
            }
            catch (System.Net.Sockets.SocketException) { }
            finally { c.Dispose(); }
        }

        public void Dispose() => _listener.Dispose();
    }

    private sealed class NoMap : IAddressMap
    {
        public string? Advertise(HostPort upstream) => null;
    }

    /// <summary>What the .NET driver sends for <c>Find({_id}).FirstOrDefault()</c> on an implicit session.</summary>
    private static byte[] Request(int requestId, int id, Guid session) => Frame(requestId, 0, new BsonDocument
    {
        ["find"] = "small",
        ["filter"] = new BsonDocument("_id", id),
        ["limit"] = 1,
        ["$db"] = "bench",
        ["lsid"] = new BsonDocument("id", new BsonBinaryData(session, GuidRepresentation.Standard)),
    });

    private static byte[] Reply(int requestId, int responseTo, int id) => Frame(requestId, responseTo, new BsonDocument
    {
        ["cursor"] = new BsonDocument
        {
            ["firstBatch"] = new BsonArray { new BsonDocument { ["_id"] = id, ["name"] = "user" + id, ["v"] = id % 13 } },
            ["id"] = 0L,
            ["ns"] = "bench.small",
        },
        ["ok"] = 1.0,
    });

    private static byte[] Frame(int requestId, int responseTo, BsonDocument body)
    {
        var frame = StandaloneTests.OpMsg(requestId, body);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(8), responseTo);
        return frame;
    }

    private static void Feed(IFrameTap tap, byte[] frame, RequestKind kind)
    {
        var t = Stopwatch.GetTimestamp();
        tap.Begin(FrameHeader.Read(frame), t, kind);
        tap.Body(frame.AsSpan(FrameHeader.Size));
        tap.End(t);
    }

    [Theory]
    [InlineData("headless", 4)]
    [InlineData("records", 4)]   // records built (RecordTap), not shipped
    [InlineData("export", 16)]   // records built and shipped by the real sender over HTTP: HttpClient's per-batch objects
    public async Task Observation_cost_per_op(string mode, int maxBytesPerOp)
    {
        if (IsDebugBuild)
            Assert.Skip("cost measurement needs a Release build");

        if (Clusters.DisposeAll() > 0) // earlier tests' clients would otherwise be measured too
            await Task.Delay(2000);    // their disposal finishes in the background
        using var sink = new RawSink();
        string[] args = mode == "export"
            ? ["--upstream.uri", "mongodb://127.0.0.1:1/", "--collector.endpoint", sink.Endpoint]
            : ["--upstream.uri", "mongodb://127.0.0.1:1/"];
        var config = ConfigLoader.Load(args, new Dictionary<string, string?>());
        await using var server = new ProxyServer(config);
        await using var observer = new Observer(server);
        long records = 0, recordBytes = 0;
        if (mode == "records")
            observer.RecordTap = line => // called from every worker thread
            {
                Interlocked.Increment(ref records);
                Interlocked.Add(ref recordBytes, line.Length);
            };
        observer.Start();

        var member = new Member(new HostPort("127.0.0.1", 1), 1, "127.0.0.1:1",
            new HelloRewriteContext { RewriteTopology = false, Map = new NoMap(), Self = "127.0.0.1:1", SpyglassDocument = [5, 0, 0, 0, 0] },
            server.Metrics);

        // a pool of connections, like a driver's; each has its own session (lsid) and request ids
        const int conns = 16, ids = 1000;
        var taps = new (IFrameTap Req, IFrameTap Rep)[conns];
        for (var c = 0; c < conns; c++)
        {
            var ctx = new ConnectionContext(c + 1, member, server, $"127.0.0.1:{40000 + c}");
            observer.Opened(ctx);
            taps[c] = (observer.CreateTap(ctx, isRequest: true), observer.CreateTap(ctx, isRequest: false));
        }
        // pre-built frames: the harness itself must not allocate in the window
        var reqs = new byte[conns * ids][];
        var reps = new byte[conns * ids][];
        for (var c = 0; c < conns; c++)
        {
            var session = Guid.NewGuid();
            for (var i = 0; i < ids; i++)
            {
                var rid = 1 + i * 2;
                reqs[c * ids + i] = Request(rid, i, session);
                reps[c * ids + i] = Reply(1_000_000 + rid, rid, i);
            }
        }

        double tapMicros = 0;
        long Run(int ops)
        {
            var n = 0;
            while (n < ops)
            {
                var t0 = ThreadCpuMicros();
                for (var k = 0; k < 2000 && n < ops; k++, n++)
                {
                    var c = n % conns;
                    var i = n / conns % ids;
                    Feed(taps[c].Req, reqs[c * ids + i], RequestKind.Other);
                    Feed(taps[c].Rep, reps[c * ids + i], RequestKind.Other);
                }
                tapMicros += ThreadCpuMicros() - t0;
                while (observer.Pool.ChargedBytes != 0) // let the workers drain; sleeping costs no CPU
                    Thread.Sleep(1);
            }
            return n;
        }
        var workers = Enumerable.Range(0, Math.Max(1, Environment.ProcessorCount / 2))
            .Select(i => server.Metrics.ObserverBusy.WithLabels(i.ToString())).ToArray();

        Run(100_000); // warm-up: tiering, pools, metric children, caches
        // Fill the capture pool to its bound: bursty feeding otherwise grows it whenever a burst sets a new
        // in-flight high, a bounded one-off cost a steady proxy pays once, not per op.
        var primed = new List<FrameCapture>();
        for (FrameCapture? slot; (slot = observer.Pool.Rent(primed.Count % 2 == 0)) != null;)
            primed.Add(slot);
        primed.ForEach(observer.Pool.Return);
        records = recordBytes = 0;
        tapMicros = 0;
        var busy0 = workers.Sum(w => w.Value);
        var chunks0 = observer.Sender?.ChunksAllocated ?? 0;
        using var allocs = new AllocationTests.AllocListener();
        GC.Collect();
        await Task.Delay(500); // the listener's own event dispatch (EventPipe, asynchronous) must not land in the window
        allocs.Recording = true;
        var proc = Process.GetCurrentProcess();
        var cpu0 = proc.TotalProcessorTime;
        var alloc0 = GC.GetTotalAllocatedBytes(precise: true);
        var ops = Run(400_000);
        var alloc = GC.GetTotalAllocatedBytes(precise: true) - alloc0;
        allocs.Recording = false;
        proc.Refresh();
        var cpu = (proc.TotalProcessorTime - cpu0).TotalMicroseconds;
        var busy = workers.Sum(w => w.Value) - busy0;
        var tap = tapMicros;

        var bytesPerOp = (double)alloc / ops;
        // tap: pump-side capture of both frames (feeding thread's CPU); worker: observer processing (busy counter);
        // process: everything, idle spinning included
        Console.WriteLine($"[{mode}] {ops} ops: tap {tap / ops:F2} + worker {(double)busy / ops:F2} µs/op; process {cpu / ops:F2} µs CPU/op; {bytesPerOp:F1} B/op" +
                          (records > 0 ? $", {records / (double)ops:F2} records/op, {recordBytes / (double)records:F0} B/record" : "") +
                          (bytesPerOp >= 1 ? $"; sampled (≈100 KB each): {allocs.Top(8)}" : ""));

        Assert.Equal(0, server.Metrics.ObservationDropped.WithLabels("no_slot").Value);
        if (mode == "records")
            Assert.True(records >= ops, $"{records} records for {ops} ops");
        if (mode == "export")
        {
            // At this rate (far beyond any real proxy's) the sender may hit collector.bufferBytes and drop:
            // that is I4 working. What must hold is the accounting: every record is delivered or counted.
            var dropped = server.Metrics.ObservationDropped.WithLabels("export_dropped");
            long emitted = 0;
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < deadline)
            {
                emitted = Interlocked.Read(ref sink.Records) + dropped.Value;
                if (emitted >= ops + 100_000) break;
                await Task.Delay(50);
            }
            Console.WriteLine($"[export] delivered {sink.Records}, dropped {dropped.Value}, chunks allocated in the window {observer.Sender!.ChunksAllocated - chunks0}");
            Assert.True(emitted >= ops + 100_000, $"{sink.Records} delivered + {dropped.Value} dropped < {ops + 100_000}");
        }
        Assert.True(bytesPerOp <= maxBytesPerOp, $"{bytesPerOp:F1} B/op > {maxBytesPerOp}; sampled (≈100 KB each): {allocs.Top(10)}");
    }
}
