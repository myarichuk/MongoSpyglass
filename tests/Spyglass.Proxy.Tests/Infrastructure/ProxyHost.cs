using System.Collections.Concurrent;
using System.Text.Json;
using Spyglass.Proxy.Config;

namespace Spyglass.Proxy.Tests.Infrastructure;

/// <summary>An in-process proxy in front of a <see cref="Deployment"/>.</summary>
public sealed class ProxyHost : IAsyncDisposable
{
    public required ProxyServer Server { get; init; }
    public required int BasePort { get; init; }

    /// <summary>Every wire record the proxy built (headless mode included), parsed.</summary>
    public ConcurrentQueue<JsonElement> Records { get; } = new();

    public IEnumerable<JsonElement> RecordsOf(string type) => Records.Where(r => r.GetProperty("type").GetString() == type);

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
        var host = new ProxyHost { Server = server, BasePort = basePort };
        server.Observation!.RecordTap = line => host.Records.Enqueue(JsonDocument.Parse(line.ToArray()).RootElement.Clone());
        return host;
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

    /// <summary>Waits until the observer has produced records matching <paramref name="predicate"/>.</summary>
    public async Task<List<JsonElement>> WaitRecordsAsync(Func<JsonElement, bool> predicate, int count, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (true)
        {
            var found = Records.Where(predicate).ToList();
            if (found.Count >= count) return found;
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"expected {count} records, have {found.Count}");
            await Task.Delay(20);
        }
    }

    /// <summary>
    /// Waits until every expected key has a record (a count is not enough: other tests' records share the
    /// fixture and the observer delivers asynchronously). Returns the index either way, so the caller's
    /// assertion names what is missing.
    /// </summary>
    public async Task<ILookup<TKey, JsonElement>> WaitForKeysAsync<TKey>(string type, Func<JsonElement, TKey> keyOf, IReadOnlyCollection<TKey> expected, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (true)
        {
            var index = RecordsOf(type).ToLookup(keyOf);
            if (expected.All(index.Contains) || DateTime.UtcNow > deadline) return index;
            await Task.Delay(20);
        }
    }

    public ValueTask DisposeAsync() => Server.DisposeAsync();
}
