# Cursor Analyzer Refactor Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Improve cursor analyzer efficiency, optimize memory for request tracking, and add cursor open time statistics.

**Architecture:** Approach 1 (Rete-Integrated Stats). Logic stays within NRules, using a sliding window for duration tracking and exploding arrays for efficient matching.

**Tech Stack:** C#, .NET 7/8, NRules, Blazor.

---

### Task 1: Update Facts & Models

**Files:**
- Modify: `MongoSpyglass.Service/Analyzers/Rete/Facts.cs`

- [ ] **Step 1: Add new fact types and update `CursorFact`**

```csharp
using System.Collections.Generic;
using System.Linq;

// ... existing code ...

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
```

- [ ] **Step 2: Commit changes**

```bash
git add MongoSpyglass.Service/Analyzers/Rete/Facts.cs
git commit -m "refactor: update facts for cursor tracking and memory optimization"
```

---

### Task 2: Refactor Cursor Creation and Stats Tracking

**Files:**
- Modify: `MongoSpyglass.Service/Analyzers/Rete/CursorRules.cs`
- Create: `MongoSpyglass.Service.Tests/Analyzers/CursorRulesTests.cs`

- [ ] **Step 1: Update `DetectNewCursorRule` and add `UpdateCursorStatsRule`**

```csharp
// In MongoSpyglass.Service/Analyzers/Rete/CursorRules.cs

public class DetectNewCursorRule : Rule
{
    public override void Define()
    {
        MessageFact? msg = null;

        When()
            .Match<MessageFact>(() => msg, m => m.Message.Tag == "from" && !m.Message.Document.IsDefault && HasCursor(m));

        Then()
            .Do(ctx => ProcessCursorResponse(ctx, msg!));
    }

    private bool HasCursor(MessageFact m) => m.Message.Document.TryGetElementOffset("cursor", out _);

    private void ProcessCursorResponse(IContext ctx, MessageFact msg)
    {
        try {
            if (msg.Message.Document.TryGetElementOffset("cursor", out var cursorOffset)) {
                var cursorDoc = msg.Message.Document.GetDocument(cursorOffset, msg.Message.Tracker.Arena);
                if (cursorDoc.TryGetElementOffset("id", out var idOffset)) {
                    long id = cursorDoc.GetInt64(idOffset);
                    if (id > 0) {
                        string ns = "unknown";
                        if (cursorDoc.TryGetElementOffset("ns", out var nsOffset)) ns = cursorDoc.GetString(nsOffset);
                        
                        var cursor = new CursorFact { 
                            Id = id, 
                            Namespace = ns, 
                            ConnectionId = msg.Message.ConnectionId, 
                            StartTime = msg.Timestamp, // Use message timestamp
                            TotalBytes = msg.Message.MessageSizeBytes, 
                            TotalDocs = msg.Message.DocumentCount 
                        };
                        ctx.Insert(cursor);
                    }
                }
            }
        } catch {}
    }
}

public class UpdateCursorStatsRule : Rule
{
    public override void Define()
    {
        CursorFact? cursor = null;
        CursorStatsFact? stats = null;

        When()
            .Match<CursorFact>(() => cursor, c => c.IsClosed && c.ClosedAt.HasValue)
            .Match<CursorStatsFact>(() => stats);

        Then()
            .Do(ctx => UpdateStats(ctx, stats!, cursor!));
    }

    private void UpdateStats(IContext ctx, CursorStatsFact stats, CursorFact cursor)
    {
        if (cursor.DurationMs.HasValue)
        {
            stats.AddDuration(cursor.DurationMs.Value);
            ctx.Update(stats);
            // We retract the closed cursor fact to keep the session lean after stats are recorded
            ctx.Retract(cursor); 
        }
    }
}
```

- [ ] **Step 2: Initialize `CursorStatsFact` in the engine**

Modify `MongoSpyglass.Service/Analyzers/ReteAnalyzerEngine.cs`:
```csharp
// In constructor
_session.Insert(new CursorStatsFact());
```

- [ ] **Step 3: Write test for Stats Tracking**

