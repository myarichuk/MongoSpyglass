using System.Net;

namespace Spyglass.Collector;

/// <summary>
/// <c>spyglass-collector healthcheck</c>: GETs <c>/healthz</c> on the local listener and exits 0 on 200, 1 otherwise. The
/// listener comes from <c>--bind</c>, else <c>SPYGLASS_BIND</c> (what the container sets), else the default; a wildcard
/// address is probed on loopback. Nothing else is read, so it runs with any (or no) collector configuration. Plain
/// HTTP/1.1 over a socket (Kestrel answers <c>Connection: close</c> with an unchunked body).
/// </summary>
internal static class HealthProbe
{
    public static async Task<int> RunAsync(string[] args)
    {
        string? bind = Environment.GetEnvironmentVariable("SPYGLASS_BIND"), path = "/healthz";
        for (var i = 0; i + 1 < args.Length; i += 2)
        {
            if (args[i] == "--bind") bind = args[i + 1];
            else if (args[i] == "--path") path = args[i + 1];
        }
        var ep = bind is { Length: > 0 } ? IPEndPoint.Parse(bind) : new CollectorOptions().Bind;
        var host = ep.Address.Equals(IPAddress.Any) ? IPAddress.Loopback
            : ep.Address.Equals(IPAddress.IPv6Any) ? IPAddress.IPv6Loopback
            : ep.Address;
        // a raw HTTP/1.1 GET: the probe runs every 30 s in its own process, and HttpClient's JIT alone costs ~0.3 s of CPU
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            using var socket = new System.Net.Sockets.Socket(host.AddressFamily, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
            await socket.ConnectAsync(new IPEndPoint(host, ep.Port), cts.Token).ConfigureAwait(false);
            await socket.SendAsync(System.Text.Encoding.ASCII.GetBytes($"GET {path} HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n"), cts.Token).ConfigureAwait(false);
            var buf = new byte[16 * 1024];
            var n = 0;
            int read;
            while (n < buf.Length && (read = await socket.ReceiveAsync(buf.AsMemory(n), cts.Token).ConfigureAwait(false)) > 0) n += read;
            var response = System.Text.Encoding.UTF8.GetString(buf, 0, n);
            var body = response.IndexOf("\r\n\r\n", StringComparison.Ordinal) is var b and >= 0 ? response[(b + 4)..] : "";
            var ok = response.StartsWith("HTTP/1.1 200 ", StringComparison.Ordinal);
            Console.WriteLine($"{(response.Length >= 12 ? response[9..12] : "???")} {body}");
            return ok ? 0 : 1;
        }
        catch (Exception e) when (e is System.Net.Sockets.SocketException or OperationCanceledException)
        {
            Console.WriteLine($"{host}:{ep.Port}{path}: {e.Message}");
            return 1;
        }
    }
}
