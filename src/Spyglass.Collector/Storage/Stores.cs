using System.Collections.Concurrent;
using Spyglass.Collector.Analysis;
using Spyglass.Collector.Model;

namespace Spyglass.Collector.Storage;

/// <summary>03 §8 <c>GET /ops</c> filters. Newest first; <see cref="Cursor"/> is the last id of the previous page.</summary>
public sealed record OpQuery
{
    public long? From, To;              // µs since epoch
    public string? App, Coll, Command, Shape, Status, Uow;
    public long? MinLatencyUs;
    public int Limit = 100;
    public string? Cursor;
}

/// <summary>03 §7. Written by one writer thread; read concurrently by the API.</summary>
public interface IOperationStore
{
    void Append(IReadOnlyList<Operation> ops);
    Operation? Get(string id);
    List<Operation> Query(OpQuery q);
    /// <summary>Changes to stored ops, applied in order: late context, further stream replies, alerts raised later.</summary>
    void Update(IReadOnlyList<OpUpdate> updates);
    /// <summary>
    /// Retention (03 §7): older than <paramref name="olderThanUs"/>, then oldest first until under <paramref name="maxBytes"/>.
    /// In chunks: <paramref name="progress"/> runs between them, and other writers get the store in between.
    /// </summary>
    int Prune(long olderThanUs, long maxBytes, Action? progress = null);
    long Count { get; }
    /// <summary>
    /// Runs <paramref name="work"/> (this store's Append/Update/callsite calls) as one atomic write, so a failure leaves none
    /// of it applied and the caller can retry the lot. Stores without transactions just run it (their writes are idempotent).
    /// </summary>
    void Transact(Action work) => work();
}

/// <summary>A change to a stored op. Null fields are left as they are.</summary>
public sealed record OpUpdate(string Id, int? ReplyCount = null, IReadOnlyList<string>? Alerts = null, CmdRecord? Ctx = null);

/// <summary>03 §7 callsites: frames by hash, kept until unused for 30 days.</summary>
public interface ICallsiteStore
{
    void Upsert(IReadOnlyList<CallsiteRecord> callsites, long nowUs);
    /// <summary>Marks callsites as in use (joined ops reference them).</summary>
    void Touch(IReadOnlyCollection<string> hashes, long nowUs);
    CallsiteRecord? Callsite(string hash);
    int PruneCallsites(long unusedSinceUs);
}

public interface IAggregateStore
{
    /// <summary>Adds bucket deltas into the stored buckets.</summary>
    void Merge(IReadOnlyList<MinuteBucket> deltas);
    void UpsertShapes(IReadOnlyList<ShapeInfo> shapes);
    List<MinuteBucket> Buckets(long fromMinute, long toMinute, string? shape = null);
    ShapeInfo? Shape(string hash);
    List<ShapeInfo> Shapes();
    /// <summary>Deletes shapes whose <c>LastSeen</c> is before <paramref name="lastSeenBeforeUs"/> (03 §7: unused for as long as hourly aggregates are kept).</summary>
    int PruneShapes(long lastSeenBeforeUs);
    /// <summary>03 §7: minute buckets roll up to hourly after 48 h; hourly kept 30 d.</summary>
    void Rollup(long nowUs);
}

public interface IAlertStore
{
    void Upsert(IReadOnlyList<Alert> alerts);
    List<Alert> All();
    /// <summary>Deletes alerts (03 §7: 30 d after resolve).</summary>
    void Delete(IReadOnlyCollection<string> ids);
}

