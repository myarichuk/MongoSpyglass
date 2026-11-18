using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Net.Sockets;
using Spyglass.Proxy.Config;
using Spyglass.Proxy.Diagnostics;
using Spyglass.Proxy.Metrics;
using Spyglass.Proxy.Wire;

namespace Spyglass.Proxy.Forwarding;

internal enum PumpEnd
{
    /// <summary>The source sent FIN.</summary>
    SourceClosed,
    /// <summary>Reading the source failed (RST, TLS error).</summary>
    SourceFailed,
    /// <summary>Writing to the destination failed.</summary>
    DestinationFailed,
    InvalidFrame,
    /// <summary>A hello frame being buffered for rewriting did not complete in time (01 §13); not an invalid frame.</summary>
    HelloTimeout,
    /// <summary>The frame processing code threw; the connection fails closed (01 §3.2).</summary>
    ProcessingFault,
    Cancelled,
}

/// <summary>
/// One direction of a proxied connection (01 §3). Cut-through: every read chunk is written to the
/// destination before the next read, except
/// <list type="bullet">
/// <item>the first ≤ 16 bytes of a frame (requests: ≤ <see cref="RequestInspector.InspectBytes"/>), held only
/// until the header (command name) is complete — the peer cannot act on a partial header anyway;</item>
/// <item>hello-family frames that get rewritten (R1–R3), buffered whole up to <c>helloRewriteMaxBytes</c>.</item>
/// </list>
/// No allocation per frame on the pass-through path.
/// </summary>
internal sealed class Pump
{
    private enum State { Header, Pass, Buffer }

    /// <summary>A client hello is a few hundred bytes (&lt; 4 KB in practice); a request frame declaring more than this is passed through, not buffered (01 §13).</summary>
    internal const int RequestHelloMaxBytes = 64 * 1024;
    /// <summary>First budget charge for a request being buffered; further charges follow the bytes actually received.</summary>
    private const int RequestReserveStep = 4096;

    private readonly Stream _src;
    private readonly Stream _dst;
    private readonly bool _isRequest;
    private readonly ConnectionContext _conn;
    private readonly IFrameTap _tap;
    private readonly ProxyConfig _config;
    private readonly Counter _bytes;
    private readonly Counter _frames;
    private readonly Histogram _addedLatency;
    private readonly byte[] _buf;

    // Hot-path statistics are pump-local and flushed to the shared (per-listener) metrics at most every
    // 250 ms and at exit: no interlocked operation, and no shared cache line, per read.
    private static readonly long FlushTicks = Stopwatch.Frequency / 4;
    private long _unflushedBytes, _unflushedFrames, _lastFlush, _totalBytes;
    private readonly long[] _latencyCounts;
    private double _latencySum;

    private int _held;          // bytes at _buf[0.._held) not yet forwarded (an incomplete header / inspect window)
    private State _state = State.Header;
    private int _remaining;     // body bytes of the current Pass frame still to come
    private FrameHeader _header;
    private byte[]? _frame;     // Buffer state: whole frame
    private int _frameReserved; // bytes of the hello budget held for _frame (replies: the whole frame; requests: grows with the bytes received, FP-1)
    private CancellationTokenSource? _bufferCts; // deadline of an incomplete buffered frame
    private int _frameFilled;
    private RequestKind _kind;

    public string? InvalidFrameHeaderHex { get; private set; }
    public Exception? Failure { get; private set; }

    public Pump(Stream src, Stream dst, bool isRequest, ConnectionContext conn, IFrameTap tap, ProxyConfig config,
        Counter bytes, Counter frames, Histogram addedLatency)
    {
        _src = src;
        _dst = dst;
        _isRequest = isRequest;
        _conn = conn;
        _tap = tap;
        _config = config;
        _bytes = bytes;
        _frames = frames;
        _addedLatency = addedLatency;
        _buf = ArrayPool<byte>.Shared.Rent(config.Limits.ReadBufferBytes);
        _latencyCounts = new long[addedLatency.BucketCount];
        _lastFlush = Stopwatch.GetTimestamp();
    }

