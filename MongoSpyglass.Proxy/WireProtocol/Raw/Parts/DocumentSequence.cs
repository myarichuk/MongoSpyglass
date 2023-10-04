using System.Runtime.InteropServices;

namespace MongoSpyglass.Proxy.WireProtocol.Raw.Parts
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public ref struct DocumentSequence
    {
        public int Size;
        public Span<char> DocumentSequenceIdentifier;
        public Span<byte> BsonDocumentArray; //continuous array of documents
    }
}
