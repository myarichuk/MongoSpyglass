using System.Runtime.InteropServices;

namespace MongoSpyglass.Proxy.WireProtocol;

[StructLayout(LayoutKind.Explicit)]
internal unsafe struct Section
{
    [FieldOffset(0)]
    public byte Kind;

    [FieldOffset(1)] // Kind 0
    public byte* BsonDocument;

    [FieldOffset(1)] //Kind 1 
    public DocumentSequence BsonDocuments;
}

/*
    public int Size;
   public char* DocumentSequenceIdentifier;
   public byte** BsonDocumentArray;
 */