/// <summary>
/// Everything in memory: tests and <c>--data :memory:</c>. Bounded where it grows with traffic: ops by count and (in
/// <see cref="Prune"/>) by the size bound, minute buckets by rollup into hours and 30 d retention, callsites by a cap.
/// Shapes and alerts are as many as the analysis stage keeps (its own retention), not unbounded streams.
/// </summary>
public sealed class MemoryStore : IOperationStore, IAggregateStore, IAlertStore, ICallsiteStore
{
    private readonly ConcurrentDictionary<string, (CallsiteRecord Record, long LastUsed)> _callsites = new();
    private readonly int _maxOps;
    private const int MaxCallsites = 50_000, RowOverhead = 256;
    private long _bytes;
    private readonly ConcurrentDictionary<(string, long), MinuteBucket> _hours = new();
    private readonly ConcurrentDictionary<string, Operation> _ops = new();
    private readonly ConcurrentQueue<string> _order = new();
    private readonly ConcurrentDictionary<(string, long), MinuteBucket> _buckets = new();
    private readonly ConcurrentDictionary<string, ShapeInfo> _shapes = new();
    private readonly ConcurrentDictionary<string, Alert> _alerts = new();

    public MemoryStore(int maxOps = 200_000) => _maxOps = maxOps;

    public long Count => _ops.Count;

    public void Append(IReadOnlyList<Operation> ops)
    {
        foreach (var op in ops)
        {
            _ops[op.Id] = op;
            _order.Enqueue(op.Id);
            Interlocked.Add(ref _bytes, Size(op));
        }
        while (_ops.Count > _maxOps && _order.TryDequeue(out var old))
            if (_ops.TryRemove(old, out var gone)) Interlocked.Add(ref _bytes, -Size(gone));
    }

    private static long Size(Operation op) => (op.Op.Raw?.Length ?? 0) + RowOverhead;

    public Operation? Get(string id) => _ops.GetValueOrDefault(id);

    public List<Operation> Query(OpQuery q) =>
        _ops.Values.Where(o => Matches(o, q)).OrderByDescending(o => o.Id, StringComparer.Ordinal).Take(q.Limit).ToList();

    internal static bool Matches(Operation o, OpQuery q) =>
        (q.From is not { } f || o.Ts >= f) && (q.To is not { } t || o.Ts <= t) &&
        (q.App == null || o.App == q.App) && (q.Coll == null || o.Op.Coll == q.Coll) &&
        (q.Command == null || o.Op.Command == q.Command) && (q.Shape == null || o.ShapeHash == q.Shape) &&
        (q.Status == null || o.Op.Status == q.Status) && (q.Uow == null || o.Ctx?.UowId == q.Uow) &&
        (q.MinLatencyUs is not { } m || o.ServerLatencyUs >= m) &&
        (q.Cursor == null || string.CompareOrdinal(o.Id, q.Cursor) < 0);

    public void Update(IReadOnlyList<OpUpdate> updates)
    {
        foreach (var u in updates)
        {
            if (!_ops.TryGetValue(u.Id, out var op)) continue;
            if (u.ReplyCount is { } rc) op.ReplyCount = rc;
            if (u.Alerts != null) op.Alerts = [.. u.Alerts];
            if (u.Ctx != null) Volatile.Write(ref op.Ctx, u.Ctx);
        }
    }

    public void Upsert(IReadOnlyList<CallsiteRecord> callsites, long nowUs)
    {
        foreach (var c in callsites) _callsites[c.Callsite] = (c, nowUs);
        if (_callsites.Count > MaxCallsites) // over the cap: least recently used first
            foreach (var (k, _) in _callsites.OrderBy(e => e.Value.LastUsed).Take(_callsites.Count - MaxCallsites))
                _callsites.TryRemove(k, out _);
    }

    public void Touch(IReadOnlyCollection<string> hashes, long nowUs)
    {
        foreach (var h in hashes)
            if (_callsites.TryGetValue(h, out var e)) _callsites[h] = (e.Record, nowUs);
    }

    public CallsiteRecord? Callsite(string hash) => _callsites.TryGetValue(hash, out var e) ? e.Record : null;

    public int PruneCallsites(long unusedSinceUs)
    {
        var n = 0;
        foreach (var (k, v) in _callsites)
            if (v.LastUsed < unusedSinceUs && _callsites.TryRemove(k, out _)) n++;
        return n;
    }