```csharp
// MongoSpyglass.Service.Tests/Analyzers/CursorRulesTests.cs
[Fact]
public void UpdateCursorStatsRule_CalculatesAverage()
{
    var repository = new RuleRepository();
    repository.Load(x => x.From(typeof(UpdateCursorStatsRule).Assembly));
    var factory = repository.Compile();
    var session = factory.CreateSession();
    
    var stats = new CursorStatsFact();
    session.Insert(stats);
    
    var startTime = DateTime.UtcNow.AddSeconds(-10);
    var cursor1 = new CursorFact { Id = 1, StartTime = startTime, IsClosed = true, ClosedAt = startTime.AddMilliseconds(500) };
    var cursor2 = new CursorFact { Id = 2, StartTime = startTime, IsClosed = true, ClosedAt = startTime.AddMilliseconds(1500) };
    
    session.Insert(cursor1);
    session.Insert(cursor2);
    session.Fire();
    
    Assert.Equal(1000, stats.AverageOpenTimeMs);
    Assert.Equal(2, stats.TotalClosedCount);
}
```

- [ ] **Step 4: Run tests and commit**

---

### Task 3: Refactor KillCursors (Efficient Matching)

**Files:**
- Modify: `MongoSpyglass.Service/Analyzers/Rete/CursorRules.cs`

- [ ] **Step 1: Replace `KillCursorsRule` with two rules**

```csharp
public class ExplodeKillCursorsRule : Rule
{
    public override void Define()
    {
        MessageFact? msg = null;

        When()
            .Match<MessageFact>(() => msg, m => m.Message.Tag == "to" && HasKillCursors(m));

        Then()
            .Do(ctx => Explode(ctx, msg!));
    }

    private bool HasKillCursors(MessageFact m) => m.Message.Document.TryGetElementOffset("killCursors", out _);

    private void Explode(IContext ctx, MessageFact msg)
    {
        try {
            if (msg.Message.Document.TryGetElementOffset("cursors", out var cursorsOffset)) {
                var arr = msg.Message.Document.GetArray(cursorsOffset, msg.Message.Tracker.Arena);
                foreach (var el in arr) {
                    ctx.Insert(new PendingKillFact { CursorId = el.Get<long>(), ConnectionId = msg.Message.ConnectionId });
                }
            }
        } catch {}
    }
}

public class KillCursorsRule : Rule
{
    public override void Define()
    {
        PendingKillFact? kill = null;
        CursorFact? cursor = null;

        When()
            .Match<PendingKillFact>(() => kill)
            .Match<CursorFact>(() => cursor, c => c.Id == kill!.CursorId && c.ConnectionId == kill!.ConnectionId && !c.IsClosed);

        Then()
            .Do(ctx => ProcessKill(ctx, kill!, cursor!));
    }

    private void ProcessKill(IContext ctx, PendingKillFact kill, CursorFact cursor)
    {
        cursor.IsClosed = true;
        cursor.ClosureReason = "Killed by Client";
        cursor.ClosedAt = DateTime.UtcNow;
        ctx.Update(cursor);
        ctx.Retract(kill);
    }
}
```

- [ ] **Step 2: Commit**

---

### Task 4: Memory Optimization for Request Tracking

**Files:**
- Modify: `MongoSpyglass.Service/Analyzers/Rete/SlowQueryRules.cs`

- [ ] **Step 1: Refactor `TrackRequestRule` and `DetectSlowQueryRule`**

