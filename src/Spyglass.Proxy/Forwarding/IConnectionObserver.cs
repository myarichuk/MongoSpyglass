namespace Spyglass.Proxy.Forwarding;

internal enum CloseReason
{
    ClientClosed,
    UpstreamClosed,
    InvalidFrame,
    UpstreamConnectFailed,
    TlsFailed,
    ProxyShutdown,
}

/// <summary>Connection lifecycle and per-direction taps for the observation side (01 §4).</summary>
internal interface IConnectionObserver
{
    IFrameTap CreateTap(ConnectionContext conn, bool isRequest);
    void Opened(ConnectionContext conn);
    void Closed(ConnectionContext conn, CloseReason reason, string? detail);
}

internal sealed class NullObserver : IConnectionObserver
{
    public static readonly NullObserver Instance = new();
    public IFrameTap CreateTap(ConnectionContext conn, bool isRequest) => NullTap.Instance;
    public void Opened(ConnectionContext conn) { }
    public void Closed(ConnectionContext conn, CloseReason reason, string? detail) { }
}

internal static class CloseReasonNames
{
    public static string Wire(this CloseReason r) => r switch
    {
        CloseReason.ClientClosed => "client_closed",
        CloseReason.UpstreamClosed => "upstream_closed",
        CloseReason.InvalidFrame => "invalid_frame",
        CloseReason.UpstreamConnectFailed => "upstream_connect_failed",
        CloseReason.TlsFailed => "tls_failed",
        _ => "proxy_shutdown",
    };
}
