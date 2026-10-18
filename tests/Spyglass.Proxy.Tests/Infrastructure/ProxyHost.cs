using Spyglass.Proxy.Config;

namespace Spyglass.Proxy.Tests.Infrastructure;

/// <summary>An in-process proxy in front of a <see cref="Deployment"/>.</summary>
public sealed class ProxyHost : IAsyncDisposable
{
    public required ProxyServer Server { get; init; }
    public required int BasePort { get; init; }

    public static async Task<ProxyHost> StartAsync(string upstreamUri, int listeners = 3, Action<Dictionary<string, string>>? configure = null)
    {
        var basePort = Ports.FreeRange(listeners + 4); // room for members added later
        var args = new Dictionary<string, string>
        {
            ["upstream.uri"] = upstreamUri,
            ["listen.basePort"] = basePort.ToString(),
            ["listen.portRange"] = $"{basePort},{basePort + listeners + 3}",
            ["metrics.bind"] = "127.0.0.1:0",
            ["logLevel"] = Environment.GetEnvironmentVariable("SPYGLASS_TEST_LOG") ?? "warn",
        };
        configure?.Invoke(args);
        var config = ConfigLoader.Load(args.SelectMany(kv => new[] { "--" + kv.Key, kv.Value }).ToArray(), new Dictionary<string, string?>());
        Diagnostics.Log.Level = Diagnostics.Log.Parse(config.LogLevel);
        var server = new ProxyServer(config);
        await server.StartAsync();
        return new ProxyHost { Server = server, BasePort = basePort };
    }

    public string Uri(string? setName = null, string? extra = null)
    {
        var opts = new List<string>();
        if (setName != null) opts.Add("replicaSet=" + setName);
        if (extra != null) opts.Add(extra);
        return $"mongodb://localhost:{BasePort}/" + (opts.Count > 0 ? "?" + string.Join('&', opts) : "");
    }

    public async Task<string> HttpGetAsync(string path)
    {
        using var http = new HttpClient();
        return await http.GetStringAsync($"http://{Server.MetricsEndpoint}{path}");
    }

    public ValueTask DisposeAsync() => Server.DisposeAsync();
}
