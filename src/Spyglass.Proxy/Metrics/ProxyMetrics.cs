namespace Spyglass.Proxy.Metrics;

/// <summary>Self-telemetry families of 01 §12.</summary>
public sealed class ProxyMetrics
{
    public static readonly double[] AddedLatencyBuckets =
        [5e-6, 1e-5, 2.5e-5, 5e-5, 1e-4, 2.5e-4, 5e-4, 1e-3, 2.5e-3, 5e-3];

    public MetricsRegistry Registry { get; } = new();

    public readonly GaugeFamily Connections;
    public readonly CounterFamily ConnectionsTotal;
    public readonly CounterFamily Bytes;
    public readonly CounterFamily Frames;
    public readonly CounterFamily InvalidFrames;
    public readonly CounterFamily ObservationDropped;
    public readonly GaugeFamily ObserveQueueBytes;
    public readonly CounterFamily Rewrites;
    public readonly CounterFamily RewritesSkipped;
    public readonly CounterFamily UncorrelatedReplies;
    public readonly HistogramFamily AddedLatency;

    public ProxyMetrics()
    {
        Connections = Registry.Gauge("spyglass_proxy_connections", "Client connections by state.", "listener", "state");
        ConnectionsTotal = Registry.Counter("spyglass_proxy_connections_total", "Accepted client connections by result.", "listener", "result");
        Bytes = Registry.Counter("spyglass_proxy_bytes_total", "Bytes read from the source side of each direction.", "listener", "direction");
        Frames = Registry.Counter("spyglass_proxy_frames_total", "Wire frames forwarded.", "listener", "direction");
        InvalidFrames = Registry.Counter("spyglass_proxy_invalid_frames_total", "Connections closed because a frame could not be framed.", "listener");
        ObservationDropped = Registry.Counter("spyglass_proxy_observation_dropped_total", "Observation data dropped (I4).", "reason");
        ObserveQueueBytes = Registry.Gauge("spyglass_proxy_observe_queue_bytes", "Captured bytes waiting for the observer.");
        Rewrites = Registry.Counter("spyglass_proxy_rewrite_total", "Handshake rewrites applied.", "kind");
        RewritesSkipped = Registry.Counter("spyglass_proxy_rewrite_skipped_total", "Handshake rewrites skipped; original bytes forwarded.", "reason");
        UncorrelatedReplies = Registry.Counter("spyglass_proxy_uncorrelated_replies_total", "Replies whose responseTo matched no pending request.");
        AddedLatency = Registry.Histogram("spyglass_proxy_forward_added_latency_seconds",
            "Proxy residency of a read chunk that completed at least one frame: read completion to write completion.",
            AddedLatencyBuckets);
    }
}
