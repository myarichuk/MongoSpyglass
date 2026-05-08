using System.Runtime.InteropServices;
using SharpArena.Collections;

namespace MongoSpyglass.Proxy.WireProtocol.Raw
{
    [StructLayout(LayoutKind.Sequential)]
    public unsafe ref partial struct OpMsg
    {
        public FlagBits Flags;
        public ArenaList<byte> Sections; // Byte array representing section data
        public uint? Checksum;
    }
}
