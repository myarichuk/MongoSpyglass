using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace MongoSpyglass.Proxy.WireProtocol.Raw;

[StructLayout(LayoutKind.Sequential, Pack = 0)]
public ref struct MsgHeader
{
    public int MessageLength;

    public int RequestID;
            
    public int ResponseTo;

    public OpCode OpCode;
}