    private void ObserveLatency(long since)
    {
        var seconds = Stopwatch.GetElapsedTime(since).TotalSeconds;
        _latencyCounts[_addedLatency.BucketIndex(seconds)]++;
        _latencySum += seconds;
    }

    private void FlushStats(long now)
    {
        _lastFlush = now;
        if (_unflushedBytes != 0) { _bytes.Add(_unflushedBytes); _unflushedBytes = 0; }
        if (_unflushedFrames != 0) { _frames.Add(_unflushedFrames); _unflushedFrames = 0; }
        if (_latencySum != 0 || _latencyCounts.AsSpan().ContainsAnyExcept(0))
        {
            _addedLatency.Merge(_latencyCounts, _latencySum);
            _latencySum = 0;
        }
        // single writer per field: the observer and close path only read it
        if (_isRequest) Volatile.Write(ref _conn.BytesFromClient, _totalBytes);
        else Volatile.Write(ref _conn.BytesFromServer, _totalBytes);
    }

    public async Task<PumpEnd> RunAsync(CancellationToken ct)
    {
        try
        {
            while (true)
            {
                int n;
                try
                {
                    n = await _src.ReadAsync(_buf.AsMemory(_held), _bufferCts?.Token ?? ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return PumpEnd.Cancelled;
                }
                catch (OperationCanceledException) when (_bufferCts != null)
                {
                    Failure = new TimeoutException($"hello frame not completed within {_config.Limits.HelloBufferTimeoutMs / 1000.0:0.###} s");
                    return PumpEnd.HelloTimeout;
                }
                catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException)
                {
                    Failure = e;
                    return PumpEnd.SourceFailed;
                }

                if (n == 0)
                {
                    // FIN. Forward whatever partial header we were holding: transparency over tidiness.
                    if (_held > 0 && !await WriteAsync(0, _held, ct).ConfigureAwait(false))
                        return PumpEnd.DestinationFailed;
                    _held = 0;
                    return PumpEnd.SourceClosed;
                }

                var now = Stopwatch.GetTimestamp();
                _unflushedBytes += n;
                _totalBytes += n;

                PumpEnd? r;
                try
                {
                    r = await ProcessAsync(_held + n, now, ct).ConfigureAwait(false);
                }
                catch (Exception e) when (e is not (OperationCanceledException or IOException or SocketException or ObjectDisposedException))
                {
                    // A parser fault must never escape the pump: fail the connection closed like an invalid frame (01 §3.2).
                    Log.Warn($"conn {_conn.ConnSeq}: frame processing fault: {e.GetType().Name}: {e.Message}");
                    Failure = e;
                    return PumpEnd.ProcessingFault;
                }
                if (r != null)
                    return r.Value;
                if (_state == State.Buffer && _bufferCts == null)
                {
                    _bufferCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    _bufferCts.CancelAfter(_config.Limits.HelloBufferTimeoutMs);
                }
                if (now - _lastFlush > FlushTicks)
                    FlushStats(now);
            }
        }
        finally
        {
            FlushStats(Stopwatch.GetTimestamp());
            _tap.Abort();
            ArrayPool<byte>.Shared.Return(_buf);
            if (_frame != null)
            {
                ArrayPool<byte>.Shared.Return(_frame);
                _frame = null;
            }
            ReleaseFrameBudget();
        }
    }

    private ByteBudget FrameBudget => _isRequest ? _conn.Server.HelloRequestBuffers : _conn.Server.HelloBuffers;

    private void ReleaseFrameBudget()
    {
        if (_frameReserved != 0)
        {
            FrameBudget.Release(_frameReserved);
            _frameReserved = 0;
        }
        if (_bufferCts != null)
        {
            _bufferCts.Dispose();
            _bufferCts = null;
        }
    }

