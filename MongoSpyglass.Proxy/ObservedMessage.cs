using MongoSpyglass.Proxy.Bson;
using MongoSpyglass.Proxy.WireProtocol;
using MongoSpyglass.Proxy.Memory;

namespace MongoSpyglass.Proxy;

public readonly struct ObservedMessage
{
    public readonly string Tag;
    public readonly string ConnectionId;
    public readonly int RequestId;
    public readonly int ResponseTo;
    public readonly OpCode OpCode;
    public readonly BlittableBsonDocument Document;
    public readonly ArenaTracker Tracker;
    public readonly double? DurationMs;
    public readonly int MessageSizeBytes;
    public readonly int DocumentCount;

    public ObservedMessage(string tag, string connectionId, int requestId, int responseTo, OpCode opCode, BlittableBsonDocument document, ArenaTracker tracker, double? durationMs, int messageSizeBytes, int documentCount = 0)
    {
        Tag = tag;
        ConnectionId = connectionId;
        RequestId = requestId;
        ResponseTo = responseTo;
        OpCode = opCode;
        Document = document;
        Tracker = tracker;
        DurationMs = durationMs;
        MessageSizeBytes = messageSizeBytes;
        DocumentCount = documentCount;
    }
    
    public void AddRef() => Tracker.AddRef();
    public void Release() => Tracker.Release();
}
