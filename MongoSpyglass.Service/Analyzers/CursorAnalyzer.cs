using System.Collections.Concurrent;
using MongoSpyglass.Proxy;
using MongoSpyglass.Proxy.Bson;
using MongoSpyglass.Proxy.WireProtocol;

namespace MongoSpyglass.Service.Analyzers;

public class CursorStats
{
    public long Id { get; set; }
    public string Namespace { get; set; } = string.Empty;
    public string ConnectionId { get; set; } = string.Empty;
    public DateTime StartTime { get; set; } = DateTime.Now;
    public long TotalBytes { get; set; }
    public long TotalDocs { get; set; }
    public bool IsClosed { get; set; }
    public string ClosureReason { get; set; } = string.Empty;
}

public class CursorAnalyzer : IAnalyzerPlugin
{
    private readonly ConcurrentDictionary<long, CursorStats> _activeCursors = new();
    private readonly ConcurrentDictionary<int, long> _pendingGetMores = new();
    private readonly ConcurrentDictionary<string, List<long>> _connectionToCursors = new();

    public string Name => "Cursor Analyzer";

    public int ActiveCursorsCount => _activeCursors.Count(x => !x.Value.IsClosed);

    public void OnMessage(in ObservedMessage msg)
    {
        try
        {
            if (msg.Tag == "from" && !msg.Document.IsDefault)
            {
                // Look for "cursor" object in response
                if (msg.Document.TryGetElementOffset("cursor", out var cursorOffset))
                {
                    try
                    {
                        var cursorDoc = msg.Document.GetDocument(cursorOffset, msg.Tracker.Arena);
                        if (cursorDoc.TryGetElementOffset("id", out var idOffset))
                        {
                            long id = cursorDoc.GetInt64(idOffset);
                            if (id > 0)
                            {
                                string ns = "unknown";
                                if (cursorDoc.TryGetElementOffset("ns", out var nsOffset))
                                {
                                    ns = cursorDoc.GetString(nsOffset);
                                }
                                
                                var connectionId = msg.ConnectionId;
                                var stats = _activeCursors.GetOrAdd(id, _ => new CursorStats { Id = id, Namespace = ns, ConnectionId = connectionId });
                                stats.TotalBytes += msg.MessageSizeBytes;
                                stats.TotalDocs += msg.DocumentCount;

                                var cursorList = _connectionToCursors.GetOrAdd(msg.ConnectionId, _ => new());
                                lock(cursorList) { if (!cursorList.Contains(id))
                                    {
                                        cursorList.Add(id);
                                    }
                                }
                            }
                            else if (id == 0 && msg.ResponseTo != 0)
                            {
                                if (_pendingGetMores.TryRemove(msg.ResponseTo, out var originalId))
                                {
                                    if (_activeCursors.TryGetValue(originalId, out var stats))
                                    {
                                        stats.IsClosed = true;
                                        stats.ClosureReason = "Exhausted";
                                    }
                                }
                            }
                        }
                    }
                    catch { }
                }
            }
            else if (msg.Tag == "to" && !msg.Document.IsDefault)
            {
                if (msg.Document.TryGetElementOffset("getMore", out var getMoreOffset))
                {
                    try {
                        long id = msg.Document.GetInt64(getMoreOffset);
                        _pendingGetMores[msg.RequestId] = id;
                    } catch { }
                }
                else if (msg.Document.TryGetElementOffset("killCursors", out _))
                {
                    if (msg.Document.TryGetElementOffset("cursors", out var cursorsOffset))
                    {
                        try
                        {
                            var arr = msg.Document.GetArray(cursorsOffset, msg.Tracker.Arena);
                            foreach (var el in arr)
                            {
                                long id = el.Get<long>();
                                if (_activeCursors.TryGetValue(id, out var stats))
                                {
                                    stats.IsClosed = true;
                                    stats.ClosureReason = "Killed by Client";
                                }
                            }
                        }
                        catch { }
                    }
                }
            }

            // Cleanup stale pending requests (> 1 min)
            if (_pendingGetMores.Count > 1000)
            {
                var keys = _pendingGetMores.Keys.Take(100).ToList();
                foreach (var k in keys)
                {
                    _pendingGetMores.TryRemove(k, out _);
                }
            }
        }
        finally
        {
            msg.Release();
        }
    }

    public void OnConnectionClosed(string connectionId)
    {
        if (_connectionToCursors.TryRemove(connectionId, out var cursors))
        {
            foreach (var id in cursors)
            {
                if (_activeCursors.TryGetValue(id, out var stats) && !stats.IsClosed)
                {
                    stats.IsClosed = true;
                    stats.ClosureReason = "Connection Closed";
                }
            }
        }
    }

    public IEnumerable<Insight> GetInsights()
    {
        var active = _activeCursors.Values.Where(x => !x.IsClosed).ToList();
        if (active.Count > 0)
        {
            yield return new Insight(
                "Open Cursors",
                $"There are currently {active.Count} active cursors.",
                InsightLevel.Info,
                string.Join("\n", active.Select(s => $"ID: {s.Id} | NS: {s.Namespace} | Duration: {(DateTime.Now - s.StartTime).TotalSeconds:F1}s | Data: {s.TotalBytes / 1024.0:F1} KB | Rows: {s.TotalDocs}")),
                Category: "Cursors"
            );
        }

        var leaky = _activeCursors.Values.Where(x => x.IsClosed && x.ClosureReason == "Connection Closed").Take(10).ToList();
        if (leaky.Count > 0)
        {
             yield return new Insight(
                "Abandoned Cursors",
                $"{leaky.Count} cursors were abandoned due to connection closure.",
                InsightLevel.Warning,
                string.Join("\n", leaky.Select(s => $"ID: {s.Id} | NS: {s.Namespace} | Rows: {s.TotalDocs}")),
                Category: "Cursors"
            );
        }
    }
}