```csharp
public class TrackRequestRule : Rule
{
    public override void Define()
    {
        MessageFact? msg = null;

        When()
            .Match<MessageFact>(() => msg, m => m.Message.Tag == "to" && !m.Message.Document.IsDefault);

        Then()
            .Do(ctx => TrackRequest(ctx, msg!));
    }

    private void TrackRequest(IContext ctx, MessageFact msg)
    {
        string command = "unknown";
        string collection = "unknown";

        if (msg.Message.Document.KeysEnumerable.Any()) {
            command = msg.Message.Document.KeysEnumerable.First().ToString();
            try {
                if (msg.Message.Document.TryGetElementOffset("collection", out var colOffset)) collection = msg.Message.Document.GetString(colOffset);
                else if (msg.Message.Document.TryGetElementOffset(command.AsSpan(), out var offset)) collection = msg.Message.Document.GetString(offset);
                else if (msg.Message.Document.TryGetElementOffset("$db", out var dbOff)) collection = msg.Message.Document.GetString(dbOff);
            } catch {}
        }
        // Store reference to message fact instead of cloning BSON
        ctx.Insert(new PendingRequestFact { 
            RequestId = msg.Message.RequestId, 
            Command = command, 
            Collection = collection, 
            TriggerMessage = msg,
            Timestamp = msg.Timestamp 
        });
    }
}

public class DetectSlowQueryRule : Rule
{
    public override void Define()
    {
        MessageFact? msg = null;
        PendingRequestFact? pending = null;

        When()
            .Match<MessageFact>(() => msg, m => m.Message.Tag == "from" && m.Message.DurationMs > 100)
            .Match<PendingRequestFact>(() => pending, p => p.RequestId == msg!.Message.ResponseTo);

        Then()
            .Do(ctx => GenerateSlowQueryInsight(ctx, msg!, pending!));
    }

    private void GenerateSlowQueryInsight(IContext ctx, MessageFact msg, PendingRequestFact req)
    {
        string payloadJson = "{}";
        // ONLY clone/serialize if it's slow
        if (req.TriggerMessage != null) {
            try {
                var raw = req.TriggerMessage.Message.Document.AsReadOnlySpan().ToArray();
                var bsonDoc = BsonSerializer.Deserialize<BsonDocument>(raw);
                payloadJson = bsonDoc.ToJson(new JsonWriterSettings { Indent = true });
            } catch {}
        }

        var insight = new Insight(
            "Slow Query Detected",
            $"Slow {req.Command} on {req.Collection} detected: {msg.Message.DurationMs:F2}ms",
            InsightLevel.Warning,
            $"Total Latency: {msg.Message.DurationMs:F2}ms\nPayload:\n{payloadJson}",
            Category: "Performance"
        );
        ctx.Insert(insight);
        ctx.Retract(req);
    }
}
```

- [ ] **Step 2: Commit**

---

### Task 5: UI Integration (Cursor Statistics Widget)

**Files:**
- Modify: `MongoSpyglass.Service/Pages/Analysis.razor`
- Modify: `MongoSpyglass.Service/Analyzers/ReteAnalyzerEngine.cs`

- [ ] **Step 1: Expose stats in the engine**

In `ReteAnalyzerEngine.cs`:
```csharp
public CursorStatsFact GetCursorStats() => _session.Query<CursorStatsFact>().FirstOrDefault() ?? new CursorStatsFact();
```

- [ ] **Step 2: Add UI Widget to `Analysis.razor`**

```razor
@* Add after Performance Bottlenecks header *@
<div class="grid grid-cols-1 md:grid-cols-3 gap-4 mb-4">
    <div class="bg-surface-container border border-outline-variant rounded p-4 flex flex-col items-center justify-center">
        <div class="text-xs text-on-surface-variant font-ui-label-sm mb-1 uppercase tracking-wider">Active Cursors</div>
        <div class="text-3xl font-display-mono text-primary">@Analyzers.OfType<ReteAnalyzerEngine>().FirstOrDefault()?.ActiveCursorsCount</div>
    </div>
    <div class="bg-surface-container border border-outline-variant rounded p-4 flex flex-col items-center justify-center">
        <div class="text-xs text-on-surface-variant font-ui-label-sm mb-1 uppercase tracking-wider">Avg Open Duration (Sliding 100)</div>
        <div class="text-3xl font-display-mono text-secondary">@Analyzers.OfType<ReteAnalyzerEngine>().FirstOrDefault()?.GetCursorStats().AverageOpenTimeMs.ToString("F0")ms</div>
    </div>
    <div class="bg-surface-container border border-outline-variant rounded p-4 flex flex-col items-center justify-center">
        <div class="text-xs text-on-surface-variant font-ui-label-sm mb-1 uppercase tracking-wider">Total Cursors Tracked</div>
        <div class="text-3xl font-display-mono text-tertiary">@Analyzers.OfType<ReteAnalyzerEngine>().FirstOrDefault()?.GetCursorStats().TotalClosedCount</div>
    </div>
</div>
```

- [ ] **Step 3: Final validation and commit**
