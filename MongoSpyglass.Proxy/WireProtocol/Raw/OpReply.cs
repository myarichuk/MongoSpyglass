using SharpArena.Allocators;

namespace MongoSpyglass.Proxy.WireProtocol.Raw;

public ref struct OpReply
{
    public int ResponseFlags;
    public long CursorID;
    public int StartingFrom;
    public int NumberReturned;
    public Span<byte> Documents;
}
