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
            var opMsg = this;
            var outputValue = allocator.Allocate<byte>(sizeof(uint) + sizeof(byte) + opMsg.DataSection.Length);

            opMsg.Flags.AsBytes(allocator).CopyTo(outputValue);
            outputValue[sizeof(uint)] = opMsg.Kind;
            opMsg.DataSection.CopyTo(outputValue[(sizeof(uint) + sizeof(byte))..]);

            return outputValue;
        }
    }
}
