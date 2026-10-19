using Spyglass.Proxy.Metrics;

namespace Spyglass.Proxy.Observation;

/// <summary>
/// Open server cursors keyed by <c>(member, cursorId)</c>, not by connection: a getMore may arrive on any
/// pooled connection (03 §5.4). Feeds <c>spyglass_cursors_open</c>. Entries idle longer than the server's
/// default cursor timeout (10 min) are assumed timed out.
/// </summary>
internal sealed class CursorRegistry
{
    private static readonly long IdleTicks = TimeSpan.FromMinutes(10).Ticks;

    private readonly object _gate = new();
    private readonly Dictionary<(string Member, long Id), Entry> _open = new();
    private readonly CommandMetrics _metrics;
    private long _lastSweep = DateTime.UtcNow.Ticks;

    private sealed class Entry
    {
        public required Gauge Gauge;
        public long LastUsed;
    }

    public CursorRegistry(CommandMetrics metrics) => _metrics = metrics;

    public int Count
    {
        get { lock (_gate) return _open.Count; }
    }

    public void Opened(string member, long id, string db, string? coll)
    {
        if (id == 0) return;
        lock (_gate)
        {
            if (_open.ContainsKey((member, id))) return;
            var g = _metrics.CursorsOpen.WithLabels(db, coll ?? "");
            g.Inc();
            _open[(member, id)] = new Entry { Gauge = g, LastUsed = DateTime.UtcNow.Ticks };
        }
        SweepMaybe();
    }

    public void Touched(string member, long id)
    {
        lock (_gate)
            if (_open.TryGetValue((member, id), out var e))
                e.LastUsed = DateTime.UtcNow.Ticks;
    }

    public void Closed(string member, long id)
    {
        lock (_gate)
            if (_open.Remove((member, id), out var e))
                e.Gauge.Dec();
    }

    private void SweepMaybe()
    {
        var now = DateTime.UtcNow.Ticks;
        if (now - Volatile.Read(ref _lastSweep) < TimeSpan.TicksPerMinute) return;
        Volatile.Write(ref _lastSweep, now);
        lock (_gate)
        {
            foreach (var (k, e) in _open.ToArray())
            {
                if (now - e.LastUsed <= IdleTicks) continue;
                _open.Remove(k);
                e.Gauge.Dec();
            }
        }
    }
}
