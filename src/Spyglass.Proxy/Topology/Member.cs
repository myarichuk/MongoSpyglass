using Spyglass.Proxy.Config;
using Spyglass.Proxy.Metrics;
using Spyglass.Proxy.Wire;

namespace Spyglass.Proxy.Topology;

/// <summary>One upstream mongod/mongos and the listener that forwards to it (README §5).</summary>
public sealed class Member
{
    public Member(HostPort upstream, int listenPort, string advertised, HelloRewriteContext rewriteContext, ProxyMetrics metrics)
    {
        Upstream = upstream;
        ListenPort = listenPort;
        Advertised = advertised;
        RewriteContext = rewriteContext;

        OpenConnections = metrics.Connections.WithLabels(advertised, "open");
        BytesFromClient = metrics.Bytes.WithLabels(advertised, "client_to_server");
        BytesFromServer = metrics.Bytes.WithLabels(advertised, "server_to_client");
        FramesFromClient = metrics.Frames.WithLabels(advertised, "client_to_server");
        FramesFromServer = metrics.Frames.WithLabels(advertised, "server_to_client");
        InvalidFrames = metrics.InvalidFrames.WithLabels(advertised);
        AcceptedOk = metrics.ConnectionsTotal.WithLabels(advertised, "ok");
        AcceptedUpstreamFailed = metrics.ConnectionsTotal.WithLabels(advertised, "upstream_failed");
        AcceptedTlsFailed = metrics.ConnectionsTotal.WithLabels(advertised, "tls_failed");
        AcceptedLimit = metrics.ConnectionsTotal.WithLabels(advertised, "limit");
        AddedLatency = metrics.AddedLatency.WithLabels();
    }

    public HostPort Upstream { get; }
    public int ListenPort { get; }
    /// <summary>Normalized <c>host:port</c> clients use for this member (02 §3.2).</summary>
    public string Advertised { get; }
    public HelloRewriteContext RewriteContext { get; }

    /// <summary>Last time the member was reported by a monitor or forwarded hello (UTC ticks).</summary>
    public long LastSeenTicks;
    public long LastMonitorSuccessTicks;

    // Cached metric children: the forwarding path never looks up labels.
    public readonly Gauge OpenConnections;
    public readonly Counter BytesFromClient, BytesFromServer, FramesFromClient, FramesFromServer, InvalidFrames;
    public readonly Counter AcceptedOk, AcceptedUpstreamFailed, AcceptedTlsFailed, AcceptedLimit;
    public readonly Histogram AddedLatency;

    public override string ToString() => $"{Advertised} → {Upstream}";
}
