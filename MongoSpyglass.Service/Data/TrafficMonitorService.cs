using System.Collections.Concurrent;
using MongoSpyglass.Proxy;
using MongoSpyglass.Proxy.WireProtocol;

namespace MongoSpyglass.Service.Data;

public record DecryptedTraffic
{
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public string Tag { get; init; } = string.Empty; // "to" or "from"
    public int RequestId { get; init; }
    public string OpCode { get; init; } = string.Empty;
    public string Command { get; init; } = string.Empty;
    public string Collection { get; init; } = string.Empty;
    public string PayloadJson { get; init; } = string.Empty;
    public double? DurationMs { get; init; }
}

public class TrafficMonitorService : ITrafficListener
{
    private readonly ConcurrentQueue<DecryptedTraffic> _traffic = new();
    private const int MaxItems = 1000;

    public event Action? OnTrafficReceived;

    public IEnumerable<DecryptedTraffic> Traffic => _traffic.Reverse();

    public void AddTraffic(DecryptedTraffic entry)
    {
        _traffic.Enqueue(entry);
        while (_traffic.Count > MaxItems)
        {
            _traffic.TryDequeue(out _);
        }
        OnTrafficReceived?.Invoke();
    }

    public void OnMessage(string tag, int requestId, string opCode, string command, string collection, string payloadJson, double? durationMs = null)
    {
        AddTraffic(new DecryptedTraffic
        {
            Tag = tag,
            RequestId = requestId,
            OpCode = opCode,
            Command = command,
            Collection = collection,
            PayloadJson = payloadJson,
            DurationMs = durationMs
        });
    }
}
