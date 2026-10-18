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
    private readonly Counter _tapFault;
    private FrameCapture? _slot;

    public CaptureTap(ConnectionContext conn, bool isRequest, CapturePool pool, Observer observer, Counter noSlot, Counter tapFault)
    {
        _conn = conn;
        _isRequest = isRequest;
        _pool = pool;
        _observer = observer;
        _noSlot = noSlot;
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
            var slot = _pool.Rent(_isRequest);
            if (slot == null)
            {
                _noSlot.Inc();
                return;
            }
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
        if (slot == null) return;
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
        if (slot == null) return;
        _slot = null;
        slot.TLast = tLast;
        _observer.Publish(slot);
    }

    public void Abort()
    {
        var slot = _slot;
        _slot = null;
        if (slot != null)
            _pool.Return(slot);
    }
}
