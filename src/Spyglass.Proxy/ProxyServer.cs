using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Spyglass.Proxy.Config;
using Spyglass.Proxy.Diagnostics;
using Spyglass.Proxy.Forwarding;
using Spyglass.Proxy.Metrics;
using Spyglass.Proxy.Observation;
using Spyglass.Proxy.Topology;
using Spyglass.Proxy.Wire;

namespace Spyglass.Proxy;

public sealed class ProxyServer : IAsyncDisposable
{
    public const int Schema = 1;

    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentDictionary<Member, Socket> _listeners = new();
    private readonly ConcurrentDictionary<long, ProxyConnection> _live = new();
    private readonly List<Task> _acceptLoops = [];
    private long _connSeq;
    private int _openConnections;
    private MetricsServer? _metricsServer;
    private bool _started;

    public ProxyServer(ProxyConfig config)
    {
        Config = config;
        Uri = MongoUri.Parse(config.Upstream.Uri!);
        Upstream = new UpstreamConnector(config, Uri);
        if (config.Listen.Tls.Mode == DownstreamTlsMode.Tls)
            DownstreamTls = new DownstreamTls(config.Listen.Tls, config.Listen.AdvertiseHost);
        Topology = new TopologyManager(this);
    }

    public ProxyConfig Config { get; }
    public MongoUri Uri { get; }
    public Guid ProxyId { get; } = Guid.NewGuid();
    public ProxyMetrics Metrics { get; } = new();
    public UpstreamConnector Upstream { get; }
    public DownstreamTls? DownstreamTls { get; }
    public TopologyManager Topology { get; }
    internal IConnectionObserver Observer { get; set; } = NullObserver.Instance;
    internal Observer? Observation { get; private set; }
    public CancellationToken ShutdownToken => _shutdown.Token;
    public int OpenConnections => Volatile.Read(ref _openConnections);
    public bool Ready { get; private set; }

    /// <summary>Endpoint the metrics server is bound to (after start), or null if disabled.</summary>
    public IPEndPoint? MetricsEndpoint => _metricsServer?.Endpoint;

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_started) throw new InvalidOperationException("already started");
        _started = true;

        foreach (var w in ConfigLoader.Warnings(Config))
            Log.Warn(w);
        if (DownstreamTls is { } tls)
            Log.Info($"client TLS: {(tls.SelfSigned ? "self-signed" : "configured")} certificate, SHA-256 {tls.Sha256Fingerprint}");

        Observation = new Observer(this);
        Observation.Start();
        Observer = Observation;

        if (Config.Metrics.Bind != null)
        {
            _metricsServer = new MetricsServer(this, IPEndPoint.Parse(Config.Metrics.Bind));
            _metricsServer.Start();
        }

        var seeds = Uri.IsSrv
            ? await SrvResolver.ResolveSeedsAsync(Uri, ct).ConfigureAwait(false)
            : Uri.Hosts;
        await Topology.DiscoverAsync(seeds, ct).ConfigureAwait(false);
        Topology.StartMonitors();
        Ready = true;

        var worstCase = Config.Limits.ObserveQueueBytes + Config.Collector.BufferBytes
                        + (long)Config.Limits.MaxClientConnections * 2 * Config.Limits.ReadBufferBytes;
        Log.Info($"proxy {ProxyId} ready; worst-case buffer memory {worstCase / (1024 * 1024)} MiB " +
                 $"(observe queue + export buffer + {Config.Limits.MaxClientConnections} connections × 2 × {Config.Limits.ReadBufferBytes / 1024} KiB)");
    }

    /// <summary>Binds and starts accepting for <paramref name="member"/>. Throws if the port can't be bound.</summary>
    internal void StartListener(Member member)
    {
        var bind = IPAddress.Parse(Config.Listen.BindAddress);
        var socket = new Socket(bind.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.Bind(new IPEndPoint(bind, member.ListenPort));
            socket.Listen(512);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
        _listeners[member] = socket;
        lock (_acceptLoops)
            _acceptLoops.Add(Task.Run(() => AcceptLoopAsync(member, socket)));
    }

    internal void StopListener(Member member)
    {
        if (_listeners.TryRemove(member, out var s))
            s.Dispose();
    }

    private async Task AcceptLoopAsync(Member member, Socket listener)
    {
        var ct = _shutdown.Token;
        while (!ct.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await listener.AcceptAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException e) when (e.SocketErrorCode == SocketError.OperationAborted) { return; }
            catch (SocketException e)
            {
                Log.Warn($"listener {member.Advertised}: accept failed: {e.SocketErrorCode}");
                continue;
            }

            if (Interlocked.Increment(ref _openConnections) > Config.Limits.MaxClientConnections)
            {
                Interlocked.Decrement(ref _openConnections);
                member.AcceptedLimit.Inc();
                client.Dispose();
                continue;
            }

            var seq = Interlocked.Increment(ref _connSeq);
            var conn = new ProxyConnection(this, member, client, seq);
            _live[seq] = conn;
            _ = Task.Run(async () =>
            {
                try { await conn.RunAsync(ct).ConfigureAwait(false); }
                finally
                {
                    _live.TryRemove(seq, out _);
                    Interlocked.Decrement(ref _openConnections);
                }
            });
        }
    }

    /// <summary>Resets live client connections matching <paramref name="filter"/>; returns how many.</summary>
    internal int ResetConnections(Func<ConnectionContext, bool>? filter = null)
    {
        var n = 0;
        foreach (var c in _live.Values)
        {
            if (filter != null && !filter(c.Context)) continue;
            c.Reset();
            n++;
        }
        return n;
    }

    /// <summary>The <c>$spyglass</c> discovery document for a listener (02 §2.2), encoded once.</summary>
    internal byte[] BuildSpyglassDocument(string advertised)
    {
        var w = new BsonWriter(256);
        w.StartDocument();
        w.WriteInt32("v", 0);
        w.WriteInt32("schema", Schema);
        w.WriteString("proxyId", ProxyId.ToString());
        w.WriteString("member", advertised);
        var ingest = Config.Discovery.AdvertiseIngest ?? Config.Collector.Endpoint;
        if (ingest != null)
            w.WriteString("ingest", ingest);
        if (Config.Discovery.AdvertiseToken && Config.Collector.Token is { Length: > 0 } token)
            w.WriteString("token", token);
        w.EndDocument();
        return w.ToArray();
    }

    public async ValueTask DisposeAsync()
    {
        Ready = false;
        _shutdown.Cancel();
        foreach (var s in _listeners.Values)
            s.Dispose();
        _listeners.Clear();
        Task[] loops;
        lock (_acceptLoops) loops = _acceptLoops.ToArray();
        try { await Task.WhenAll(loops).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch (TimeoutException) { }

        // Live connections observe the shutdown token and close with proxy_shutdown.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!_live.IsEmpty && DateTime.UtcNow < deadline)
            await Task.Delay(20).ConfigureAwait(false);

        if (Observation != null)
            await Observation.DisposeAsync().ConfigureAwait(false);
        if (_metricsServer != null)
            await _metricsServer.DisposeAsync().ConfigureAwait(false);
        _shutdown.Dispose();
    }
}
