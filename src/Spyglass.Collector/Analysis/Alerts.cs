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
    public long FirstSeen, LastSeen;      // µs since epoch (event time: what the UI shows)
    /// <summary>
    /// Collector clock at the last occurrence (muted ones included): what the quiet period runs on. Event time comes from the
    /// producer's clock, which may lag by more than the quiet period (CA-5). 0 in rows from before: <see cref="LastSeen"/> stands in.
    /// </summary>
    public long LastHitUs;
    /// <summary>Collector clock when <see cref="Summary"/> last changed: a hot alert's summary is rebuilt at most once a second (CA-18).</summary>
    [System.Text.Json.Serialization.JsonIgnore] public long SummaryAtUs;
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
    private volatile Alert[] _snapshot = [];
    private bool _dirty;

    public Alert[] Snapshot() => _snapshot;

    /// <summary>The alert of <c>(rule, scope)</c>, for the caller to skip building a scope and a summary it will not need.</summary>
    public Alert? Find(string rule, string scopeKey) => _alerts.GetValueOrDefault((rule, scopeKey));

    /// <summary>
    /// Records an occurrence; null when muted. <paramref name="hitUs"/> is the collector's clock (the quiet period runs on it);
    /// <paramref name="ts"/> the event time.
    /// </summary>
    public AlertChange? Hit(string rule, string scopeKey, Dictionary<string, string?> scope, string summary, string? opId, long ts, long hitUs = 0, string? details = null)
    {
        if (hitUs == 0) hitUs = ts;
        if (_alerts.TryGetValue((rule, scopeKey), out var a))
        {
            a.LastHitUs = hitUs;
            a.LastSeen = Math.Max(a.LastSeen, ts);
            if (a.Muted) // silent, but the problem is still there: it must not resolve or expire under the mute (CA-21)
            {
                a.Occurrences++;
                if (!a.Open) { a.Open = true; a.ResolvedAt = null; }
                _dirty = true; // the occurrence count and times reach readers (and, through the caller, the store) while muted (FC-5)
                return null;
            }
            var reopened = !a.Open;
            a.Open = true;
            a.ResolvedAt = null;
            a.Occurrences++;
            if (!ReferenceEquals(a.Summary, summary)) { a.Summary = summary; a.SummaryAtUs = hitUs; }
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
            LastHitUs = hitUs,
            SummaryAtUs = hitUs,
            Occurrences = 1,
            Summary = summary,
            Details = details,
        };
        if (opId != null) a.SampleOpIds.Add(opId);
        _alerts[(rule, scopeKey)] = a;
        _dirty = true;
        return new AlertChange(a, true);
    }

    /// <summary>Resolves alerts quiet for their rule's period; returns those resolved.</summary>
    public List<Alert> ResolveQuiet(long nowUs)
    {
        var resolved = new List<Alert>();
        foreach (var a in _alerts.Values)
        {
            if (!a.Open || nowUs - (a.LastHitUs != 0 ? a.LastHitUs : a.LastSeen) < (long)QuietPeriod(a.Rule).TotalMicroseconds) continue;
            a.Open = false;
            a.ResolvedAt = nowUs;
            resolved.Add(a);
        }
        if (resolved.Count > 0) _dirty = true;
        return resolved;
    }

    public bool SetMuted(string id, bool muted) => SetMuted(id, muted, out _);

    public bool SetMuted(string id, bool muted, out Alert? alert)
    {
        var a = alert = _alerts.Values.FirstOrDefault(x => x.Id == id);
        if (a == null) return false;
        a.Muted = muted;
        _dirty = true;
        return true;
    }

    /// <summary>
    /// Drops resolved alerts older than the retention (03 §7: 30 d after resolve); returns the ids, for the store to delete.
    /// A muted alert is kept: dropping it would lose the mute (CA-21).
    /// </summary>
    public List<string>? Expire(long nowUs, TimeSpan retention)
    {
        var limit = (long)retention.TotalMicroseconds;
        List<(string, string)>? drop = null;
        foreach (var (k, a) in _alerts)
            if (!a.Muted && a.ResolvedAt is { } r && nowUs - r > limit) (drop ??= []).Add(k);
        if (drop == null) return null;
        var ids = new List<string>(drop.Count);
        foreach (var k in drop)
        {
            ids.Add(_alerts[k].Id);
            _alerts.Remove(k);
        }
        _dirty = true;
        return ids;
    }

    public IEnumerable<Alert> All => _alerts.Values;

    public void Restore(Alert a)
    {
        _alerts[(a.Rule, a.ScopeKey)] = a;
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
