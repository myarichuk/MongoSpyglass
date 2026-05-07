namespace MongoSpyglass.Proxy;

/// <summary>
/// Simple synchronous listener interface for traffic events.
/// For high-volume or decoupled scenarios, the proxy now uses a bounded channel internally.
/// </summary>
public interface ITrafficListener
{
    void OnMessage(string tag, int requestId, string opCode, string command, string collection, string payloadJson, double? durationMs = null);
}
