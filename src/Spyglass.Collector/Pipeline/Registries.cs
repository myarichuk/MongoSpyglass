using System.Collections.Concurrent;
using Spyglass.Collector.Model;

namespace Spyglass.Collector.Pipeline;

/// <summary>What the collector knows about one proxied connection, from its <c>conn</c> records.</summary>
public sealed class ConnectionInfo
{
    public required string ProxyId;
    public required long ConnSeq;
    public required string Member;
    public long? ServerConnectionId;
    public string? Client, App, Driver, DriverVersion, Exporter, Os, Platform;
    public bool Tls;
    public long OpenedTs;
    public long? ClosedTs;
    public string? CloseReason;
    public long BytesIn, BytesOut;
    /// <summary>Wall clock (Environment.TickCount64) when closed: entries are kept a while for late ops.</summary>
    public long ClosedAtTick;
}

/// <summary>
/// Shared by the ingest threads (writers) and the pipeline workers (readers). Not partition state: a
/// connection's <c>conn open</c> can precede the hello reply that gives it the server connection id
/// the ops are partitioned by.
/// </summary>
public sealed class ConnectionRegistry
{
    private readonly ConcurrentDictionary<(string, long), ConnectionInfo> _conns = new();
    private const long KeepClosedMs = 60_000;

    public void Apply(ConnRecord c)
    {
        var info = _conns.GetOrAdd((c.Src, c.ConnSeq), _ => new ConnectionInfo { ProxyId = c.Src, ConnSeq = c.ConnSeq, Member = c.Member, OpenedTs = c.Ts });
        lock (info)
        {
            info.ServerConnectionId ??= c.ServerConnectionId;
            info.Client ??= c.Client;
            info.App ??= c.App;
            info.Driver ??= c.Driver;
            info.DriverVersion ??= c.DriverVersion;
            info.Exporter ??= c.Exporter;
            info.Os ??= c.Os;
            info.Platform ??= c.Platform;
            info.Tls |= c.Tls;
            if (c.Event == "close")
            {
                info.ClosedTs = c.Ts;
                info.CloseReason = c.CloseReason;
                info.BytesIn = c.BytesIn;
                info.BytesOut = c.BytesOut;
                info.ClosedAtTick = Environment.TickCount64;
            }
        }
    }

    public ConnectionInfo? Get(string proxyId, long connSeq) => _conns.GetValueOrDefault((proxyId, connSeq));

    public IEnumerable<ConnectionInfo> All => _conns.Values;

    public int Sweep()
    {
        var now = Environment.TickCount64;
        var n = 0;
        foreach (var (k, v) in _conns)
            if (v.ClosedTs != null && now - v.ClosedAtTick > KeepClosedMs && _conns.TryRemove(k, out _))
                n++;
        return n;
    }
}

/// <summary>A proxy or exporter instance (03 §8 <c>/producers</c>).</summary>
public sealed class ProducerInfo
{
    public required string Src;
    public string Kind = "unknown";        // proxy | exporter name
    public string? Version;
    public int Schema;
    public string? Topology, SetName;
    public List<(string Member, string Upstream)> Members = [];
    public string? Compression, Values;
    public long FirstSeen, LastSeen;       // collector wall clock, µs
    public long Records, Rejected, ClockSkewed;
    public Dictionary<string, long> Dropped = [];
    public long SentRecords;
    /// <summary>The proxy's <c>capture.values</c> is not <c>shape</c>: valueHash can be computed (03 §4).</summary>
    public bool ValuesPreserved => Values != "shape";
}

public sealed class ProducerRegistry
{
    private readonly ConcurrentDictionary<string, ProducerInfo> _producers = new();

    public ProducerInfo Touch(string src, string? producerHeader, long nowUs)
    {
        var p = _producers.GetOrAdd(src, s => new ProducerInfo { Src = s, FirstSeen = nowUs });
        p.LastSeen = nowUs;
        if (producerHeader != null && p.Kind == "unknown") p.Kind = producerHeader;
        return p;
    }

    public ProducerInfo? Get(string src) => _producers.GetValueOrDefault(src);

    public IEnumerable<ProducerInfo> All => _producers.Values;

    public void Apply(ProxyRecord r)
    {
        var p = _producers.GetOrAdd(r.Src, s => new ProducerInfo { Src = s });
        lock (p)
        {
            p.Kind = "proxy";
            p.Version = r.Version;
            p.Schema = r.Schema;
            p.Topology = r.Topology;
            p.SetName = r.SetName;
            p.Members = r.Members;
            p.Compression = r.Compression;
            p.Values = r.Values;
        }
    }

    public void Apply(StatsRecord r)
    {
        var p = _producers.GetOrAdd(r.Src, s => new ProducerInfo { Src = s });
        lock (p)
        {
            p.Dropped = r.Dropped;
            p.SentRecords = r.SentRecords;
        }
    }
}
