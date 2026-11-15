using Spyglass.Proxy.Forwarding;
using Spyglass.Proxy.Metrics;
using Spyglass.Proxy.Wire;

namespace Spyglass.Proxy.Observation;

/// <summary>
/// The per-direction tap (01 §4): copies at most the capture limit, feeds the streaming scanners,
/// publishes the slot to the observer. Never throws, never blocks, no allocation in steady state.
/// </summary>
internal sealed class CaptureTap : IFrameTap
{
    private readonly ConnectionContext _conn;
    private readonly bool _isRequest;
    private readonly CapturePool _pool;
    private readonly Observer _observer;
    private readonly Counter _noSlot;
    private readonly Counter _queueFull;
    private readonly Counter _tapFault;
    private FrameCapture? _slot;

    /// <summary>01 §4's per-connection ring, per direction: a connection can't hold more than this many slots at once.</summary>
    internal const int RingSlots = 512;
    private readonly int[] _inflight = new int[1];

    // A reply that got no slot still tells the observer which request it answers (and whether more replies follow),
    // so the pending entry is released instead of lingering until the connection closes.
    private bool _lost;
    private int _lostRequestId, _lostResponseTo, _lostOpCode, _lostFlagsHave;
    private readonly byte[] _lostFlags = new byte[4];

    public CaptureTap(ConnectionContext conn, bool isRequest, CapturePool pool, Observer observer, Counter noSlot, Counter queueFull, Counter tapFault)
    {
        _conn = conn;
        _isRequest = isRequest;
        _pool = pool;
        _observer = observer;
        _noSlot = noSlot;
        _queueFull = queueFull;
        _tapFault = tapFault;
    }

    public void Begin(in FrameHeader header, long tFirst, RequestKind kind)
    {
        try
        {
            if (_slot != null) // previous frame never ended (cannot happen with a correct pump)
            {
                _pool.Return(_slot);
                _slot = null;
            }
            _lost = false;
            FrameCapture? slot = null;
            if (Volatile.Read(ref _inflight[0]) >= RingSlots)
                _noSlot.Inc();
            else if ((slot = _pool.Rent(_isRequest)) == null)
                _queueFull.Inc();
            if (slot == null)
            {
                if (!_isRequest)
                {
                    _lost = true;
                    _lostRequestId = header.RequestId;
                    _lostResponseTo = header.ResponseTo;
                    _lostOpCode = header.OpCode;
                    _lostFlagsHave = header.OpCode == OpCodes.Msg ? 0 : 4;
                }
                return;
            }
            Interlocked.Increment(ref _inflight[0]);
            slot.Inflight = _inflight;
            slot.Conn = _conn;
            slot.RequestId = header.RequestId;
            slot.ResponseTo = header.ResponseTo;
            slot.OpCode = header.OpCode;
            slot.FrameLength = header.MessageLength;
            slot.Kind = kind;
            slot.TFirst = tFirst;
            slot.TLast = tFirst;
            slot.CapturedLength = 0;
            slot.Truncated = false;
            slot.Flags = MsgFlags.None;
            slot.FlagsHave = header.OpCode == OpCodes.Msg ? (byte)0 : (byte)4;
            var body = header.MessageLength - FrameHeader.Size;
            if (_isRequest)
                slot.Sequences.Begin(header.OpCode, body);
            else
                slot.Scanner.Begin(header.OpCode, slot.Facts);
            _slot = slot;
        }
        catch
        {
            _tapFault.Inc();
            _slot = null;
        }
    }

    public void Body(ReadOnlySpan<byte> chunk)
    {
        var slot = _slot;
        if (slot == null)
        {
            if (_lost && _lostFlagsHave < 4)
            {
                var n = Math.Min(4 - _lostFlagsHave, chunk.Length);
                chunk[..n].CopyTo(_lostFlags.AsSpan(_lostFlagsHave));
                _lostFlagsHave += n;
            }
            return;
        }
        try
        {
            if (slot.FlagsHave < 4)
            {
                var n = Math.Min(4 - slot.FlagsHave, chunk.Length);
                chunk[..n].CopyTo(slot.FlagBytes.AsSpan(slot.FlagsHave));
                slot.FlagsHave += (byte)n;
                if (slot.FlagsHave == 4)
                    slot.Flags = (MsgFlags)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(slot.FlagBytes);
            }

            // Requests are copied up to the capture limit; replies are only scanned, except OP_COMPRESSED ones under
            // compression=observe, whose head the observer inflates (01 §8, §10).
            if (_isRequest || slot.OpCode == OpCodes.Compressed)
            {
                var room = slot.Buffer.Length - slot.CapturedLength;
                if (room > 0)
                {
                    var copy = Math.Min(room, chunk.Length);
                    chunk[..copy].CopyTo(slot.Buffer.AsSpan(slot.CapturedLength));
                    slot.CapturedLength += copy;
                    if (copy < chunk.Length) slot.Truncated = true;
                }
                else if (chunk.Length > 0)
                    slot.Truncated = true;
            }

            if (_isRequest)
                slot.Sequences.Feed(chunk);
            else
                slot.Scanner.Feed(chunk);
        }
        catch
        {
            _tapFault.Inc();
            _pool.Return(slot);
            _slot = null;
        }
    }

    public void End(long tLast)
    {
        var slot = _slot;
        if (slot == null)
        {
            if (_lost)
            {
                _lost = false;
                var flags = _lostFlagsHave == 4 ? (MsgFlags)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(_lostFlags) : MsgFlags.None;
                _observer.PublishUnobservedReply(_conn, _lostResponseTo, _lostRequestId, _lostOpCode, flags);
            }
            return;
        }
        _slot = null;
        slot.TLast = tLast;
        _observer.Publish(slot);
    }

    public void Abort()
    {
        _lost = false;
        var slot = _slot;
        _slot = null;
        if (slot != null)
            _pool.Return(slot);
    }
}
