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
        "(?i)^(pass(word)?|pwd|secret|token|api[-_]?key|authorization|cookie|ssn|card(number)?)$";
}

public sealed class LimitsConfig
{
    public int MaxClientConnections { get; set; } = 4096;
    public long ObserveQueueBytes { get; set; } = 64L * 1024 * 1024;
    public int MaxPendingPerConnection { get; set; } = 1024;
    public int HelloRewriteMaxBytes { get; set; } = 1024 * 1024;
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
