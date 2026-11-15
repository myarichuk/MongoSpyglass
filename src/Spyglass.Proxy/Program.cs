using Spyglass.Proxy;
using Spyglass.Proxy.Config;
using Spyglass.Proxy.Diagnostics;

// Run socket continuations on the epoll threads instead of hopping to the thread pool. Measured on the
// mongod A/B bench (tools/ProxyBench): added p50 latency 70–104 µs → 16–46 µs, proxy CPU/op −30–45%
// (01 §14).
// Safe because nothing on the forwarding path blocks (01 §3, I1). Must be set before the first socket
// is created; an explicit value in the environment wins.
if (Environment.GetEnvironmentVariable("DOTNET_SYSTEM_NET_SOCKETS_INLINE_COMPLETIONS") == null)
    Environment.SetEnvironmentVariable("DOTNET_SYSTEM_NET_SOCKETS_INLINE_COMPLETIONS", "1");

// container HEALTHCHECK (the image has no shell or curl): spyglass-proxy healthcheck [--bind host:port] [--path /readyz]
if (args is ["healthcheck", ..])
    return await HealthProbe.RunAsync(args[1..]);

if (args is ["--help"] or ["-h"])
{
    Console.WriteLine("""
        spyglass-proxy --config <file.yaml|file.json> [--section.key value ...]

        Every configuration key (docs/spec/01-proxy.md §2) can be given as a flag, e.g.
          --upstream.uri mongodb://db1:27017,db2:27017/?replicaSet=rs0 --listen.basePort 27017
        or as an environment variable: SPYGLASS_UPSTREAM__URI, SPYGLASS_LISTEN__BASEPORT, ...
        Precedence: flags > environment > file.

        spyglass-proxy healthcheck [--path /readyz]   GET the metrics listener; exit 0 on 200 (container HEALTHCHECK)
        """);
    return 0;
}

ProxyConfig config;
try
{
    config = ConfigLoader.Load(args);
}
catch (ConfigException e)
{
    Console.Error.WriteLine($"configuration error: {e.Message}");
    return 2;
}

Log.Level = Log.Parse(config.LogLevel);

// Not `using`: signal and exit callbacks can still fire after Main returns, and Cancel() on a disposed source would abort the process.
var stop = new CancellationTokenSource();
void Stop()
{
    try { stop.Cancel(); }
    catch (ObjectDisposedException) { }
}

ProxyServer server;
try
{
    server = new ProxyServer(config);
}
catch (Exception e) when (e is ConfigException or System.Security.Cryptography.CryptographicException or FormatException)
{
    Log.Error($"startup failed: {e.Message}");
    return 3;
}
await using var disposeServer = server;

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    Stop();
};
// `docker stop` sends SIGTERM: take the graceful path (close connections with proxy_shutdown, flush the sender) instead of dying with 143.
using var sigterm = System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGTERM, c => { c.Cancel = true; Stop(); });
using var sigquit = System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGQUIT, c => { c.Cancel = true; Stop(); });

try
{
    await server.StartAsync(stop.Token);
}
catch (ConfigException e)
{
    Log.Error($"startup failed: {e.Message}");
    return 3;
}
catch (OperationCanceledException)
{
    return 0;
}

try
{
    await Task.Delay(Timeout.Infinite, stop.Token);
}
catch (OperationCanceledException)
{
}

Log.Info("shutting down");
return 0;
