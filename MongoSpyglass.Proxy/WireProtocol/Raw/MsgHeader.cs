using System.Runtime.InteropServices;
using MongoSpyglass.Proxy.WireProtocol.Raw.Parts;

namespace MongoSpyglass.Proxy.WireProtocol.Raw;

[StructLayout(LayoutKind.Sequential, Pack = 0)]
public ref struct MsgHeader
{
    public int MessageLength;

    public int RequestID;
            
    public int ResponseTo;

    public OpCode OpCode;
}