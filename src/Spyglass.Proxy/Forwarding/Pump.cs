using System.Buffers;
using System.Diagnostics;
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

    private int _held;          // bytes at _buf[0.._held) not yet forwarded (an incomplete header / inspect window)
    private State _state = State.Header;
    private int _remaining;     // body bytes of the current Pass frame still to come
    private FrameHeader _header;
    private byte[]? _frame;     // Buffer state: whole frame
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
                    n = await _src.ReadAsync(_buf.AsMemory(_held), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return PumpEnd.Cancelled;
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
                _bytes.Add(n);
                if (_isRequest) Interlocked.Add(ref _conn.BytesFromClient, n);
                else Interlocked.Add(ref _conn.BytesFromServer, n);

                var r = await ProcessAsync(_held + n, now, ct).ConfigureAwait(false);
                if (r != null)
                    return r.Value;
            }
        }
        finally
        {
            _tap.Abort();
            ArrayPool<byte>.Shared.Return(_buf);
            if (_frame != null)
            {
                ArrayPool<byte>.Shared.Return(_frame);
                _frame = null;
            }
        }
    }

    /// <summary>Processes <c>_buf[0..total)</c>. Returns null to continue reading.</summary>
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
                        // Forward nothing more: we can no longer frame this stream (01 §3.2 FAIL_CLOSED).
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
                        buffer = kind == RequestKind.Hello && _config.Compression == CompressionMode.Strip
                                 && h.MessageLength <= _config.Limits.HelloRewriteMaxBytes;
                    }
                    else
                    {
                        var isHelloReply = _conn.TakePendingHello(h.ResponseTo);
                        if (isHelloReply)
                        {
                            kind = RequestKind.Hello;
                            buffer = h.MessageLength <= _config.Limits.HelloRewriteMaxBytes;
                            if (!buffer)
                                _conn.Server.Metrics.RewritesSkipped.WithLabels("too_large").Inc();
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
                        _frame = ArrayPool<byte>.Shared.Rent(h.MessageLength);
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
                    var n = Math.Min(_header.MessageLength - _frameFilled, total - p);
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
            _addedLatency.Observe(Stopwatch.GetElapsedTime(now).TotalSeconds);
        return null;

    Hold:
        // [p, total) is the start of a frame whose header (or inspect window) is incomplete.
        if (p > fwdStart && !await WriteAsync(fwdStart, p - fwdStart, ct).ConfigureAwait(false))
            return PumpEnd.DestinationFailed;
        _held = total - p;
        if (p > 0)
            Buffer.BlockCopy(_buf, p, _buf, 0, _held);
        if (completedFrame)
            _addedLatency.Observe(Stopwatch.GetElapsedTime(now).TotalSeconds);
        return null;
    }

    private void EndFrame(long now)
    {
        _tap.End(now);
        _frames.Inc();
        _state = State.Header;
    }

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
            _frames.Inc();

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
        var result = HelloRewriter.RewriteReply(frame, member.RewriteContext, facts, out var failure);
        if (result == null)
        {
            _conn.Server.Metrics.RewritesSkipped.WithLabels(failure.ToString().ToLowerInvariant()).Inc();
            HelloRewriter.ExtractFacts(frame, facts);
        }
        else
        {
            _conn.Server.Metrics.Rewrites.WithLabels(member.RewriteContext.RewriteTopology ? "topology" : "discovery").Inc();
        }

        if (facts.MeMismatch != null)
            Log.Error($"listener {member.Advertised}: {facts.MeMismatch}");

        // Streaming hello / exhaust: the next reply in the stream answers this reply's requestId (01 §3.3).
        if (_header.OpCode == OpCodes.Msg && frame.Length >= 20 &&
            ((MsgFlags)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(frame[16..]) & MsgFlags.MoreToCome) != 0)
            _conn.AddPendingHello(_header.RequestId);

        _conn.OnHelloReply(facts);
        return result;
    }

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
