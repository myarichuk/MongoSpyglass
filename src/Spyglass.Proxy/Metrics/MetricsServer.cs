using System.Net;
using System.Net.Sockets;
using System.Text;
using Spyglass.Proxy.Diagnostics;

namespace Spyglass.Proxy.Metrics;

/// <summary>
/// Minimal HTTP/1.1 server for <c>/metrics</c>, <c>/healthz</c>, <c>/readyz</c> (01 §12).
/// GET only, one request per connection, bounded request size. Not on the forwarding path.
/// </summary>
internal sealed class MetricsServer : IAsyncDisposable
{
    private readonly ProxyServer _proxy;
    private readonly Socket _listener;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public MetricsServer(ProxyServer proxy, IPEndPoint bind)
    {
        _proxy = proxy;
        _listener = new Socket(bind.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        _listener.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _listener.Bind(bind);
        _listener.Listen(64);
        Endpoint = (IPEndPoint)_listener.LocalEndPoint!;
    }

    public IPEndPoint Endpoint { get; }

    public void Start()
    {
        _loop = Task.Run(AcceptLoopAsync);
        Log.Info($"metrics on http://{Endpoint}/metrics");
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            Socket s;
            try { s = await _listener.AcceptAsync(_cts.Token).ConfigureAwait(false); }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException) { return; }
            _ = Task.Run(() => ServeAsync(s));
        }
    }

    private async Task ServeAsync(Socket s)
    {
        using var _ = s;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await using var stream = new NetworkStream(s, ownsSocket: false);
            var buf = new byte[8192];
            var n = 0;
            while (n < buf.Length)
            {
                var r = await stream.ReadAsync(buf.AsMemory(n), timeout.Token).ConfigureAwait(false);
                if (r == 0) break;
                n += r;
                if (buf.AsSpan(0, n).IndexOf("\r\n\r\n"u8) >= 0) break;
            }
            var requestLine = Encoding.ASCII.GetString(buf, 0, n).Split("\r\n")[0].Split(' ');
            var (status, contentType, body) = requestLine is [var method, var target, ..]
                ? Handle(method, target.Split('?')[0])
                : (400, "text/plain", "bad request\n");

            var payload = Encoding.UTF8.GetBytes(body);
            var head = $"HTTP/1.1 {status} {Reason(status)}\r\nContent-Type: {contentType}\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(head), timeout.Token).ConfigureAwait(false);
            await stream.WriteAsync(payload, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or SocketException or OperationCanceledException)
        {
        }
    }

    private (int, string, string) Handle(string method, string path)
    {
        if (method != "GET")
            return (405, "text/plain", "method not allowed\n");
        switch (path)
        {
            case "/metrics":
                return (200, "text/plain; version=0.0.4; charset=utf-8", _proxy.Metrics.Registry.Render());
            case "/healthz":
                return (200, "text/plain", "ok\n");
            case "/readyz":
            {
                var monitorOk = _proxy.Topology.AnyMonitorSucceededWithin(TimeSpan.FromSeconds(30));
                var ready = _proxy.Ready && monitorOk;
                var sb = new StringBuilder();
                sb.Append(ready ? "ready\n" : "not ready\n");
                sb.Append("proxyId ").Append(_proxy.ProxyId).Append('\n');
                sb.Append("listeners ").Append(_proxy.Ready ? "bound" : "starting").Append('\n');
                sb.Append("monitor ").Append(monitorOk ? "ok" : "no successful hello in 30s").Append('\n');
                sb.Append("topology ").Append(_proxy.Topology.Describe()).Append('\n');
                if (_proxy.DownstreamTls is { } tls)
                    sb.Append("tlsSha256 ").Append(tls.Sha256Fingerprint).Append('\n');
                return (ready ? 200 : 503, "text/plain", sb.ToString());
            }
            default:
                return (404, "text/plain", "not found\n");
        }
    }

    private static string Reason(int s) => s switch
    {
        200 => "OK",
        400 => "Bad Request",
        404 => "Not Found",
        405 => "Method Not Allowed",
        503 => "Service Unavailable",
        _ => "Status"
    };

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Dispose();
        if (_loop != null)
            await _loop.ConfigureAwait(false);
        _cts.Dispose();
    }
}
