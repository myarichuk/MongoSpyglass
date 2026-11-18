using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Spyglass.Proxy.Diagnostics;

/// <summary>
/// <c>spyglass-proxy healthcheck [--path /readyz]</c>: GETs the metrics listener (01 §12) and exits 0 on 200, 1 otherwise.
/// The listener is the effective <c>metrics.bind</c>: <c>--bind</c>, else the configuration the proxy itself loads
/// (<c>--config</c>/<c>SPYGLASS_CONFIG</c>, <c>SPYGLASS_*</c> environment), else the default; a wildcard address is
/// probed on loopback. The container image has no shell or curl, so the probe is the binary itself, over a raw socket:
/// it runs every 30 s in a fresh process, and HttpClient's JIT alone would cost ~0.3 s of CPU each time.
/// </summary>
internal static class HealthProbe
{
    public static async Task<int> RunAsync(string[] args)
    {
        string? bind = null, path = "/readyz", config = null;
        for (var i = 0; i + 1 < args.Length; i += 2)
        {
            if (args[i] == "--bind") bind = args[i + 1];
            else if (args[i] == "--path") path = args[i + 1];
            else if (args[i] == "--config") config = args[i + 1];
        }
        if (bind == null)
        {
            try
            {
                // the same loader as the proxy: a file-configured metrics.bind is honoured
                bind = Config.ConfigLoader.Load(config != null ? ["--config", config] : []).Metrics.Bind;
                if (bind == null)
                {
                    Console.WriteLine("metrics endpoint disabled (metrics.bind is unset); nothing to probe");
                    return 0;
                }
            }
            catch (Config.ConfigException)
            {
                // e.g. no upstream.uri in this process's view of the config: fall back to what the container sets
                bind = Environment.GetEnvironmentVariable("SPYGLASS_METRICS__BIND");
            }
        }
        var ep = IPEndPoint.Parse(bind is { Length: > 0 } ? bind : new Config.MetricsConfig().Bind!);
        var host = ep.Address.Equals(IPAddress.Any) ? IPAddress.Loopback
            : ep.Address.Equals(IPAddress.IPv6Any) ? IPAddress.IPv6Loopback
            : ep.Address;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            using var socket = new Socket(host.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync(new IPEndPoint(host, ep.Port), cts.Token).ConfigureAwait(false);
            await socket.SendAsync(Encoding.ASCII.GetBytes($"GET {path} HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n"), cts.Token).ConfigureAwait(false);
            var buf = new byte[16 * 1024];
            var n = 0;
            int read;
            while (n < buf.Length && (read = await socket.ReceiveAsync(buf.AsMemory(n), cts.Token).ConfigureAwait(false)) > 0) n += read;
            var response = Encoding.UTF8.GetString(buf, 0, n);
            var body = response.IndexOf("\r\n\r\n", StringComparison.Ordinal) is var b and >= 0 ? response[(b + 4)..] : "";
            var ok = response.StartsWith("HTTP/1.1 200 ", StringComparison.Ordinal);
            Console.Write($"{(response.Length >= 12 ? response[9..12] : "???")} {body}");
            return ok ? 0 : 1;
        }
        catch (Exception e) when (e is SocketException or OperationCanceledException)
        {
            Console.WriteLine($"{host}:{ep.Port}{path}: {e.Message}");
            return 1;
        }
    }
}
