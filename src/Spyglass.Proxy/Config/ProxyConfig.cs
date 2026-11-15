namespace Spyglass.Proxy.Config;

// Mirrors the schema in docs/spec/01-proxy.md §2. Defaults here are the spec defaults.

public enum TopologyKind { Auto, Standalone, ReplicaSet, Sharded }
public enum TriState { Auto, True, False }
public enum DownstreamTlsMode { None, Tls }
public enum CompressionMode { Strip, Opaque, Observe }
public enum ValueMode { Full, Truncated, Shape }

public sealed class ProxyConfig
{
    public UpstreamConfig Upstream { get; } = new();
    public ListenConfig Listen { get; } = new();
    public CompressionMode Compression { get; set; } = CompressionMode.Strip;
    public CaptureConfig Capture { get; } = new();
    public LimitsConfig Limits { get; } = new();
    public CollectorConfig Collector { get; } = new();
    public DiscoveryConfig Discovery { get; } = new();
    public MetricsConfig Metrics { get; } = new();
    public string LogLevel { get; set; } = "info";
    /// <summary>Keys a lower-precedence source set that a higher one replaced with another value (reported as startup warnings).</summary>
    public List<string> Overrides { get; } = [];
}

public sealed class UpstreamConfig
{
    public string? Uri { get; set; }
    public TopologyKind Topology { get; set; } = TopologyKind.Auto;
    public UpstreamTlsConfig Tls { get; } = new();
}

public sealed class UpstreamTlsConfig
{
    public TriState Enabled { get; set; } = TriState.Auto;
    public string? CaFile { get; set; }
    public bool AllowInvalidCertificates { get; set; }
    public bool AllowInvalidHostnames { get; set; }
}

public sealed class ListenConfig
{
    public string BindAddress { get; set; } = "127.0.0.1";
    public string AdvertiseHost { get; set; } = "localhost";
    public int BasePort { get; set; } = 27017;
    public int PortRangeStart { get; set; } = 27017;
    public int PortRangeEnd { get; set; } = 27099;
    public DownstreamTlsConfig Tls { get; } = new();
}

public sealed class DownstreamTlsConfig
{
    public DownstreamTlsMode Mode { get; set; } = DownstreamTlsMode.None;
    public string? CertFile { get; set; }
    public string? KeyFile { get; set; }
}

public sealed class CaptureConfig
{
    public int CommandBytes { get; set; } = 16384;
    public int ReplyBytes { get; set; }
    public ValueMode Values { get; set; } = ValueMode.Truncated;
    public int TruncateStringsAt { get; set; } = 64;
    public string RedactFieldPattern { get; set; } =
        "(?i)(passw(or)?d|passphrase|pwd|^pass$|secret|token|api[-_]?key|access[-_]?key|private[-_]?key|authorization|bearer|cookie|credential|ssn$|social[-_]?security|^card$|card[-_]?num|card[-_]?no(?![a-z])|credit[-_]?card|cv[vc]$|^pin$|^otp$)";
}

public sealed class LimitsConfig
{
    public int MaxClientConnections { get; set; } = 4096;
    public long ObserveQueueBytes { get; set; } = 64L * 1024 * 1024;
    public int MaxPendingPerConnection { get; set; } = 1024;
    /// <summary>A request with no reply for this long is dropped from the observer's pending table (<c>pending_evicted</c>); long enough for any operation.</summary>
    public int PendingMaxAgeSeconds { get; set; } = 6 * 3600;
    public int HelloRewriteMaxBytes { get; set; } = 1024 * 1024;
    /// <summary>Process-wide budget for hello replies buffered for rewriting (01 §13); over it, frames pass through unmodified. Hello requests draw on a separate budget of a quarter of this.</summary>
    public long HelloBufferBytes { get; set; } = 16L * 1024 * 1024;
    /// <summary>A hello frame being buffered for rewriting must complete within this (01 §13): a stalled peer is not waited on forever.</summary>
    public int HelloBufferTimeoutMs { get; set; } = 10_000;
    public int ReadBufferBytes { get; set; } = 64 * 1024;
}

public sealed class CollectorConfig
{
    public string? Endpoint { get; set; }
    public string? Token { get; set; }
    public int BatchMaxRecords { get; set; } = 2000;
    public int BatchMaxBytes { get; set; } = 1024 * 1024;
    public int FlushIntervalMs { get; set; } = 500;
    public long BufferBytes { get; set; } = 16L * 1024 * 1024;
}

public sealed class DiscoveryConfig
{
    public string? AdvertiseIngest { get; set; }
    public bool AdvertiseToken { get; set; } = true;
}

public sealed class MetricsConfig
{
    public string? Bind { get; set; } = "127.0.0.1:9187";
    public int MaxSeries { get; set; } = 5000;
}
