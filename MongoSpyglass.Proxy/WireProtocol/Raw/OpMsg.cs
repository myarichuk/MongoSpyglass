// Ignore Spelling: Mongo

using Simple.Arena;
using System.Runtime.InteropServices;

namespace MongoSpyglass.Proxy.WireProtocol.Raw
{
    [StructLayout(LayoutKind.Sequential)]
    public unsafe ref partial struct OpMsg
    {
        public FlagBits Flags;

        public byte Kind;

        public Span<byte> DataSection;

        public static unsafe OpMsg FromBytes(Span<byte> pBytes)
        {
            var pOpMsg = (OpMsg*)pBytes.ToIntPtr().ToPointer();

            return new OpMsg
            {
                Flags = pOpMsg->Flags,
                Kind = pOpMsg->Kind,
                DataSection = pOpMsg->DataSection
            };
        }

        public unsafe Span<byte> ToBytes(GrowableArena allocator)
        {
            var opQuery = this;
            var outputValue = allocator.Allocate<byte>(sizeof(uint) + sizeof(byte) + DataSection.Length);

            var pOpMsg = (OpMsg*)outputValue.ToIntPtr().ToPointer();

            pOpMsg->Flags = opQuery.Flags;
            pOpMsg->Kind = opQuery.Kind;
            pOpMsg->DataSection = opQuery.DataSection;

            return outputValue;
        }
    }
}
