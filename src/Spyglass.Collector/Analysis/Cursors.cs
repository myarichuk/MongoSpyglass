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
    /// <summary>Collector clock at the last activity: idle expiry runs on it, not on the producer's <c>ts</c> (a lagging clock would expire every cursor).</summary>
    internal long IdleSinceUs;
    internal LinkedListNode<CursorInfo>? Node;

    /// <summary>A copy for readers on other threads: the registry keeps mutating the original.</summary>
    internal CursorInfo Snapshot()
    {
        var c = (CursorInfo)MemberwiseClone();
        c.Node = null;
        return c;
    }
}

/// <summary>Per shape (03 §5.4): time to exhaustion, total documents, getMore count, closure reasons.</summary>
public sealed class CursorShapeStats
{
    public long Closed, Documents, GetMores, ExhaustionUsSum, Exhausted;
    public Dictionary<string, long> Reasons = [];

    internal CursorShapeStats Snapshot()
    {
        var s = (CursorShapeStats)MemberwiseClone();
        s.Reasons = new Dictionary<string, long>(Reasons);
        return s;
    }
}

/// <summary>
/// 03 §5.4 cursor lifecycle. Keyed by <c>(proxyId, member, cursorId)</c>, never by connection: drivers send
/// getMore over any pooled connection to the server. A connection closing has no effect. Analysis thread only.
/// Open cursors are kept in order of last activity, so eviction at the cap and idle expiry both work from the head in O(1).
/// </summary>
public sealed class CursorRegistry(Func<long>? clockUs = null)
{
    private const int MaxOpen = 100_000, MaxClosed = 10_000;
    private readonly Dictionary<(string, string, long), CursorInfo> _open = [];
    private readonly LinkedList<CursorInfo> _byActivity = new();
    private readonly Queue<CursorInfo> _closed = new();
    private readonly Dictionary<string, CursorShapeStats> _byShape = [];

    /// <summary>Bumped on every change: readers' snapshots are rebuilt only when it moved.</summary>
    public long Version { get; private set; }
    public IEnumerable<CursorInfo> Open => _open.Values;
    public IEnumerable<CursorInfo> Closed => _closed;
    public IReadOnlyDictionary<string, CursorShapeStats> ByShape => _byShape;

    private long Idle(long ts) => clockUs?.Invoke() ?? ts;

    private void Active(CursorInfo c, long ts)
    {
        c.IdleSinceUs = Idle(ts);
        _byActivity.Remove(c.Node!);
        _byActivity.AddLast(c.Node!);
    }

    private void Detach(CursorInfo c)
    {
        _open.Remove((c.ProxyId, c.Member, c.CursorId));
        if (c.Node != null) _byActivity.Remove(c.Node);
        c.Node = null;
    }

    public void Opened(string proxyId, string member, long cursorId, string opId, string shapeHash, string? app, string? db, string? coll, long ts, int firstBatch, bool awaitsByDesign = false)
    {
        Version++;
        if (_open.TryGetValue((proxyId, member, cursorId), out var same)) Detach(same); // the id was reused: the old one is gone
        else if (_open.Count >= MaxOpen && _byActivity.First is { } oldest) // bounded: the least recently active is dropped from tracking, not closed
            Detach(oldest.Value);
        var c = new CursorInfo
        {
            ProxyId = proxyId, Member = member, CursorId = cursorId, OpenOpId = opId, ShapeHash = shapeHash,
            App = app, Db = db, Coll = coll, OpenedTs = ts, LastActivityTs = ts, Documents = firstBatch, AwaitsByDesign = awaitsByDesign,
            IdleSinceUs = Idle(ts),
        };
        c.Node = _byActivity.AddLast(c);
        _open[(proxyId, member, cursorId)] = c;
    }

    public CursorInfo? Get(string proxyId, string member, long cursorId) => _open.GetValueOrDefault((proxyId, member, cursorId));

    /// <param name="getMore">false for an exhaust stream's batch: no getMore was sent for it</param>
    public CursorInfo? Touched(string proxyId, string member, long cursorId, long ts, int returned, bool getMore = true)
    {
        if (!_open.TryGetValue((proxyId, member, cursorId), out var c)) return null;
        Version++;
        c.LastActivityTs = Math.Max(c.LastActivityTs, ts);
        c.Documents += returned;
        if (getMore) c.GetMores++;
        Active(c, ts);
        return c;
    }

    /// <param name="returned">the closing batch's documents (a getMore's reply, or an exhaust stream's last batch)</param>
    /// <param name="finalGetMore">closed by a getMore: the batch counts as a getMore</param>
    public CursorInfo? Close(string proxyId, string member, long cursorId, string reason, long ts, int returned = 0, bool finalGetMore = false)
    {
        if (!_open.TryGetValue((proxyId, member, cursorId), out var c)) return null;
        Version++;
        Detach(c);
        c.Documents += returned;
        if (finalGetMore) c.GetMores++;
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

    /// <summary>Cursors idle longer than the server's cursor timeout (on the collector's clock): closed as timed-out (and abandoned).</summary>
    public List<CursorInfo> Expire(long nowUs, TimeSpan timeout)
    {
        var limit = (long)timeout.TotalMicroseconds;
        var expired = new List<CursorInfo>();
        while (_byActivity.First is { } head && nowUs - head.Value.IdleSinceUs > limit)
        {
            var c = head.Value;
            Close(c.ProxyId, c.Member, c.CursorId, "timed-out", nowUs);
            expired.Add(c);
        }
        return expired;
    }

    /// <summary>Drops per-shape closure stats for shapes that left the catalog (03 §5: bounded state).</summary>
    public void ForgetShapes(IEnumerable<string> hashes)
    {
        foreach (var h in hashes) _byShape.Remove(h);
        Version++;
    }

    /// <summary>Drops per-shape stats whose shape is not in <paramref name="known"/>.</summary>
    public void RetainShapes(IReadOnlyDictionary<string, ShapeInfo> known)
    {
        List<string>? drop = null;
        foreach (var k in _byShape.Keys)
            if (!known.ContainsKey(k)) (drop ??= []).Add(k);
        if (drop != null) ForgetShapes(drop);
    }
}