    /// <summary>Processes <c>_buf[0..total)</c>. Returns null to continue reading.</summary>
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<PumpEnd?> ProcessAsync(int total, long now, CancellationToken ct)
    {
        var p = 0;
        var fwdStart = 0;
        var completedFrame = false;

        while (p < total)
        {
            switch (_state)
            {
                case State.Header:
                {
                    var avail = total - p;
                    if (avail < FrameHeader.Size)
                        goto Hold;

                    var h = FrameHeader.Read(_buf.AsSpan(p, FrameHeader.Size));
                    if (h.MessageLength < FrameHeader.Size || h.MessageLength > _conn.MaxFrame)
                    {
                        InvalidFrameHeaderHex = Convert.ToHexString(_buf, p, FrameHeader.Size);
                        // Forward nothing from the bad header on: we can no longer frame this stream (01 §3.2 FAIL_CLOSED).
                        // The complete frames before it in this read were already taken by the tap: deliver them.
                        if (p > fwdStart && !await WriteAsync(fwdStart, p - fwdStart, ct).ConfigureAwait(false))
                            return PumpEnd.DestinationFailed;
                        return PumpEnd.InvalidFrame;
                    }

                    var kind = RequestKind.Other;
                    bool buffer;
                    if (_isRequest)
                    {
                        var need = Math.Min(h.MessageLength, RequestInspector.InspectBytes);
                        if (avail < need)
                            goto Hold;
                        kind = RequestInspector.Classify(_buf.AsSpan(p, need));
                        if (kind == RequestKind.Hello)
                            _conn.AddPendingHello(h.RequestId); // before any byte of the request is forwarded
                        buffer = kind == RequestKind.Hello && _config.Compression == CompressionMode.Strip;
                        if (buffer && !ReserveFrame(h.MessageLength))
                            buffer = false;
                    }
                    else
                    {
                        // a pending hello's reply needs its flags (bytes 16..19) to register a moreToCome stream
                        if (_conn.HasPendingHello && avail < Math.Min(h.MessageLength, 20))
                            goto Hold;
                        var isHelloReply = _conn.TakePendingHello(h.ResponseTo);
                        if (isHelloReply)
                        {
                            kind = RequestKind.Hello;
                            buffer = ReserveFrame(h.MessageLength);
                            if (!buffer)
                                RegisterStream(h, _buf.AsSpan(p, Math.Min(h.MessageLength, 20)));
                        }
                        else buffer = false;
                    }

                    _header = h;
                    _kind = kind;
                    _tap.Begin(h, now, kind);

                    if (buffer)
                    {
                        // Flush pass-through bytes that precede this frame, then accumulate it whole.
                        if (p > fwdStart && !await WriteAsync(fwdStart, p - fwdStart, ct).ConfigureAwait(false))
                            return PumpEnd.DestinationFailed;
                        _frame = ArrayPool<byte>.Shared.Rent(_frameReserved);
                        _frameFilled = 0;
                        _state = State.Buffer;
                        fwdStart = p; // consumed by Buffer below; reset after copying
                    }
                    else
                    {
                        p += FrameHeader.Size;
                        _remaining = h.MessageLength - FrameHeader.Size;
                        _state = State.Pass;
                        if (_remaining == 0)
                        {
                            EndFrame(now);
                            completedFrame = true;
                        }
                    }
                    break;
                }

                case State.Pass:
                {
                    var n = Math.Min(_remaining, total - p);
                    _tap.Body(_buf.AsSpan(p, n));
                    p += n;
                    _remaining -= n;
                    if (_remaining == 0)
                    {
                        EndFrame(now);
                        completedFrame = true;
                    }
                    break;
                }

                case State.Buffer:
                {
                    var room = Math.Min(_header.MessageLength, _frameReserved) - _frameFilled;
                    if (room == 0)
                    {
                        // more bytes have arrived than the request budget was charged for: charge them now, or stop buffering
                        if (GrowFrame())
                            break;
                        if (!await AbandonBufferingAsync(ct).ConfigureAwait(false))
                            return PumpEnd.DestinationFailed;
                        fwdStart = p;
                        break;
                    }
                    var n = Math.Min(room, total - p);
                    Buffer.BlockCopy(_buf, p, _frame!, _frameFilled, n);
                    _frameFilled += n;
                    p += n;
                    fwdStart = p;
                    if (_frameFilled == _header.MessageLength)
                    {
                        if (!await ForwardBufferedAsync(now, ct).ConfigureAwait(false))
                            return PumpEnd.DestinationFailed;
                        completedFrame = true;
                    }
                    break;
                }
            }
        }

        // Everything consumed: one write for the pass-through range.
        if (total > fwdStart && !await WriteAsync(fwdStart, total - fwdStart, ct).ConfigureAwait(false))
            return PumpEnd.DestinationFailed;
        _held = 0;
        if (completedFrame)
            ObserveLatency(now);
        return null;

    Hold:
        // [p, total) is the start of a frame whose header (or inspect window) is incomplete.
        if (p > fwdStart && !await WriteAsync(fwdStart, p - fwdStart, ct).ConfigureAwait(false))
            return PumpEnd.DestinationFailed;
        _held = total - p;
        if (p > 0)
            Buffer.BlockCopy(_buf, p, _buf, 0, _held);
        if (completedFrame)
            ObserveLatency(now);
        return null;
    }

