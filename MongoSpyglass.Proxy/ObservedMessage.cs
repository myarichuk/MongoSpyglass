using MongoSpyglass.Proxy.Bson;
using MongoSpyglass.Proxy.WireProtocol;
using MongoSpyglass.Proxy.Memory;

namespace MongoSpyglass.Proxy;

public readonly unsafe struct ObservedMessage
{
    public readonly string Tag;
    public readonly string ConnectionId;
    public readonly int RequestId;
    public readonly int ResponseTo;
    public readonly OpCode OpCode;
    public readonly BlittableBsonDocument Document;
    public readonly byte* BodyPtr;
    public readonly int BodyLen;
    public readonly ArenaTracker Tracker;
    public readonly double? DurationMs;
    public readonly int MessageSizeBytes;
    public readonly int DocumentCount;

    public ObservedMessage(string tag, string connectionId, int requestId, int responseTo, OpCode opCode, BlittableBsonDocument document, byte* bodyPtr, int bodyLen, ArenaTracker tracker, double? durationMs, int messageSizeBytes, int documentCount = 0)
    {
        Tag = tag;
        ConnectionId = connectionId;
        RequestId = requestId;
        ResponseTo = responseTo;
        OpCode = opCode;
        Document = document;
        BodyPtr = bodyPtr;
        BodyLen = bodyLen;
        Tracker = tracker;
        DurationMs = durationMs;
        MessageSizeBytes = messageSizeBytes;
        DocumentCount = documentCount;
    }
    
    public ReadOnlySpan<byte> FullBody => new(BodyPtr, BodyLen);
    
    public void AddRef() => Tracker.AddRef();
    public void Release() => Tracker.Release();
}
