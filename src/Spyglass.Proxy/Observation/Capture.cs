using System.Collections.Concurrent;
using Spyglass.Proxy.Forwarding;
using Spyglass.Proxy.Metrics;
using Spyglass.Proxy.Wire;

namespace Spyglass.Proxy.Observation;

/// <summary>One observed frame (01 §4). Pooled; never allocated per frame in steady state.</summary>
internal sealed class FrameCapture
{
    public FrameCapture(int bufferSize, bool isRequest)
    {
        Buffer = new byte[bufferSize];
        IsRequest = isRequest;
        Charge = bufferSize + Overhead;
    }

    /// <summary>Approximate fixed size of a slot excluding its buffer, charged against the queue bound.</summary>
    public const int Overhead = 1024;

    public readonly bool IsRequest;
    public readonly byte[] Buffer;
    public readonly long Charge;
    public readonly ReplyFacts Facts = new();
    public readonly ReplyScanner Scanner = new();
    public readonly SequenceScanner Sequences = new();

    public ConnectionContext Conn = null!;
    public int RequestId, ResponseTo, OpCode, FrameLength;
    public MsgFlags Flags;
    public RequestKind Kind;
    public long TFirst, TLast;
    public int CapturedLength;  // body bytes copied (excludes header)
    public bool Truncated;
    public byte FlagsHave;      // bytes of OP_MSG flags seen so far
    public readonly byte[] FlagBytes = new byte[4];
}

/// <summary>
/// Work for an observer worker: a captured frame or a connection lifecycle event, in per-connection order.
/// </summary>
internal readonly record struct ObserverItem(FrameCapture? Frame, ConnectionContext Conn, ObserverEvent Event, CloseReason Reason = default, string? Detail = null);

internal enum ObserverEvent : byte { Frame, Opened, Closed }

/// <summary>
/// Slot pool bounded by <c>limits.observeQueueBytes</c> (I4): every slot in use — being filled by a pump,
/// queued, or being processed — is charged its buffer size. When the bound is reached new frames are
/// not observed. Forwarding never waits for a slot.
/// </summary>
internal sealed class CapturePool
{
    private readonly ConcurrentQueue<FrameCapture> _requests = new();
    private readonly ConcurrentQueue<FrameCapture> _replies = new();
    private readonly int _requestBytes, _replyBytes;
    private readonly long _bound;
    private long _charged;
    private readonly Gauge _gauge;

    public CapturePool(int requestBytes, int replyBytes, long bound, Gauge gauge)
    {
        _requestBytes = requestBytes;
        _replyBytes = replyBytes;
        _bound = bound;
        _gauge = gauge;
    }

    public long ChargedBytes => Volatile.Read(ref _charged);

    public FrameCapture? Rent(bool isRequest)
    {
        var charge = (isRequest ? _requestBytes : _replyBytes) + FrameCapture.Overhead;
        if (Interlocked.Add(ref _charged, charge) > _bound)
        {
            Interlocked.Add(ref _charged, -charge);
            return null;
        }
        _gauge.Add(charge);
        var q = isRequest ? _requests : _replies;
        return q.TryDequeue(out var slot) ? slot : new FrameCapture(isRequest ? _requestBytes : _replyBytes, isRequest);
    }

    public void Return(FrameCapture slot)
    {
        slot.Conn = null!;
        Interlocked.Add(ref _charged, -slot.Charge);
        _gauge.Add(-slot.Charge);
        (slot.IsRequest ? _requests : _replies).Enqueue(slot);
    }
}
