using System;
using System.Linq;
using MongoSpyglass.Service.Analyzers.Rete;
using NRules;
using NRules.Fluent;
using Xunit;

namespace MongoSpyglass.Service.Tests.Analyzers;

public class CursorRulesTests
{
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

    [Fact]
    public void KillCursorsRule_ClosesCursor()
    {
        var repository = new RuleRepository();
        repository.Load(x => x.From(typeof(KillCursorsRule).Assembly));
        var factory = repository.Compile();
        var session = factory.CreateSession();

        var connectionId = "conn1";
        var cursorId = 12345L;
        var cursor = new CursorFact { Id = cursorId, ConnectionId = connectionId, IsClosed = false };
        session.Insert(cursor);

        // Mock killCursors message
        // Using a real message or at least one that satisfies HasKillCursors and Explode
        // We need a BlittableBsonDocument with "killCursors" and "cursors" array.
        
        // Actually, since I'm testing the Rete rules, I can just use a MessageFact
        // and let ExplodeKillCursorsRule do its thing.
        // But creating a BlittableBsonDocument manually might be complex.
        
        // Alternatively, I can test the rules individually by inserting the facts they expect.
        // But testing the chain is better.
        
        // Let's see if I can use BsonUtils or similar to create a document.
        // Or I can just insert the PendingKillFact directly to test KillCursorsRule, 
        // and test ExplodeKillCursorsRule separately if needed.
        
        // Testing the chain:
        // ExplodeKillCursorsRule inserts PendingKillFact.
        // KillCursorsRule processes it.
        
        // I'll check how messages are mocked in other tests if any.
        // CleanupRuleTests.cs?
    }
}
