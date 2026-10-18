using System.Buffers.Binary;

namespace Spyglass.Proxy.Wire;

public static class OpCodes
{
    public const int Reply = 1;
    public const int Query = 2004;
    public const int Compressed = 2012;
    public const int Msg = 2013;

    public static string Name(int opCode) => opCode switch
    {
        Reply => "OP_REPLY",
        Query => "OP_QUERY",
        Compressed => "OP_COMPRESSED",
        Msg => "OP_MSG",
        2001 => "OP_UPDATE",
        2002 => "OP_INSERT",
        2005 => "OP_GET_MORE",
        2006 => "OP_DELETE",
        2007 => "OP_KILL_CURSORS",
        _ => "OP_" + opCode
    };
}

[Flags]
public enum MsgFlags : uint
{
    None = 0,
    ChecksumPresent = 1 << 0,
    MoreToCome = 1 << 1,
    ExhaustAllowed = 1 << 16,
}

public readonly struct FrameHeader
{
    public const int Size = 16;

    public readonly int MessageLength;
    public readonly int RequestId;
    public readonly int ResponseTo;
    public readonly int OpCode;

    public FrameHeader(int messageLength, int requestId, int responseTo, int opCode)
    {
        MessageLength = messageLength;
        RequestId = requestId;
        ResponseTo = responseTo;
        OpCode = opCode;
    }

    public static FrameHeader Read(ReadOnlySpan<byte> s) => new(
        BinaryPrimitives.ReadInt32LittleEndian(s),
        BinaryPrimitives.ReadInt32LittleEndian(s[4..]),
        BinaryPrimitives.ReadInt32LittleEndian(s[8..]),
        BinaryPrimitives.ReadInt32LittleEndian(s[12..]));

    public void Write(Span<byte> s)
    {
        BinaryPrimitives.WriteInt32LittleEndian(s, MessageLength);
        BinaryPrimitives.WriteInt32LittleEndian(s[4..], RequestId);
        BinaryPrimitives.WriteInt32LittleEndian(s[8..], ResponseTo);
        BinaryPrimitives.WriteInt32LittleEndian(s[12..], OpCode);
    }
}
