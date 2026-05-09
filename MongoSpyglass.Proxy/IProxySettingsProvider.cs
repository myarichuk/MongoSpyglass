using System;
using System.Net;

namespace MongoSpyglass.Proxy;

public interface IProxySettingsProvider
{
    (IPEndPoint TargetServer, int IncomingPort) GetCurrentSettings();
    event Action OnSettingsChanged;
}
