using System;
using System.Collections.Generic;
using System.Linq;

namespace MongoSpyglass.Service.Analyzers.Rete;

public class CursorFact
{
    public string? RavenId { get; set; } // For persistence
    public string SessionId { get; set; } = string.Empty;
    public long Id { get; set; }
    public string Namespace { get; set; } = string.Empty;
    public string ConnectionId { get; set; } = string.Empty;
    public DateTime StartTime { get; set; } = DateTime.UtcNow;
    public DateTime LastActivity { get; set; } = DateTime.UtcNow; // For TTL
    public long TotalBytes { get; set; }
    public long TotalDocs { get; set; }
    public bool IsClosed { get; set; }
    public string ClosureReason { get; set; } = string.Empty;
    public DateTime? ClosedAt { get; set; }
    public bool OrphanedByDisconnect { get; set; } // Set when connection closes (distinguishes leak from normal close)
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
    public string ConnectionId { get; set; } = string.Empty;
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
    public string? RavenId { get; set; }
    public string SessionId { get; set; } = string.Empty;
    private const int MaxWindowSize = 100;
    private readonly Queue<double> _durations = new();

    public double AverageOpenTimeMs => _durations.Any() ? _durations.Average() : 0;
    // Note: RawDurations is needed for RavenDB serialization of the private Queue
    public List<double> RawDurations 
    { 
        get => _durations.ToList(); 
        set { _durations.Clear(); foreach(var v in value) _durations.Enqueue(v); } 
    }
    public long TotalClosedCount { get; set; }

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
    public string ConnectionId { get; set; } = string.Empty;
    public string Command { get; set; } = string.Empty;
    public string Collection { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

// New ingest facts extracted at observation time (no arena retention)
public class RequestObservedFact
{
    public int RequestId { get; set; }
    public string ConnectionId { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string Command { get; set; } = string.Empty;
    public string Collection { get; set; } = string.Empty;
    public string ShapeHash { get; set; } = string.Empty; // Hash of query shape (namespace + field names/operators, no values)
    public string ValueHash { get; set; } = string.Empty; // Hash of full query (shape + values)
    public string ExampleJson { get; set; } = string.Empty; // Truncated BSON-as-JSON example (a few KB)
}

public class ResponseObservedFact
{
    public int RequestId { get; set; } // Matches ResponseTo from wire protocol
    public string ConnectionId { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public double DurationMs { get; set; }
    public int MessageSizeBytes { get; set; }
    public int DocumentCount { get; set; }
    public long? CursorId { get; set; } // Nullable: not all responses have cursors
    public string? Namespace { get; set; } // Extracted from response if present
}

public class GetMoreRequestedFact
{
    public int RequestId { get; set; }
    public string ConnectionId { get; set; } = string.Empty;
    public long CursorId { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

public class KillCursorsRequestedFact
{
    public long CursorId { get; set; }
    public string ConnectionId { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

// Storage fact for query examples (persisted to RavenDB, not in NRules session)
public class QueryExampleFact
{
    public string? Id { get; set; } // RavenDB doc ID: "QueryExample/{hash}"
    public string Hash { get; set; } = string.Empty; // 128-bit XxHash128 hex string
    public string Kind { get; set; } = string.Empty; // "SlowQuery", "N1Shape", "DuplicateValue"
    public string Namespace { get; set; } = string.Empty;
    public string Command { get; set; } = string.Empty;
    public string ExampleJson { get; set; } = string.Empty;
    public DateTime FirstSeenUtc { get; set; } = DateTime.UtcNow;
    public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;
    public long OccurrenceCount { get; set; } = 1;
    public double? MaxDurationMs { get; set; } // For SlowQuery kind
}

public class SessionFact
{
    public string Id { get; set; } = string.Empty;
}

public class SettingsSnapshotFact
{
    public double SlowQueryThresholdMs { get; set; } = 100;
}

public class QueryExampleToSaveFact
{
    public string Hash { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string ExampleJson { get; set; } = string.Empty;
    public string Namespace { get; set; } = string.Empty;
    public string Command { get; set; } = string.Empty;
}

public class CursorLeakAlertThresholdFact
{
    public double IdleHoursThreshold { get; set; } = 1;
}
