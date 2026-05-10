using System;
using System.Collections.Generic;
using System.Linq;

namespace MongoSpyglass.Service.Analyzers.Rete;

public class CursorFact
{
    public long Id { get; set; }
    public string Namespace { get; set; } = string.Empty;
    public string ConnectionId { get; set; } = string.Empty;
    public DateTime StartTime { get; set; } = DateTime.UtcNow; // Will be set from MessageFact.Timestamp
    public long TotalBytes { get; set; }
    public long TotalDocs { get; set; }
    public bool IsClosed { get; set; }
    public string ClosureReason { get; set; } = string.Empty;
    public DateTime? ClosedAt { get; set; }
    public double? DurationMs => ClosedAt.HasValue ? (ClosedAt.Value - StartTime).TotalMilliseconds : null;
}

public class ConnectionClosedFact
{
    public string ConnectionId { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

public class PendingGetMoreFact
{
    public int RequestId { get; set; }
    public long CursorId { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

public class PendingKillFact
{
    public long CursorId { get; set; }
    public string ConnectionId { get; set; } = string.Empty;
}

public class CursorStatsFact
{
    private const int MaxWindowSize = 100;
    private readonly Queue<double> _durations = new();

    public double AverageOpenTimeMs => _durations.Any() ? _durations.Average() : 0;
    public int WindowSize => _durations.Count;
    public long TotalClosedCount { get; private set; }

    public void AddDuration(double ms)
    {
        _durations.Enqueue(ms);
        if (_durations.Count > MaxWindowSize) _durations.Dequeue();
        TotalClosedCount++;
    }
}

public class PendingRequestFact
{
    public int RequestId { get; set; }
    public string Command { get; set; } = string.Empty;
    public string Collection { get; set; } = string.Empty;
    public MessageFact? TriggerMessage { get; set; } // Memory optimization reference
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}
