using NRules.Fluent.Dsl;
using NRules.RuleModel;
using MongoSpyglass.Service.Analyzers.Rete;
using System.Linq;
using System;

namespace MongoSpyglass.Service.Analyzers.Rete;

public class TrackRequestRule : Rule
{
    public override void Define()
    {
        RequestObservedFact? req = null;

        When()
            .Match<RequestObservedFact>(() => req);

        Then()
            .Do(ctx => TrackRequest(ctx, req!));
    }

    private void TrackRequest(IContext ctx, RequestObservedFact req)
    {
        ctx.Insert(new PendingRequestFact
        {
            RequestId = req.RequestId,
            ConnectionId = req.ConnectionId,
            Command = req.Command,
            Collection = req.Collection,
            Timestamp = req.Timestamp
        });
    }
}

public class DetectSlowQueryRule : Rule
{
    public override void Define()
    {
        ResponseObservedFact? resp = null;
        PendingRequestFact? pending = null;
        RequestObservedFact? req = null;
        SettingsSnapshotFact? settings = null;

        When()
            .Match<SettingsSnapshotFact>(() => settings)
            .Match<RequestObservedFact>(() => req)
            .Match<ResponseObservedFact>(() => resp, r => r.DurationMs > settings!.SlowQueryThresholdMs)
            .Match<PendingRequestFact>(() => pending,
                p => p.RequestId == resp!.RequestId && p.ConnectionId == resp!.ConnectionId &&
                     p.RequestId == req!.RequestId && p.ConnectionId == req!.ConnectionId);

        Then()
            .Do(ctx => GenerateSlowQueryInsight(ctx, resp!, pending!, req!));
    }

    private void GenerateSlowQueryInsight(IContext ctx, ResponseObservedFact resp, PendingRequestFact pending, RequestObservedFact req)
    {
        var insight = new Insight(
            "Slow Query Detected",
            $"Slow {pending.Command} on {pending.Collection} detected: {resp.DurationMs:F2}ms",
            InsightLevel.Warning,
            $"Total Latency: {resp.DurationMs:F2}ms\nSize: {resp.MessageSizeBytes} bytes\nDocuments: {resp.DocumentCount}",
            Category: "Performance"
        );
        ctx.Insert(insight);

        // Queue example for persistence
        ctx.Insert(new QueryExampleToSaveFact
        {
            Hash = req.ValueHash,
            Kind = "SlowQuery",
            ExampleJson = req.ExampleJson,
            Namespace = pending.Collection,
            Command = pending.Command
        });

        ctx.Retract(pending);
    }
}

public class CleanupPendingRequestRule : Rule
{
    public override void Define()
    {
        ResponseObservedFact? resp = null;
        PendingRequestFact? pending = null;

        When()
            .Match<ResponseObservedFact>(() => resp, r => r.DurationMs <= 100)
            .Match<PendingRequestFact>(() => pending,
                p => p.RequestId == resp!.RequestId && p.ConnectionId == resp!.ConnectionId);

        Then()
            .Do(ctx => ctx.Retract(pending!));
    }
}
