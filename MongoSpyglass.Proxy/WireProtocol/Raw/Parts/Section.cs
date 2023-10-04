using System.Runtime.InteropServices;

namespace MongoSpyglass.Proxy.WireProtocol.Raw.Parts
{
    [StructLayout(LayoutKind.Explicit, Pack = 0)]
    public ref struct Section
    {
        [FieldOffset(0)]
        public byte Kind;

        [FieldOffset(1)] // Kind 0
        public Span<byte> BsonDocument;

        [FieldOffset(1)] //Kind 1 
        public DocumentSequence BsonDocuments;
    }
}
