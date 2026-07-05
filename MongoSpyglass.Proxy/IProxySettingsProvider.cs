using System.Net;

namespace MongoSpyglass.Proxy;

public interface IProxySettingsProvider
{
    (IPEndPoint TargetServer, int IncomingPort) GetCurrentSettings();
    IPAddress GetBindAddress();
    event Action OnSettingsChanged;
}
