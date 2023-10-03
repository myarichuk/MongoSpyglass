using System.Runtime.InteropServices;

namespace MongoSpyglass.Proxy.WireProtocol
{
    public static class StructExtensions
    {
        public static unsafe Span<byte> AsBytes<T>(this T value, GrowableArena allocator) where T : unmanaged
        {
            var outputValue = allocator.Allocate<byte>(sizeof(T));
            var pValue = new Span<byte>((byte*)&value, sizeof(T)); //since T is unmanaged, no need for fixed

            pValue.CopyTo(outputValue);

            return outputValue;
        }
    }
}
