namespace MongoSpyglass.Proxy;

public interface ITrafficListener
{
    void OnMessage(string tag, int requestId, string opCode, string command, string collection, string payloadJson, double? durationMs = null);
}
