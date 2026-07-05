using NRules.Fluent.Dsl;
using NRules.RuleModel;
using System;

namespace MongoSpyglass.Service.Analyzers.Rete;

public class CleanupExpiredRequestRule : Rule
{
    public override void Define()
    {
        PendingRequestFact? fact = null;
        TimeTick? tick = null;

        When()
            .Match<TimeTick>(() => tick)
            .Match<PendingRequestFact>(() => fact, f => (tick!.CurrentTime - f.Timestamp).TotalMinutes > 5);

        Then()
            .Do(ctx => ctx.Retract(fact!));
    }
}

public class CleanupAbandonedCursorRule : Rule
{
    public override void Define()
    {
        CursorFact? cursor = null;
        TimeTick? tick = null;

        When()
            .Match<TimeTick>(() => tick)
            .Match<CursorFact>(() => cursor, f => !f.IsClosed && (tick!.CurrentTime - f.LastActivity).TotalHours > 24);

        Then()
            .Do(ctx => CleanupAbandoned(ctx, cursor!, tick!));
    }

    private void CleanupAbandoned(IContext ctx, CursorFact cursor, TimeTick tick)
    {
        var idleHours = (tick.CurrentTime - cursor.LastActivity).TotalHours;
        var insight = new Insight(
            "Abandoned Cursor Leaked",
            $"Cursor {cursor.Id} on {cursor.Namespace} from connection {cursor.ConnectionId} idle for {idleHours:F1}h",
            InsightLevel.Critical,
            $"Namespace: {cursor.Namespace}\nCursor ID: {cursor.Id}\nConnection: {cursor.ConnectionId}\n" +
            $"Idle Duration: {idleHours:F1} hours\nOrphaned: {cursor.OrphanedByDisconnect}",
            Category: "Cursor Leak"
        );
        ctx.Insert(insight);
        ctx.Retract(cursor);
    }
}
