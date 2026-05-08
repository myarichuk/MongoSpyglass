namespace MongoSpyglass.Proxy;

public interface ITrafficListener
{
    void OnMessage(in ObservedMessage msg);
    void OnConnectionClosed(string connectionId) { }
}

