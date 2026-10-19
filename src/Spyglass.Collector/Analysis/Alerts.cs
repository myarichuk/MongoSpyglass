namespace Spyglass.Collector.Analysis;

public enum Severity { Info, Warning, Critical }

/// <summary>03 §5.1. One per <c>(rule, scope)</c>; mutated only on the analysis thread, snapshotted for readers.</summary>
public sealed class Alert
{
    public required string Id;
    public required string Rule;
    public required string ScopeKey;
    public required Dictionary<string, string?> Scope;
    public Severity Severity;
    public long FirstSeen, LastSeen;      // µs since epoch (event time)
    public long Occurrences;
    public List<string> SampleOpIds = [];
    public string Summary = "";
    public string? Details;
    public bool Open = true;
    public long? ResolvedAt;
    public bool Muted;

    public Alert Snapshot()
    {
        var a = (Alert)MemberwiseClone();
        a.SampleOpIds = [.. SampleOpIds];
        a.Scope = new Dictionary<string, string?>(Scope);
        return a;
    }
}

/// <summary>What the analysis thread reports when an alert opens, reopens or updates.</summary>
public readonly record struct AlertChange(Alert Alert, bool Opened);

/// <summary>
/// Alert lifecycle (03 §5.1). Single-threaded (the analysis stage); readers get snapshots through
/// <see cref="Snapshot"/>, refreshed on change.
/// </summary>
public sealed class AlertBook
{
    public const int MaxSamples = 10;

    /// <summary>Per-rule quiet period before an open alert resolves.</summary>
    public static TimeSpan QuietPeriod(string rule) => rule switch
    {
        "unacknowledged-writes" => TimeSpan.FromHours(1),
        "cursor-abandoned" => TimeSpan.FromMinutes(30),
        _ => TimeSpan.FromMinutes(10),
    };

    public static Severity DefaultSeverity(string rule) => rule switch
    {
        "unacknowledged-writes" => Severity.Info,
        "error-burst" or "n-plus-one" or "pool-starvation" => Severity.Critical,
        _ => Severity.Warning,
    };

    private readonly Dictionary<(string Rule, string Scope), Alert> _alerts = [];
    private readonly HashSet<(string Rule, string Scope)> _muted = [];
    private volatile Alert[] _snapshot = [];
    private bool _dirty;

    public Alert[] Snapshot() => _snapshot;

    public bool IsMuted(string rule, string scopeKey) => _muted.Contains((rule, scopeKey));

    /// <summary>Records an occurrence; null when muted.</summary>
    public AlertChange? Hit(string rule, string scopeKey, Dictionary<string, string?> scope, string summary, string? opId, long ts, string? details = null)
    {
        if (_alerts.TryGetValue((rule, scopeKey), out var a))
        {
            if (a.Muted) return null;
            var reopened = !a.Open;
            a.Open = true;
            a.ResolvedAt = null;
            a.Occurrences++;
            a.LastSeen = Math.Max(a.LastSeen, ts);
            a.Summary = summary;
            if (details != null) a.Details = details;
            if (opId != null && a.SampleOpIds.Count < MaxSamples) a.SampleOpIds.Add(opId);
            _dirty = true;
            return new AlertChange(a, reopened);
        }
        a = new Alert
        {
            Id = Model.Ulid.New(ts / 1000),
            Rule = rule,
            ScopeKey = scopeKey,
            Scope = scope,
            Severity = DefaultSeverity(rule),
            FirstSeen = ts,
            LastSeen = ts,
            Occurrences = 1,
            Summary = summary,
            Details = details,
            Muted = _muted.Contains((rule, scopeKey)),
        };
        if (opId != null) a.SampleOpIds.Add(opId);
        _alerts[(rule, scopeKey)] = a;
        _dirty = true;
        return a.Muted ? null : new AlertChange(a, true);
    }

    /// <summary>Resolves alerts quiet for their rule's period; returns those resolved.</summary>
    public List<Alert> ResolveQuiet(long nowUs)
    {
        var resolved = new List<Alert>();
        foreach (var a in _alerts.Values)
        {
            if (!a.Open || nowUs - a.LastSeen < (long)QuietPeriod(a.Rule).TotalMicroseconds) continue;
            a.Open = false;
            a.ResolvedAt = nowUs;
            resolved.Add(a);
        }
        if (resolved.Count > 0) _dirty = true;
        return resolved;
    }

    public bool SetMuted(string id, bool muted)
    {
        var a = _alerts.Values.FirstOrDefault(x => x.Id == id);
        if (a == null) return false;
        a.Muted = muted;
        if (muted) _muted.Add((a.Rule, a.ScopeKey));
        else _muted.Remove((a.Rule, a.ScopeKey));
        _dirty = true;
        return true;
    }

    /// <summary>Drops resolved alerts older than the retention (03 §7: 30 d after resolve).</summary>
    public void Expire(long nowUs, TimeSpan retention)
    {
        foreach (var k in _alerts.Where(kv => kv.Value.ResolvedAt is { } r && nowUs - r > (long)retention.TotalMicroseconds).Select(kv => kv.Key).ToList())
            _alerts.Remove(k);
        _dirty = true;
    }

    public IEnumerable<Alert> All => _alerts.Values;

    public void Restore(Alert a)
    {
        _alerts[(a.Rule, a.ScopeKey)] = a;
        if (a.Muted) _muted.Add((a.Rule, a.ScopeKey));
        _dirty = true;
    }

    /// <summary>Called by the analysis thread after a batch: republishes the snapshot if anything changed.</summary>
    public void Publish()
    {
        if (!_dirty) return;
        _dirty = false;
        _snapshot = _alerts.Values.Select(a => a.Snapshot()).ToArray();
    }
}
