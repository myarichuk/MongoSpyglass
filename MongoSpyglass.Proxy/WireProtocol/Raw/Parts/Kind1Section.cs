using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MongoSpyglass.Proxy.WireProtocol.Raw.Parts
{
    [StructLayout(LayoutKind.Sequential, Pack = 0)]
    public ref struct Kind1Section
    {
        public int Size;
        public Span<char> DocumentSequenceIdentifier;
        public Span<byte> BsonDocumentArray; //continuous array of documents
    }
}