    public int Prune(long olderThanUs, long maxBytes, Action? progress = null)
    {
        var n = 0;
        while (_order.TryPeek(out var id) && (!_ops.TryGetValue(id, out var op) || op.Ts < olderThanUs) && _order.TryDequeue(out _))
            if (_ops.TryRemove(id, out var gone)) { n++; Interlocked.Add(ref _bytes, -Size(gone)); }
        while (Interlocked.Read(ref _bytes) > maxBytes && _order.TryDequeue(out var oldest)) // the size bound: oldest first
            if (_ops.TryRemove(oldest, out var g)) { n++; Interlocked.Add(ref _bytes, -Size(g)); }
        return n;
    }

    public void Merge(IReadOnlyList<MinuteBucket> deltas)
    {
        foreach (var d in deltas)
            _buckets.AddOrUpdate((d.ShapeHash, d.Minute), _ => Copy(d), (_, b) => { lock (b) b.Merge(d); return b; });
    }

    private static MinuteBucket Copy(MinuteBucket d)
    {
        var b = new MinuteBucket { ShapeHash = d.ShapeHash, Minute = d.Minute };
        b.Merge(d);
        return b;
    }

    public void UpsertShapes(IReadOnlyList<ShapeInfo> shapes)
    {
        foreach (var s in shapes) _shapes[s.ShapeHash] = s;
    }

    public List<MinuteBucket> Buckets(long fromMinute, long toMinute, string? shape = null) =>
        _buckets.Values.Where(b => b.Minute >= fromMinute && b.Minute <= toMinute && (shape == null || b.ShapeHash == shape))
            .Select(b => { lock (b) return Copy(b); })
            .Concat(_hours.Values.Where(h => h.Minute * 60 >= fromMinute && h.Minute * 60 <= toMinute && (shape == null || h.ShapeHash == shape))
                .Select(h => { lock (h) { var c = Copy(h); c.Minute *= 60; return c; } })) // hourly ones at their hour's first minute, as SqliteStore reports them
            .ToList();

    public ShapeInfo? Shape(string hash) => _shapes.GetValueOrDefault(hash);

    public List<ShapeInfo> Shapes() => [.. _shapes.Values];

    public int PruneShapes(long lastSeenBeforeUs)
    {
        var n = 0;
        foreach (var (k, v) in _shapes)
            if (v.LastSeen < lastSeenBeforeUs && _shapes.TryRemove(k, out _)) n++;
        return n;
    }

    /// <summary>03 §7 as SqliteStore: minutes older than 48 h roll up to hours, hours are kept 30 d.</summary>
    public void Rollup(long nowUs)
    {
        var nowMinute = nowUs / 60_000_000;
        var cutoff = nowMinute - 48 * 60;
        foreach (var (key, b) in _buckets)
        {
            if (b.Minute >= cutoff || !_buckets.TryRemove(key, out _)) continue;
            lock (b)
                _hours.AddOrUpdate((b.ShapeHash, b.Minute / 60), _ => Copy2(b, b.Minute / 60), (_, h) => { lock (h) h.Merge(b); return h; });
        }
        var keepHour = (nowMinute - 30 * 24 * 60) / 60;
        foreach (var (key, _) in _hours)
            if (key.Item2 < keepHour) _hours.TryRemove(key, out _);
    }

    private static MinuteBucket Copy2(MinuteBucket d, long key)
    {
        var b = new MinuteBucket { ShapeHash = d.ShapeHash, Minute = key };
        b.Merge(d);
        return b;
    }

    public void Upsert(IReadOnlyList<Alert> alerts)
    {
        foreach (var a in alerts) _alerts[a.Id] = a;
    }

    public List<Alert> All() => [.. _alerts.Values];

    public void Delete(IReadOnlyCollection<string> ids)
    {
        foreach (var id in ids) _alerts.TryRemove(id, out _);
    }
}
