using System.Collections.Concurrent;
using Spyglass.Collector.Analysis;
using Spyglass.Collector.Model;

namespace Spyglass.Collector.Storage;

/// <summary>03 §8 <c>GET /ops</c> filters. Newest first; <see cref="Cursor"/> is the last id of the previous page.</summary>
public sealed record OpQuery
{
    public long? From, To;              // µs since epoch
    public string? App, Coll, Command, Shape, Status;
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
    /// <summary>Further replies on a moreToCome stream (02 §6.1) and alerts raised later (03 §8 "linked alerts").</summary>
    void Update(string id, int replyCount, IReadOnlyList<string>? alerts);
    /// <summary>Retention (03 §7): older than <paramref name="olderThanUs"/>, then oldest first until under <paramref name="maxBytes"/>.</summary>
    int Prune(long olderThanUs, long maxBytes);
    long Count { get; }
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
public sealed class MemoryStore : IOperationStore, IAggregateStore, IAlertStore
{
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
        (q.Status == null || o.Op.Status == q.Status) &&
        (q.MinLatencyUs is not { } m || o.ServerLatencyUs >= m) &&
        (q.Cursor == null || string.CompareOrdinal(o.Id, q.Cursor) < 0);

    public void Update(string id, int replyCount, IReadOnlyList<string>? alerts)
    {
        if (!_ops.TryGetValue(id, out var op)) return;
        op.ReplyCount = replyCount;
        if (alerts != null) op.Alerts = [.. alerts];
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
