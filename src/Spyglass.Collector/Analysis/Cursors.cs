namespace Spyglass.Collector.Analysis;

public sealed class CursorInfo
{
    public required string ProxyId, Member;
    public required long CursorId;
    public required string OpenOpId, ShapeHash;
    public string? App, Db, Coll;
    public bool AwaitsByDesign;   // tailable / awaitData / change stream: its getMores wait by design
    public long OpenedTs, LastActivityTs;
    public long Documents;
    public int GetMores;
    public string? ClosedReason;   // exhausted | killed | server-expired | timed-out
    public long? ClosedTs;
}

/// <summary>Per shape (03 §5.4): time to exhaustion, total documents, getMore count, closure reasons.</summary>
public sealed class CursorShapeStats
{
    public long Closed, Documents, GetMores, ExhaustionUsSum, Exhausted;
    public Dictionary<string, long> Reasons = [];
}

/// <summary>
/// 03 §5.4 cursor lifecycle. Keyed by <c>(proxyId, member, cursorId)</c>, never by connection: drivers send
/// getMore over any pooled connection to the server. A connection closing has no effect. Analysis thread only.
/// </summary>
public sealed class CursorRegistry
{
    private const int MaxOpen = 100_000, MaxClosed = 10_000;
    private readonly Dictionary<(string, string, long), CursorInfo> _open = [];
    private readonly Queue<CursorInfo> _closed = new();
    private readonly Dictionary<string, CursorShapeStats> _byShape = [];

    public int OpenCount => _open.Count;
    public IEnumerable<CursorInfo> Open => _open.Values;
    public IEnumerable<CursorInfo> Closed => _closed;
    public IReadOnlyDictionary<string, CursorShapeStats> ByShape => _byShape;

    public void Opened(string proxyId, string member, long cursorId, string opId, string shapeHash, string? app, string? db, string? coll, long ts, int firstBatch, bool awaitsByDesign = false)
    {
        if (_open.Count >= MaxOpen) // bounded: the oldest open cursor is dropped from tracking, not closed
            _open.Remove(_open.MinBy(kv => kv.Value.LastActivityTs).Key);
        _open[(proxyId, member, cursorId)] = new CursorInfo
        {
            ProxyId = proxyId, Member = member, CursorId = cursorId, OpenOpId = opId, ShapeHash = shapeHash,
            App = app, Db = db, Coll = coll, OpenedTs = ts, LastActivityTs = ts, Documents = firstBatch, AwaitsByDesign = awaitsByDesign,
        };
    }

    public CursorInfo? Get(string proxyId, string member, long cursorId) => _open.GetValueOrDefault((proxyId, member, cursorId));

    public CursorInfo? Touched(string proxyId, string member, long cursorId, long ts, int returned)
    {
        if (!_open.TryGetValue((proxyId, member, cursorId), out var c)) return null;
        c.LastActivityTs = Math.Max(c.LastActivityTs, ts);
        c.Documents += returned;
        c.GetMores++;
        return c;
    }

    /// <param name="finalGetMore">closed by a getMore (its batch, possibly empty, counts)</param>
    public CursorInfo? Close(string proxyId, string member, long cursorId, string reason, long ts, int returned = 0, bool finalGetMore = false)
    {
        if (!_open.Remove((proxyId, member, cursorId), out var c)) return null;
        if (finalGetMore) { c.Documents += returned; c.GetMores++; }
        c.ClosedReason = reason;
        c.ClosedTs = ts;
        _closed.Enqueue(c);
        if (_closed.Count > MaxClosed) _closed.Dequeue();
        if (!_byShape.TryGetValue(c.ShapeHash, out var s))
            _byShape[c.ShapeHash] = s = new CursorShapeStats();
        s.Closed++;
        s.Documents += c.Documents;
        s.GetMores += c.GetMores;
        s.Reasons[reason] = s.Reasons.GetValueOrDefault(reason) + 1;
        if (reason == "exhausted") { s.Exhausted++; s.ExhaustionUsSum += ts - c.OpenedTs; }
        return c;
    }

    /// <summary>Cursors idle longer than the server's cursor timeout: closed as timed-out (and abandoned).</summary>
    public List<CursorInfo> Expire(long nowUs, TimeSpan timeout)
    {
        var limit = (long)timeout.TotalMicroseconds;
        var expired = _open.Values.Where(c => nowUs - c.LastActivityTs > limit).ToList();
        foreach (var c in expired) Close(c.ProxyId, c.Member, c.CursorId, "timed-out", nowUs);
        return expired;
    }
}
