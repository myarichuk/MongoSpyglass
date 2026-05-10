using NRules.Fluent.Dsl;
using NRules.RuleModel;
using System;

namespace MongoSpyglass.Service.Analyzers.Rete;

public class CleanupRule : Rule
{
    public override void Define()
    {
        MessageFact? fact = null;
        TimeTick? tick = null;

        When()
            .Match<TimeTick>(() => tick)
            .Match<MessageFact>(() => fact, f => (tick!.CurrentTime - f.Timestamp).TotalSeconds > 30);

        Then()
            .Do(ctx => RetractAndRelease(ctx, fact!));
    }

    private void RetractAndRelease(IContext ctx, MessageFact fact)
    {
        ctx.Retract(fact);
        if (fact.Message.Tracker != null)
        {
            fact.Message.Release();
        }
        fact.Clear();
    }
}

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
        CursorFact? fact = null;
        TimeTick? tick = null;

        When()
            .Match<TimeTick>(() => tick)
            .Match<CursorFact>(() => fact, f => !f.IsClosed && (tick!.CurrentTime - f.LastActivity).TotalHours > 24);

        Then()
            .Do(ctx => ctx.Retract(fact!));
    }
}
