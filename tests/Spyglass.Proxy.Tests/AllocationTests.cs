using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics.Tracing;
using System.Net;
using System.Net.Sockets;
using MongoDB.Bson;
using Spyglass.Proxy.Tests.Infrastructure;

namespace Spyglass.Proxy.Tests;

/// <summary>
/// 01 §14: steady-state managed allocation on the forwarding path is 0 B/frame. The client here is a
/// zero-allocation raw socket loop on its own threads, so (almost) every byte allocated in the process
/// during the window is the proxy's. AllocationTick sampling names the types when the gate fails.
/// </summary>
[Collection("console")]
public sealed class AllocationTests(StandaloneFixture f) : IClassFixture<StandaloneFixture>
{
#if DEBUG
    private static readonly bool IsDebugBuild = true;
#else
    private static readonly bool IsDebugBuild = false;
#endif

    private sealed class AllocListener : EventListener
    {
        public readonly ConcurrentDictionary<string, long> Types = new();
        public volatile bool Recording;

        protected override void OnEventSourceCreated(EventSource source)
        {
            if (source.Name == "Microsoft-Windows-DotNETRuntime")
                EnableEvents(source, EventLevel.Verbose, (EventKeywords)0x1 /* GC */);
        }

        protected override void OnEventWritten(EventWrittenEventArgs e)
        {
            if (!Recording || e.EventName != "GCAllocationTick_V4" || e.Payload == null) return;
            var type = e.Payload[e.PayloadNames!.IndexOf("TypeName")] as string ?? "?";
            Types.AddOrUpdate(type, 1, (_, n) => n + 1);
        }
    }

    [Theory]
    [InlineData("bare", "0")]
    [InlineData("observe", "67108864")]
    public async Task Forwarding_path_allocation_per_frame(string mode, string observeQueueBytes)
    {
        if (IsDebugBuild)
            Assert.Skip("allocation gate needs a Release build: Debug compiles async state machines as heap classes");
        await using var proxy = await ProxyHost.StartAsync(f.Mongo.DirectUri, listeners: 1, configure: a => a["limits.observeQueueBytes"] = observeQueueBytes);
        proxy.Server.Observation!.RecordTap = null; // the test host's JSON parsing would count as allocation

        const int clients = 8;
        var frame = StandaloneTests.OpMsg(1, new BsonDocument { ["find"] = "alloc", ["filter"] = new BsonDocument("_id", 1), ["limit"] = 1, ["$db"] = "t" });
        var sockets = new Socket[clients];
        for (var i = 0; i < clients; i++)
        {
            sockets[i] = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            sockets[i].Connect(IPAddress.Loopback, proxy.BasePort); // sync: an async connect leaves the socket non-blocking, and sync Receive then allocates
        }

        var stop = false;
        long roundTrips = 0;
        void Loop(Socket s)
        {
            var buf = new byte[64 * 1024];
            while (!Volatile.Read(ref stop))
            {
                s.Send(frame);
                var got = 0;
                var need = 16;
                while (got < need)
                {
                    got += s.Receive(buf, got, buf.Length - got, SocketFlags.None);
                    if (got >= 4) need = BinaryPrimitives.ReadInt32LittleEndian(buf);
                }
                Interlocked.Increment(ref roundTrips);
            }
        }
        var threads = sockets.Select(s => new Thread(() => Loop(s)) { IsBackground = true }).ToList();
        threads.ForEach(t => t.Start());

        await Task.Delay(1500); // warm-up: tiering, pools
        using var listener = new AllocListener();
        GC.Collect();
        var before = GC.GetTotalAllocatedBytes(precise: true);
        var rtBefore = Interlocked.Read(ref roundTrips);
        listener.Recording = true;
        await Task.Delay(3000);
        listener.Recording = false;
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
        var frames = 2 * (Interlocked.Read(ref roundTrips) - rtBefore);
        Volatile.Write(ref stop, true);
        threads.ForEach(t => t.Join());
        foreach (var s in sockets) s.Dispose();

        var perFrame = (double)allocated / frames;
        var top = string.Join(", ", listener.Types.OrderByDescending(kv => kv.Value).Take(8).Select(kv => $"{kv.Key}×{kv.Value}"));
        Console.WriteLine($"[{mode}] {frames} frames, {allocated} B, {perFrame:F2} B/frame; sampled (≈100 KB each): {top}");

        // bare: forwarding only. observe: the tap + observer, whose per-op allocations are budgeted separately (01 §14).
        if (mode == "bare")
            Assert.True(perFrame < 1.0, $"{perFrame:F2} B/frame; {top}");
    }
}
