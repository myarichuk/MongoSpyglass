using Spyglass.Proxy;
using Spyglass.Proxy.Config;
using Spyglass.Proxy.Diagnostics;

// Run socket continuations on the epoll threads instead of hopping to the thread pool. Measured on the
// mongod A/B bench (tools/ProxyBench): added p50 latency 70–104 µs → 16–22 µs, proxy CPU/op −30%.
// Safe because nothing on the forwarding path blocks (01 §3, I1). Must be set before the first socket
// is created; an explicit value in the environment wins.
if (Environment.GetEnvironmentVariable("DOTNET_SYSTEM_NET_SOCKETS_INLINE_COMPLETIONS") == null)
    Environment.SetEnvironmentVariable("DOTNET_SYSTEM_NET_SOCKETS_INLINE_COMPLETIONS", "1");

if (args is ["--help"] or ["-h"])
{
    Console.WriteLine("""
        spyglass-proxy --config <file.yaml|file.json> [--section.key value ...]

        Every configuration key (docs/spec/01-proxy.md §2) can be given as a flag, e.g.
          --upstream.uri mongodb://db1:27017,db2:27017/?replicaSet=rs0 --listen.basePort 27017
        or as an environment variable: SPYGLASS_UPSTREAM__URI, SPYGLASS_LISTEN__BASEPORT, ...
        Precedence: flags > environment > file.
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

await using var server = new ProxyServer(config);
using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stop.Cancel();
};
AppDomain.CurrentDomain.ProcessExit += (_, _) => stop.Cancel();

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
