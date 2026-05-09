namespace MongoSpyglass.Proxy;

public interface ITrafficListener
{
    void OnMessage(in ObservedMessage msg);
    void OnConnectionClosed(string connectionId) { }

    /// <summary>
    /// Return false to skip expensive BSON body parsing for this listener.
    /// Default is true (safe, backward compatible).
    /// </summary>
    bool NeedsFullDocument => true;
}
