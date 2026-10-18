using Spyglass.Proxy.Config;
using Spyglass.Proxy.Diagnostics;
using Spyglass.Proxy.Forwarding;
using Spyglass.Proxy.Wire;

namespace Spyglass.Proxy.Topology;

/// <summary>
/// Member discovery, listener allocation and the R1 address map (01 §5).
/// </summary>
public sealed class TopologyManager : IAddressMap
{
    private static readonly TimeSpan MonitorInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan AbsentListenerTtl = TimeSpan.FromMinutes(10);

    private readonly ProxyServer _server;
    private readonly ProxyConfig _config;
    private readonly object _gate = new();
    private readonly Dictionary<HostPort, Member> _byUpstream = new();
    private readonly List<Member> _members = [];
    private readonly HashSet<int> _usedPorts = [];
    private readonly Dictionary<Member, CancellationTokenSource> _monitors = new();

    public TopologyKind Kind { get; private set; } = TopologyKind.Auto;
    public string? SetName { get; private set; }

    internal TopologyManager(ProxyServer server)
    {
        _server = server;
        _config = server.Config;
    }

    public IReadOnlyList<Member> Members
    {
        get { lock (_gate) return _members.ToArray(); }
    }

    /// <summary>01 §5.1 steps 1–4. Throws <see cref="ConfigException"/> on a topology that cannot be served.</summary>
    internal async Task DiscoverAsync(IReadOnlyList<HostPort> seeds, CancellationToken ct)
    {
        var failures = new List<string>();
        var replies = new List<(HostPort Seed, HelloReplyFacts Facts)>();
        foreach (var seed in seeds)
        {
            await using var hc = new HelloClient(_server.Upstream, seed);
            try
            {
                replies.Add((seed, await hc.HelloAsync(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false)));
            }
            catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                failures.Add($"{seed}: {e.Message}");
            }
        }

        if (replies.Count == 0)
            throw new ConfigException("no upstream seed answered hello: " + string.Join("; ", failures));
        foreach (var f in failures)
            Log.Warn($"seed {f}");

        var first = replies[0].Facts;
        var detected = first.IsMongos ? TopologyKind.Sharded
            : first.SetName != null ? TopologyKind.ReplicaSet
            : TopologyKind.Standalone;

        if (_config.Upstream.Topology != TopologyKind.Auto && _config.Upstream.Topology != detected)
            throw new ConfigException($"'upstream.topology' is {_config.Upstream.Topology} but the upstream is {detected}");

        Kind = detected;
        SetName = first.SetName;

        List<HostPort> members;
        switch (detected)
        {
            case TopologyKind.Sharded:
                // Every seed is a mongos. Seeds that didn't answer keep their listener (they may come up later);
                // a seed that answered as something else is a misconfiguration.
                foreach (var (seed, f) in replies.Where(r => !r.Facts.IsMongos))
                    throw new ConfigException($"seed {seed} is not a mongos ({(f.SetName != null ? "replica set " + f.SetName : "standalone")}) but {replies[0].Seed} is");
                members = seeds.Distinct().ToList();
                break;
            case TopologyKind.ReplicaSet:
                // The members are what the replica set calls itself (hosts/passives/arbiters), not the seed spellings:
                // `me` in each member's hello must match a listener's upstream address (01 §5.3).
                members = first.Members.Distinct().ToList();
                if (members.Count == 0)
                    throw new ConfigException($"replica set {SetName} reported no hosts (is it initiated?)");
                foreach (var (_, f) in replies.Skip(1))
                    if (f.SetName != SetName)
                        throw new ConfigException($"seeds belong to different replica sets: {SetName} and {f.SetName}");
                break;
            default:
                if (replies.Count > 1 || seeds.Count > 1)
                    Log.Warn("several seeds given for a standalone upstream; using the first that answered");
                members = [replies[0].Seed];
                break;
        }

        lock (_gate)
        {
            for (var i = 0; i < members.Count; i++)
            {
                var port = _config.Listen.BasePort + i;
                if (port > _config.Listen.PortRangeEnd)
                    throw new ConfigException($"{members.Count} members do not fit in 'listen.portRange' from 'listen.basePort' {_config.Listen.BasePort}");
                AddMemberLocked(members[i], port, startMonitor: false);
            }
        }

