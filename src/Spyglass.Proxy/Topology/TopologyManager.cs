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
    /// <summary>Test hooks: instance-wide intervals, read on every monitor iteration.</summary>
    internal TimeSpan MonitorInterval = TimeSpan.FromSeconds(10);
    internal TimeSpan AbsentListenerTtl = TimeSpan.FromMinutes(10);

    private readonly ProxyServer _server;
    private readonly ProxyConfig _config;
    private readonly object _gate = new();
    private readonly Dictionary<HostPort, Member> _byUpstream = new();
    // Copy-on-write view of _byUpstream: the forwarding path's hits (every host of every hello reply) never take _gate (PW-12).
    private volatile Dictionary<HostPort, Member> _snapshot = new();
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

        // `replicaSet` from the URI or SRV TXT must name the set we found (01 §5.1)
        if (_server.Uri.ReplicaSet is { } wanted && wanted != first.SetName)
            throw new ConfigException($"'upstream.uri' says replicaSet={wanted} but the upstream is {(first.SetName != null ? "replica set " + first.SetName : detected.ToString().ToLowerInvariant())}");

        // directConnection=true pins the proxy to the one member it was given: no member discovery, no R1 (01 §5.1)
        if (detected == TopologyKind.ReplicaSet && _server.Uri.DirectConnection)
        {
            detected = TopologyKind.Standalone;
            Log.Info("directConnection=true: proxying the one seed member only; replica-set members are not discovered");
        }

        Kind = detected;
        SetName = detected == TopologyKind.ReplicaSet ? first.SetName : null;

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
        if (_snapshot.TryGetValue(upstream, out var m))
        {
            Volatile.Write(ref m.LastSeenTicks, DateTime.UtcNow.Ticks);
            return m.Advertised;
        }
        return Kind == TopologyKind.ReplicaSet ? AdvertiseNew(upstream) : null;
    }

    private string? AdvertiseNew(HostPort upstream)
    {
        Member member;
        lock (_gate)
        {
            if (_byUpstream.TryGetValue(upstream, out var existing))
                return existing.Advertised;
            var port = NextFreePortLocked();
            if (port < 0)
            {
                Log.Error($"no free port in 'listen.portRange' for new member {upstream}; clients will see its real address");
                return null;
            }
            member = CreateMemberLocked(upstream, port);
        }

        // Bind outside the lock: socket syscalls must not stall other forwarding threads on _gate (PW-12).
        try
        {
            _server.StartListener(member);
        }
        catch (Exception e)
        {
            Log.Error($"cannot open listener for new member {upstream} on port {member.ListenPort}: {e.Message}");
            lock (_gate) // keep the port reserved: don't retry a port we failed to bind
            {
                _members.Remove(member);
                _byUpstream.Remove(upstream);
                _snapshot = new Dictionary<HostPort, Member>(_byUpstream);
            }
            return null;
        }
        Log.Info($"listener {member.Advertised} → {upstream}");
        lock (_gate)
            if (_byUpstream.TryGetValue(upstream, out var still) && still == member)
                StartMonitorLocked(member);
        return member.Advertised;
    }

    internal void OnForwardedHello(Member via, HelloReplyFacts facts)
    {
        Volatile.Write(ref via.LastSeenTicks, DateTime.UtcNow.Ticks);
        // Members of a rewritten reply were mapped (and allocated) during R1; the pump advertises those of a reply it skipped.
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
        var member = CreateMemberLocked(upstream, port);
        try
        {
            _server.StartListener(member); // binds synchronously; throws if the port is taken (startup only)
        }
        catch
        {
            _members.Remove(member);
            _byUpstream.Remove(upstream);
            _snapshot = new Dictionary<HostPort, Member>(_byUpstream);
            throw;
        }
        Log.Info($"listener {member.Advertised} → {upstream}");
        if (startMonitor)
            StartMonitorLocked(member);
        return member;
    }

    /// <summary>Registers a member and reserves its port; the caller binds its listener.</summary>
    private Member CreateMemberLocked(HostPort upstream, int port)
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
        _usedPorts.Add(port);
        _byUpstream[upstream] = member;
        _members.Add(member);
        _snapshot = new Dictionary<HostPort, Member>(_byUpstream);
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
                    // "Absent" means missing from a *successful* view of the set: after a total outage nobody
                    // has been refreshed, so sweeping on failure would tear everything down (PW-1).
                    SweepAbsent();
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

            try { await Task.Delay(MonitorInterval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Closes listeners of members absent for 10 minutes that have no connections left.
    /// Called only after a member answered hello; never removes the last member, so the set stays rediscoverable.</summary>
    internal void SweepAbsent()
    {
        if (Kind != TopologyKind.ReplicaSet) return;
        var cutoff = DateTime.UtcNow.Ticks - AbsentListenerTtl.Ticks;
        List<(Member Member, CancellationTokenSource? Monitor)>? removed = null;
        lock (_gate)
        {
            foreach (var m in _members.ToArray())
            {
                if (_members.Count <= 1) break;
                if (Volatile.Read(ref m.LastSeenTicks) >= cutoff || m.OpenConnections.Value > 0)
                    continue;
                _monitors.Remove(m, out var cts);
                _members.Remove(m);
                _byUpstream.Remove(m.Upstream);
                _usedPorts.Remove(m.ListenPort);
                (removed ??= []).Add((m, cts));
            }
            if (removed != null)
                _snapshot = new Dictionary<HostPort, Member>(_byUpstream);
        }
        if (removed == null) return;
        // sockets and tokens are torn down outside the lock
        foreach (var (m, cts) in removed)
        {
            Log.Info($"listener {m.Advertised}: member {m.Upstream} absent for {AbsentListenerTtl.TotalMinutes} min; closing");
            _server.StopListener(m);
            cts?.Cancel();
        }
    }

    public bool AnyMonitorSucceededWithin(TimeSpan window)
    {
        var cutoff = DateTime.UtcNow.Ticks - window.Ticks;
        lock (_gate)
            return _members.Any(m => Volatile.Read(ref m.LastMonitorSuccessTicks) >= cutoff);
    }
}
