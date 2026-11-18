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
    /// <summary>Environment.TickCount64 of the last conn record or op on this connection: idle eviction (PLAN 1.2).</summary>
    public long LastSeenTick = Environment.TickCount64;
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
    private readonly long _idleMs;
    private readonly int _cap;
    private long _evicted;

    /// <param name="idle">
    /// A connection with no record for this long is dropped. Off by default (FC-6): <c>conn</c> is sent once at open and once at
    /// close (02 §4.2), so an idle pool's connection could never get its app and driver back; a dead proxy's connections go with
    /// <see cref="DropProxy"/> and a lost <c>close</c> is bounded by the cap (least recently seen first).
    /// </param>
    /// <param name="cap">Hard bound: past it the least recently seen go first.</param>
    public ConnectionRegistry(TimeSpan? idle = null, int cap = 200_000)
    {
        _idleMs = idle is { } i ? (long)i.TotalMilliseconds : long.MaxValue;
        _cap = cap;
    }

    public int Count => _conns.Count;
    /// <summary>Entries dropped for idleness or over the cap (never a normal close): /metrics.</summary>
    public long Evicted => Interlocked.Read(ref _evicted);

    public void Apply(ConnRecord c)
    {
        var info = _conns.GetOrAdd((c.Src, c.ConnSeq), _ => new ConnectionInfo { ProxyId = c.Src, ConnSeq = c.ConnSeq, Member = c.Member, OpenedTs = c.Ts });
        lock (info)
        {
            info.LastSeenTick = Environment.TickCount64;
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

    /// <summary>Looks a connection up for an op or cmd on it: that is activity, so it is touched.</summary>
    public ConnectionInfo? Get(string proxyId, long connSeq)
    {
        var info = _conns.GetValueOrDefault((proxyId, connSeq));
        if (info != null) info.LastSeenTick = Environment.TickCount64; // a racy plain write of a long: only ever approximately read
        return info;
    }

    /// <summary>The proxy instance is gone (producer expired, or restarted under a new id): its open connections can never close.</summary>
    public int DropProxy(string proxyId)
    {
        var n = 0;
        foreach (var (k, _) in _conns)
            if (k.Item1 == proxyId && _conns.TryRemove(k, out _)) n++;
        Interlocked.Add(ref _evicted, n);
        return n;
    }

    public IEnumerable<ConnectionInfo> All => _conns.Values;

    public int Sweep()
    {
        var now = Environment.TickCount64;
        var n = 0;
        var idle = 0;
        foreach (var (k, v) in _conns)
        {
            if (v.ClosedTs != null && now - v.ClosedAtTick > KeepClosedMs) { if (_conns.TryRemove(k, out _)) n++; }
            else if (now - v.LastSeenTick > _idleMs && _conns.TryRemove(k, out _)) { n++; idle++; }
        }
        var over = _conns.Count - _cap;
        if (over > 0)
            foreach (var (k, _) in _conns.OrderBy(e => e.Value.LastSeenTick).Take(over))
                if (_conns.TryRemove(k, out _)) { n++; idle++; }
        Interlocked.Add(ref _evicted, idle);
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
    public List<(string Member, string Upstream, string? Cert)> Members = [];
    /// <summary>The proxy's <c>config.tls</c> as raw JSON (02 §4.2); replaced by each proxy record.</summary>
    public string? Tls;
    public string? Compression, Values;
    public long FirstSeen, LastSeen;       // collector wall clock, µs
    public long Records, Rejected, ClockSkewed;
    public Dictionary<string, long> Dropped = [];
    public long SentRecords;
    // exporters (02 §4.3 hello)
    public string? Runtime, Driver, Host, App, AppVersion;
    public long? Pid;
    // 02 §6 per exporter: cmd records with a key, joined (in time or late), orphaned, in conflict
    public long Cmds, Joined, Orphans, Conflicts;
    /// <summary>Median of <c>cmd.ts − op.ts</c> over recent joins (analysis stage): the exporter's clock against the proxy's.</summary>
    public long? SkewUs;
    public double? JoinRate => Cmds > 0 ? (double)Joined / Cmds : null;
    /// <summary>The proxy's <c>capture.values</c> is not <c>shape</c>: valueHash can be computed (03 §4).</summary>
    public bool ValuesPreserved => Values != "shape";
}

public sealed class ProducerRegistry
{
    private readonly ConcurrentDictionary<string, ProducerInfo> _producers = new();
    private readonly long _idleUs;
    private readonly int _cap;
    private long _expired;

    /// <summary>Every process start is a new src (02 §4.1) and any token holder can mint more: idle ones expire, and the count is capped.</summary>
    public ProducerRegistry(TimeSpan? idle = null, int cap = 10_000)
    {
        _idleUs = (long)(idle ?? TimeSpan.FromHours(24)).TotalMicroseconds;
        _cap = cap;
    }

    public int Count => _producers.Count;
    public long Expired => Interlocked.Read(ref _expired);

    /// <summary>
    /// Drops producers unseen for the idle period, then the least recently seen past the cap. Returns the srcs removed
    /// so their per-src state elsewhere (connections, skew windows) can go with them.
    /// </summary>
    public List<string> Sweep(long nowUs)
    {
        var gone = new List<string>();
        foreach (var (k, p) in _producers)
            if (nowUs - Volatile.Read(ref p.LastSeen) > _idleUs && p.LastSeen != 0 && _producers.TryRemove(k, out _)) gone.Add(k);
        var over = _producers.Count - _cap;
        if (over > 0)
            foreach (var (k, _) in _producers.OrderBy(e => e.Value.LastSeen).Take(over))
                if (_producers.TryRemove(k, out _)) gone.Add(k);
        Interlocked.Add(ref _expired, gone.Count);
        return gone;
    }

    public ProducerInfo Touch(string src, string? producerHeader, long nowUs)
    {
        // static lambda + argument: a capturing lambda allocated its closure on every call (every record)
        var p = _producers.TryGetValue(src, out var found) ? found : _producers.GetOrAdd(src, static (s, now) => new ProducerInfo { Src = s, FirstSeen = now }, nowUs);
        if (p.LastSeen != nowUs) p.LastSeen = nowUs; // a shared line: not rewritten with the same value
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
            p.Tls = r.Tls;
        }
    }

    public void Apply(HelloRecord r)
    {
        var p = _producers.GetOrAdd(r.Src, s => new ProducerInfo { Src = s });
        lock (p)
        {
            p.Kind = r.Exporter;
            p.Version = r.Version;
            p.Schema = r.Schema;
            p.Runtime = r.Runtime;
            p.Driver = r.Driver;
            p.Host = r.Host;
            p.Pid = r.Pid;
            p.App = r.App;
            p.AppVersion = r.AppVersion;
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
