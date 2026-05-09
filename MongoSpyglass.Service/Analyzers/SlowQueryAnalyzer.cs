using System.Collections.Concurrent;
using MongoSpyglass.Proxy;
using MongoSpyglass.Proxy.Bson;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.IO;

namespace MongoSpyglass.Service.Analyzers;

public class SlowQueryAnalyzer : IAnalyzerPlugin
{
    private record RequestInfo(DateTime Timestamp, string Command, string Collection, byte[]? RawBson);
    private readonly ConcurrentQueue<Insight> _insights = new();
    private readonly ConcurrentDictionary<int, RequestInfo> _pendingRequests = new();

    public string Name => "Slow Query Analyzer";

    public void OnMessage(in ObservedMessage msg)
    {
        try
        {
            if (msg.Tag == "to")
            {
                string command = "unknown";
                string collection = "unknown";
                byte[]? rawBson = null;

                if (!msg.Document.IsDefault)
                {
                    rawBson = msg.Document.AsReadOnlySpan().ToArray();

                    if (msg.Document.KeysEnumerable.Any())
                    {
                        var firstKey = msg.Document.KeysEnumerable.First();
                        command = firstKey.ToString();
                        
                        try 
                        {
                            if (msg.Document.TryGetElementOffset("collection", out var colOffset))
                            {
                                collection = msg.Document.GetString(colOffset);
                            }
                            else if (msg.Document.TryGetElementOffset(command.AsSpan(), out var offset))
                            {
                                collection = msg.Document.GetString(offset);
                            }
                            else if (msg.Document.TryGetElementOffset("$db", out var dbOff))
                            {
                                collection = msg.Document.GetString(dbOff);
                            }
                        }
                        catch { }
                    }
                }
                _pendingRequests[msg.RequestId] = new RequestInfo(DateTime.Now, command, collection, rawBson);
            }
            else if (msg.Tag == "from" && msg.DurationMs > 100)
            {
                if (_pendingRequests.TryRemove(msg.ResponseTo, out var req))
                {
                    string payloadJson = "{}";
                    if (req.RawBson != null)
                    {
                        try {
                            var bsonDoc = BsonSerializer.Deserialize<BsonDocument>(req.RawBson);
                            payloadJson = bsonDoc.ToJson(new JsonWriterSettings { Indent = true });
                        } catch { }
                    }

                    _insights.Enqueue(new Insight(
                        "Slow Query Detected",
                        $"Slow {req.Command} on {req.Collection} detected: {msg.DurationMs:F2}ms ({msg.MessageSizeBytes / 1024.0:F1} KB)",
                        InsightLevel.Warning,
                        $"Total Latency: {msg.DurationMs:F2}ms\nRequest Size: {msg.MessageSizeBytes} bytes\nRequest ID: {msg.ResponseTo}\n\nPayload:\n{payloadJson}",
                        Category: "Performance"
                    ));
                }
                else
                {
                    _insights.Enqueue(new Insight(
                        "Slow Query Detected",
                        $"Slow operation detected: {msg.DurationMs:F2}ms (Request details timed out or missing)",
                        InsightLevel.Info,
                        $"Total Latency: {msg.DurationMs:F2}ms\nResponse ID: {msg.RequestId}\nCorrelation ID (ResponseTo): {msg.ResponseTo}",
                        Category: "Performance"
                    ));
                }

                // Limit insights
                while (_insights.Count > 100) _insights.TryDequeue(out _);
            }
            else if (msg.Tag == "from")
            {
                _pendingRequests.TryRemove(msg.ResponseTo, out _);
            }
        }
        finally
        {
            msg.Release();
        }

        // Cleanup stale requests (> 5 min)
        if (_pendingRequests.Count > 1000)
        {
            var cutoff = DateTime.Now.AddMinutes(-5);
            foreach (var kvp in _pendingRequests)
            {
                if (kvp.Value.Timestamp < cutoff)
                {
                    _pendingRequests.TryRemove(kvp.Key, out _);
                }
            }
        }
    }

    public IEnumerable<Insight> GetInsights() => _insights;
}
