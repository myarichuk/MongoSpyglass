using System.Runtime.InteropServices;

namespace MongoSpyglass.Proxy.WireProtocol;

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
internal unsafe struct DocumentSequence
{
    public int Size;
    public char* DocumentSequenceIdentifier;
    public byte** BsonDocumentArray;
}