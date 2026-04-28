using MongoSpyglass.Proxy.WireProtocol;

namespace MongoSpyglass.Proxy.Profiling;

public struct OperationMetrics
{
    public long TimestampStart;
    public OpCode OpCode;
    public int RequestId;
    
    // For now, we'll store basic info. 
    // In the future, we can store collection names using the Arena-backed approach.
}
