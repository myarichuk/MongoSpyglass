using System.Runtime.InteropServices;
using MongoSpyglass.Proxy.WireProtocol.Raw.Parts;

namespace MongoSpyglass.Proxy.WireProtocol.Raw
{
    [StructLayout(LayoutKind.Sequential)]
    public unsafe ref struct OpMsg
    {
        [Flags]
        public enum FlagBits : uint
        {
            None = 0,
            ChecksumPresent = 1 << 0,
            MoreToCome = 1 << 1,
            ExhaustAllowed = 1 << 16
        }

        public FlagBits Flags;
        public Span<byte> Sections;
    }
}