    /// <summary>
    /// Reserves the hello-buffer budget for a frame about to be buffered; counts the skip when it can't be. Replies (from
    /// the trusted server) reserve the whole frame from their own budget; requests (any client, unauthenticated) draw on a
    /// separate budget, are capped at <see cref="RequestHelloMaxBytes"/> and are charged by the bytes actually received,
    /// so declared lengths cannot be used to starve reply rewriting (FP-1, 01 §13).
    /// </summary>
    private bool ReserveFrame(int length)
    {
        if (length > _config.Limits.HelloRewriteMaxBytes || (_isRequest && length > RequestHelloMaxBytes))
        {
            _conn.Server.Metrics.RewritesSkipped.WithLabels("too_large").Inc();
            return false;
        }
        var charge = _isRequest ? Math.Min(length, RequestReserveStep) : length;
        if (!FrameBudget.TryAcquire(charge))
        {
            _conn.Server.Metrics.RewritesSkipped.WithLabels("budget").Inc();
            return false;
        }
        _frameReserved = charge;
        return true;
    }

    /// <summary>Request side: doubles the charge (capped at the declared length) and the buffer. False when the budget refuses.</summary>
    private bool GrowFrame()
    {
        var want = Math.Min(_header.MessageLength, _frameReserved * 2);
        if (!FrameBudget.TryAcquire(want - _frameReserved))
            return false;
        var bigger = ArrayPool<byte>.Shared.Rent(want);
        Buffer.BlockCopy(_frame!, 0, bigger, 0, _frameFilled);
        ArrayPool<byte>.Shared.Return(_frame!);
        _frame = bigger;
        _frameReserved = want;
        return true;
    }

