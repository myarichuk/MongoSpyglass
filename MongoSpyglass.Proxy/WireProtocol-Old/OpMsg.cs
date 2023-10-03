using System.Runtime.InteropServices;

namespace MongoSpyglass.Proxy.WireProtocol
{
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct OpMsg
    {
        [Flags]
        internal enum FlagBits : uint
        {
            None = 0,
            ChecksumPresent = 1 << 0,
            MoreToCome = 1 << 1,
            ExhaustAllowed = 1 << 16
        }

        public FlagBits Flags;
        public Section* Sections;
    }
}
