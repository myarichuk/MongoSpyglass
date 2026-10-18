using System.Buffers.Binary;

namespace Spyglass.Proxy.Wire;

public enum RequestKind : byte
{
    Other,
    Hello,
}

/// <summary>
/// Constant-work classification of a request frame from its first bytes (01 §3.3).
/// The forwarding pump only needs to know whether a frame is hello-family; the observer
/// extracts the full command name from its capture.
/// </summary>
public static class RequestInspector
{
    /// <summary>Bytes of the frame (header included) the pump holds back to classify a request.</summary>
    public const int InspectBytes = FrameHeader.Size + 256;

    public static RequestKind Classify(ReadOnlySpan<byte> prefix)
    {
        if (prefix.Length < FrameHeader.Size + 4)
            return RequestKind.Other;
        var opCode = BinaryPrimitives.ReadInt32LittleEndian(prefix[12..]);
        var name = opCode switch
        {
            OpCodes.Msg => MsgCommandName(prefix),
            OpCodes.Query => QueryCommandName(prefix, out _),
            _ => default
        };
        return IsHelloName(name) ? RequestKind.Hello : RequestKind.Other;
    }

    public static bool IsHelloName(ReadOnlySpan<byte> name) =>
        name.SequenceEqual("hello"u8) || name.SequenceEqual("isMaster"u8) || name.SequenceEqual("ismaster"u8);

    /// <summary>First element name of the first kind-0 section, if it lies within <paramref name="frame"/>.</summary>
    public static ReadOnlySpan<byte> MsgCommandName(ReadOnlySpan<byte> frame)
    {
        // header(16) flags(4) kind(1) docLen(4) type(1) name\0
        var p = FrameHeader.Size + 4;
        while (p < frame.Length)
        {
            var kind = frame[p];
            if (kind == 0)
                return FirstElementName(frame[(p + 1)..], out _);
            if (kind != 1 || p + 5 > frame.Length)
                return default;
            var size = BinaryPrimitives.ReadInt32LittleEndian(frame[(p + 1)..]);
            if (size < 4) return default;
            p += 1 + size;
        }
        return default;
    }

    /// <summary>
    /// Command name of an OP_QUERY against <c>*.$cmd</c>, unwrapping <c>$query</c>/<c>query</c>.
    /// Empty for a legacy (non-command) query.
    /// </summary>
    public static ReadOnlySpan<byte> QueryCommandName(ReadOnlySpan<byte> frame, out ReadOnlySpan<byte> fullCollectionName)
    {
        fullCollectionName = default;
        var p = FrameHeader.Size + 4;
        if (p >= frame.Length) return default;
        var nul = frame[p..].IndexOf((byte)0);
        if (nul < 0) return default;
        fullCollectionName = frame.Slice(p, nul);
        if (!fullCollectionName.EndsWith(".$cmd"u8)) return default;
        p += nul + 1 + 8; // numberToSkip, numberToReturn
        if (p >= frame.Length) return default;
        var doc = frame[p..];
        var name = FirstElementName(doc, out var type);
        if ((name.SequenceEqual("$query"u8) || name.SequenceEqual("query"u8)) && type == BsonType.Document)
        {
            // doc(4) type(1) name\0 → nested document
            var nested = 4 + 1 + name.Length + 1;
            if (nested < doc.Length)
                return FirstElementName(doc[nested..], out _);
            return default;
        }
        return name;
    }

    private static ReadOnlySpan<byte> FirstElementName(ReadOnlySpan<byte> doc, out byte type)
    {
        type = 0;
        if (doc.Length < 6) return default;
        type = doc[4];
        if (type == 0) return default;
        var nul = doc[5..].IndexOf((byte)0);
        return nul < 0 ? default : doc.Slice(5, nul);
    }
}
