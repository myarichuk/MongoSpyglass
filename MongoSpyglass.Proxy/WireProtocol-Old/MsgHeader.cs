using System.Runtime.InteropServices;

namespace MongoSpyglass.Proxy.WireProtocol
{
    [StructLayout(LayoutKind.Sequential, Pack = 0)]
    internal struct MsgHeader
    {
        public int MessageLength;

        public int RequestID;
            
        public int ResponseTo;

        public OpCode OpCode;
    }
}