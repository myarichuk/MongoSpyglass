using System.Runtime.InteropServices;

namespace MongoSpyglass.Proxy.WireProtocol.Raw.Parts
{
    [StructLayout(LayoutKind.Sequential, Pack = 0)]
    public ref struct Kind0Section
    {
        public Span<byte> BsonDocument;
    }
}
