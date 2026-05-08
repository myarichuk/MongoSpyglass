using MongoSpyglass.Proxy.Bson;
using MongoSpyglass.Proxy.WireProtocol;
using SharpArena.Allocators;

namespace MongoSpyglass.Proxy;

public record ObservedMessage(
    string Tag,
    int RequestId,
    OpCode OpCode,
    BlittableBsonDocument Document,
    ArenaAllocator Arena,
    double? DurationMs = null) : IDisposable
{
    public void Dispose() => Memory.ArenaPool.Shared.Return(Arena);
}
