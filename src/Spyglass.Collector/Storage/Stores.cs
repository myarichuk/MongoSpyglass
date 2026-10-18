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
    /// <summary>Retention (03 §7): older than <paramref name="olderThanUs"/>, then oldest first until under <paramref name="maxBytes"/>.</summary>
    int Prune(long olderThanUs, long maxBytes);
    long Count { get; }
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
    /// <summary>03 §7: minute buckets roll up to hourly after 48 h; hourly kept 30 d.</summary>
    void Rollup(long nowUs);
}

public interface IAlertStore
{
    void Upsert(IReadOnlyList<Alert> alerts);
    List<Alert> All();
}

/// <summary>Everything in memory, bounded: tests and <c>--data :memory:</c>.</summary>
public sealed class MemoryStore : IOperationStore, IAggregateStore, IAlertStore, ICallsiteStore
{
    private readonly ConcurrentDictionary<string, (CallsiteRecord Record, long LastUsed)> _callsites = new();
    private readonly int _maxOps;
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
        }
        while (_ops.Count > _maxOps && _order.TryDequeue(out var old))
            _ops.TryRemove(old, out _);
    }

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

    public int Prune(long olderThanUs, long maxBytes)
    {
        var n = 0;
        while (_order.TryPeek(out var id) && (!_ops.TryGetValue(id, out var op) || op.Ts < olderThanUs) && _order.TryDequeue(out _))
            if (_ops.TryRemove(id, out _)) n++;
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
            .Select(b => { lock (b) return Copy(b); }).ToList();

    public ShapeInfo? Shape(string hash) => _shapes.GetValueOrDefault(hash);

    public List<ShapeInfo> Shapes() => [.. _shapes.Values];

    public void Rollup(long nowUs) { } // memory mode: bounded by process lifetime

    public void Upsert(IReadOnlyList<Alert> alerts)
    {
        foreach (var a in alerts) _alerts[a.Id] = a;
    }

    public List<Alert> All() => [.. _alerts.Values];
}
