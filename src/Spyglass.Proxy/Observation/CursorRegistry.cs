using Spyglass.Proxy.Metrics;

namespace Spyglass.Proxy.Observation;

/// <summary>
/// Open server cursors keyed by <c>(member, cursorId)</c>, not by connection: a getMore may arrive on any
/// pooled connection (03 §5.4). Feeds <c>spyglass_cursors_open</c>. Entries idle longer than the server's
/// default cursor timeout (10 min) are assumed timed out.
/// </summary>
internal sealed class CursorRegistry
{
    private const long IdleMs = 10 * 60_000, SweepEveryMs = 60_000;
    private const int MaxGauges = 4096;

    private readonly object _gate = new();
    private readonly Dictionary<(string Member, long Id), Entry> _open = new();
    /// <summary>(database, collection) → gauge; bounded, because the registry sees every cursor of every tenant.</summary>
    private readonly Dictionary<(string, string), Gauge> _gauges = new();
    private readonly CommandMetrics _metrics;
    private long _lastSweep = Environment.TickCount64;

    private sealed class Entry
    {
        public required Gauge Gauge;
        public long LastUsed; // Environment.TickCount64: immune to wall-clock steps
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
            var key = (db, coll ?? "");
            if (!_gauges.TryGetValue(key, out var g))
            {
                if (_gauges.Count >= MaxGauges) _gauges.Clear(); // the families fold past maxSeries; this is only a lookup cache
                g = _gauges[key] = _metrics.Series(_metrics.CursorsOpen, db, coll ?? "");
            }
            g.Inc();
            _open[(member, id)] = new Entry { Gauge = g, LastUsed = Environment.TickCount64 };
        }
        SweepMaybe();
    }

    public void Touched(string member, long id)
    {
        lock (_gate)
            if (_open.TryGetValue((member, id), out var e))
                e.LastUsed = Environment.TickCount64;
        SweepMaybe();
    }

    public void Closed(string member, long id)
    {
        lock (_gate)
            if (_open.Remove((member, id), out var e))
                e.Gauge.Dec();
        SweepMaybe();
    }

    /// <summary>Expires idle entries at most once a minute; called on every registry event and before each scrape.</summary>
    public void SweepMaybe()
    {
        var now = Environment.TickCount64;
        if (now - Volatile.Read(ref _lastSweep) < SweepEveryMs) return;
        Volatile.Write(ref _lastSweep, now);
        lock (_gate)
        {
            foreach (var (k, e) in _open.ToArray())
            {
                if (now - e.LastUsed <= IdleMs) continue;
                _open.Remove(k);
                e.Gauge.Dec();
            }
        }
    }
}
