using System.Buffers.Binary;

namespace Spyglass.Proxy.Wire;

/// <summary>
/// The one OP_MSG section walker (01 §3.3): a ref-struct enumerator over <c>kind(1) size(4) …</c> sections.
/// Lengths are untrusted int32 values, so no sum of an offset and a length is ever formed in <c>int</c>:
/// every bound is compared as <c>size &gt; remaining</c>. A section running past the end of the span (a capture
/// prefix) is reported as <see cref="Truncated"/> and ends the walk; an unknown kind or an impossible size ends
/// it with <see cref="Malformed"/>.
/// </summary>
public ref struct MsgSections
{
    private readonly ReadOnlySpan<byte> _s;
    private int _p;
    private bool _done;

    /// <param name="sections">Bytes from the first section to the end of the sections (checksum excluded).</param>
    public MsgSections(ReadOnlySpan<byte> sections)
    {
        _s = sections;
        _p = 0;
        _done = false;
        Kind = 0;
        Offset = Size = 0;
        Malformed = Truncated = false;
    }

    /// <summary>Sections of an OP_MSG frame (or its prefix). <paramref name="complete"/>: the span is the whole frame,
    /// so a present checksum is excluded from the sections. Offsets are relative to the frame start + 20.</summary>
    public static MsgSections Of(ReadOnlySpan<byte> frame, bool complete)
    {
        const int Start = FrameHeader.Size + 4;
        if (frame.Length < Start)
            return new MsgSections(default);
        var end = frame.Length;
        if (complete && ((MsgFlags)BinaryPrimitives.ReadUInt32LittleEndian(frame[FrameHeader.Size..]) & MsgFlags.ChecksumPresent) != 0)
            end = Math.Max(Start, end - 4);
        return new MsgSections(frame[Start..end]);
    }

    /// <summary>0 or 1.</summary>
    public byte Kind { get; private set; }
    /// <summary>Offset of the kind byte within the span given to the constructor.</summary>
    public int Offset { get; private set; }
    /// <summary>The declared size: the document length (kind 0) or the sequence size including its own 4 bytes (kind 1); ≥ 5.</summary>
    public int Size { get; private set; }
    /// <summary>The declared section extends past the span; the walk ends here.</summary>
    public bool Truncated { get; private set; }
    /// <summary>The walk stopped on an unknown kind or an impossible size.</summary>
    public bool Malformed { get; private set; }

    /// <summary>The payload bytes present in the span: the document (kind 0) or the size field onward (kind 1).</summary>
    public ReadOnlySpan<byte> Payload => _s.Slice(Offset + 1, Truncated ? _s.Length - Offset - 1 : Size);

    public bool MoveNext()
    {
        if (_done) return false;
        var remaining = _s.Length - _p;
        if (remaining < 5)
        {
            _done = true;
            return false;
        }
        var kind = _s[_p];
        var size = BinaryPrimitives.ReadInt32LittleEndian(_s[(_p + 1)..]);
        if (kind > 1 || size < 5)
        {
            Malformed = _done = true;
            return false;
        }
        Kind = kind;
        Offset = _p;
        Size = size;
        if (size > remaining - 1) // no `_p + 1 + size`: that wraps for size near int.MaxValue
        {
            Truncated = _done = true;
            return true;
        }
        _p += 1 + size;
        return true;
    }
}