    /// <summary>The budget ran out mid-frame: forward what was buffered and carry on as a pass-through frame (header and inspect window are always inside the first charge).</summary>
    private async ValueTask<bool> AbandonBufferingAsync(CancellationToken ct)
    {
        _conn.Server.Metrics.RewritesSkipped.WithLabels("budget").Inc();
        var frame = _frame!;
        var filled = _frameFilled;
        _frame = null;
        try
        {
            _tap.Body(frame.AsSpan(FrameHeader.Size, filled - FrameHeader.Size));
            await _dst.WriteAsync(frame.AsMemory(0, filled), ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
        {
            Failure = e;
            return false;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(frame);
            ReleaseFrameBudget();
        }
        _remaining = _header.MessageLength - filled;
        _state = State.Pass;
        return true;
    }

    /// <summary>Streaming hello / exhaust (01 §3.3): the next reply in the stream answers this reply's requestId.</summary>
    private void RegisterStream(in FrameHeader h, ReadOnlySpan<byte> prefix)
    {
        if (h.OpCode == OpCodes.Msg && prefix.Length >= 20 &&
            ((MsgFlags)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(prefix[16..]) & MsgFlags.MoreToCome) != 0)
            _conn.AddPendingHello(h.RequestId);
    }

    private void EndFrame(long now)
    {
        _tap.End(now);
        _unflushedFrames++;
        _state = State.Header;
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<bool> ForwardBufferedAsync(long now, CancellationToken ct)
    {
        var frame = _frame!;
        var len = _header.MessageLength;
        _frame = null;
        try
        {
            // The tap sees the frame as the source sent it.
            _tap.Body(frame.AsSpan(FrameHeader.Size, len - FrameHeader.Size));

            byte[]? rewritten = null;
            try
            {
                rewritten = _isRequest ? RewriteRequest(frame.AsSpan(0, len)) : RewriteReply(frame.AsSpan(0, len));
            }
            catch (Exception e)
            {
                // Rewriting must never take the connection down (01 §3.3).
                _conn.Server.Metrics.RewritesSkipped.WithLabels("exception").Inc();
                Log.Warn($"conn {_conn.ConnSeq}: hello rewrite failed, forwarding original: {e.GetType().Name}: {e.Message}");
            }

            _state = State.Header;
            _tap.End(now);
            _unflushedFrames++;

            try
            {
                if (rewritten != null)
                    await _dst.WriteAsync(rewritten, ct).ConfigureAwait(false);
                else
                    await _dst.WriteAsync(frame.AsMemory(0, len), ct).ConfigureAwait(false);
                return true;
            }
            catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
            {
                Failure = e;
                return false;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(frame);
            ReleaseFrameBudget();
        }
    }

    private byte[]? RewriteRequest(ReadOnlySpan<byte> frame)
    {
        var result = HelloRewriter.StripCompression(frame, out var failure);
        if (result != null)
            _conn.Server.Metrics.Rewrites.WithLabels("strip_compression").Inc();
        else if (failure != RewriteFailure.None)
            _conn.Server.Metrics.RewritesSkipped.WithLabels(failure.ToString().ToLowerInvariant()).Inc();
        return result;
    }

    private byte[]? RewriteReply(ReadOnlySpan<byte> frame)
    {
        var facts = new HelloReplyFacts();
        var member = _conn.Member;
        byte[]? result = null;
        try
        {
            result = HelloRewriter.RewriteReply(frame, member.RewriteContext, facts, out var failure);
            if (result == null)
            {
                _conn.Server.Metrics.RewritesSkipped.WithLabels(failure.ToString().ToLowerInvariant()).Inc();
                HelloRewriter.ExtractFacts(frame, facts);
                // 01 §5.1 step 5: members seen in a forwarded reply get a listener even when the reply isn't rewritten
                if (member.RewriteContext.RewriteTopology)
                    foreach (var hp in facts.Members)
                        member.RewriteContext.Map.Advertise(hp);
            }
            else
            {
                _conn.Server.Metrics.Rewrites.WithLabels(member.RewriteContext.RewriteTopology ? "topology" : "discovery").Inc();
            }
        }
        catch (Exception e)
        {
            // The bookkeeping below must still happen, or later streaming replies go out un-rewritten.
            _conn.Server.Metrics.RewritesSkipped.WithLabels("exception").Inc();
            Log.Warn($"conn {_conn.ConnSeq}: hello rewrite failed, forwarding original: {e.GetType().Name}: {e.Message}");
            result = null;
        }

        if (facts.MeMismatch != null)
            Log.Error($"listener {member.Advertised}: {facts.MeMismatch}");

        RegisterStream(_header, frame.Length >= 20 ? frame[..20] : frame);
        _conn.OnHelloReply(facts);
        return result;
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<bool> WriteAsync(int offset, int count, CancellationToken ct)
    {
        try
        {
            await _dst.WriteAsync(_buf.AsMemory(offset, count), ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
        {
            Failure = e;
            return false;
        }
    }
}