        Log.Info($"upstream is {Describe()}");
    }

    public string Describe()
    {
        lock (_gate)
            return $"{Kind}{(SetName != null ? " " + SetName : "")}: " + string.Join(", ", _members.Select(m => m.ToString()));
    }

    // ---- IAddressMap (R1) ------------------------------------------------------------------

    public string? Advertise(HostPort upstream)
    {
        lock (_gate)
        {
            if (_byUpstream.TryGetValue(upstream, out var m))
            {
                m.LastSeenTicks = DateTime.UtcNow.Ticks;
                return m.Advertised;
            }
            if (Kind != TopologyKind.ReplicaSet)
                return null;
            var port = NextFreePortLocked();
            if (port < 0)
            {
                Log.Error($"no free port in 'listen.portRange' for new member {upstream}; clients will see its real address");
                return null;
            }
            try
            {
                return AddMemberLocked(upstream, port, startMonitor: true).Advertised;
            }
            catch (Exception e)
            {
                Log.Error($"cannot open listener for new member {upstream} on port {port}: {e.Message}");
                _usedPorts.Add(port); // don't retry a port we failed to bind
                return null;
            }
        }
    }

    internal void OnForwardedHello(Member via, HelloReplyFacts facts)
    {
        via.LastSeenTicks = DateTime.UtcNow.Ticks;
        // Members in a rewritten reply were mapped (and allocated) during R1; nothing else to do.
    }

    private int NextFreePortLocked()
    {
        for (var p = _config.Listen.PortRangeStart; p <= _config.Listen.PortRangeEnd; p++)
            if (!_usedPorts.Contains(p))
                return p;
        return -1;
    }

    private Member AddMemberLocked(HostPort upstream, int port, bool startMonitor)
    {
        var advertised = new HostPort(HostPort.NormalizeHost(_config.Listen.AdvertiseHost), port).ToString();
        var member = new Member(upstream, port, advertised, new HelloRewriteContext
        {
            RewriteTopology = Kind == TopologyKind.ReplicaSet,
            Map = this,
            Self = advertised,
            SpyglassDocument = _server.BuildSpyglassDocument(advertised),
        }, _server.Metrics)
        {
            LastSeenTicks = DateTime.UtcNow.Ticks
        };

        _server.StartListener(member); // binds synchronously; throws if the port is taken
        _usedPorts.Add(port);
        _byUpstream[upstream] = member;
        _members.Add(member);
        Log.Info($"listener {advertised} → {upstream}");
        if (startMonitor)
            StartMonitorLocked(member);
        return member;
    }

    // ---- monitors (01 §5.1 step 5) ---------------------------------------------------------

    internal void StartMonitors()
    {
        lock (_gate)
            foreach (var m in _members)
                StartMonitorLocked(m);
    }

    private void StartMonitorLocked(Member m)
    {
        if (_monitors.ContainsKey(m)) return;
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_server.ShutdownToken);
        _monitors[m] = cts;
        _ = Task.Run(() => MonitorAsync(m, cts.Token));
    }

    private async Task MonitorAsync(Member member, CancellationToken ct)
    {
        await using var hc = new HelloClient(_server.Upstream, member.Upstream);
        var failing = false;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var facts = await hc.HelloAsync(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
                member.LastMonitorSuccessTicks = DateTime.UtcNow.Ticks;
                if (failing)
                    Log.Info($"monitor {member.Upstream}: reachable again");
                failing = false;

                if (Kind == TopologyKind.ReplicaSet)
                {
                    foreach (var hp in facts.Members)
                        Advertise(hp); // allocates listeners for members added with rs.add
                    if (facts.SetName != null && facts.SetName != SetName)
                        Log.Error($"monitor {member.Upstream}: reports replica set {facts.SetName}, expected {SetName}");
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                if (!failing)
                    Log.Warn($"monitor {member.Upstream}: {e.Message}");
                failing = true;
            }

            SweepAbsent();
            try { await Task.Delay(MonitorInterval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Closes listeners of members absent for 10 minutes that have no connections left.</summary>
    private void SweepAbsent()
    {
        if (Kind != TopologyKind.ReplicaSet) return;
        var cutoff = DateTime.UtcNow.Ticks - AbsentListenerTtl.Ticks;
        lock (_gate)
        {
            foreach (var m in _members.ToArray())
            {
                if (Volatile.Read(ref m.LastSeenTicks) >= cutoff || m.OpenConnections.Value > 0)
                    continue;
                Log.Info($"listener {m.Advertised}: member {m.Upstream} absent for {AbsentListenerTtl.TotalMinutes} min; closing");
                _server.StopListener(m);
                if (_monitors.Remove(m, out var cts)) cts.Cancel();
                _members.Remove(m);
                _byUpstream.Remove(m.Upstream);
                _usedPorts.Remove(m.ListenPort);
            }
        }
    }

    public bool AnyMonitorSucceededWithin(TimeSpan window)
    {
        var cutoff = DateTime.UtcNow.Ticks - window.Ticks;
        lock (_gate)
            return _members.Any(m => Volatile.Read(ref m.LastMonitorSuccessTicks) >= cutoff);
    }
}
