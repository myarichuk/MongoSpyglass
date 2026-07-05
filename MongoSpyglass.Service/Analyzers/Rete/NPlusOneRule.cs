using NRules.Fluent.Dsl;
using NRules.RuleModel;
using MongoSpyglass.Service.Analyzers.Rete;
using System;

namespace MongoSpyglass.Service.Analyzers.Rete;

public class TrackN1ShapeRule : Rule
{
    public override void Define()
    {
        RequestObservedFact? req = null;
        RequestWindowFact? window = null;
        N1DetectionThresholdFact? threshold = null;
        TimeTick? tick = null;

        When()
            .Match<N1DetectionThresholdFact>(() => threshold)
            .Match<TimeTick>(() => tick)
            .Match<RequestObservedFact>(() => req)
            .Match<RequestWindowFact>(() => window,
                w => w.Namespace == req!.Collection &&
                     w.ConnectionId == req!.ConnectionId &&
                     w.Hash == req!.ShapeHash &&
                     (tick!.CurrentTime - w.LastSeen).TotalMilliseconds < threshold!.WindowMs);

        Then()
            .Do(ctx => UpdateN1Window(ctx, req!, window!, tick!));
    }

    private void UpdateN1Window(IContext ctx, RequestObservedFact req, RequestWindowFact window, TimeTick tick)
    {
        window.LastSeen = tick.CurrentTime;
        window.Count++;

        // Track different ValueHashes (indicates different values with same structure)
        if (!window.ExampleHashes.Contains(req.ValueHash))
        {
            window.ExampleHashes.Add(req.ValueHash);
        }

        ctx.Update(window);
    }
}

public class InitN1ShapeWindowRule : Rule
{
    public override void Define()
    {
        RequestObservedFact? req = null;
        N1DetectionThresholdFact? threshold = null;
        TimeTick? tick = null;

        When()
            .Match<N1DetectionThresholdFact>(() => threshold)
            .Match<TimeTick>(() => tick)
            .Match<RequestObservedFact>(() => req)
            .Not<RequestWindowFact>(w =>
                w.Namespace == req!.Collection &&
                w.ConnectionId == req!.ConnectionId &&
                w.Hash == req!.ShapeHash);

        Then()
            .Do(ctx => CreateN1Window(ctx, req!, tick!));
    }

    private void CreateN1Window(IContext ctx, RequestObservedFact req, TimeTick tick)
    {
        var window = new RequestWindowFact
        {
            Key = $"{req.Collection}|{req.ConnectionId}|{req.ShapeHash}",
            Namespace = req.Collection,
            ConnectionId = req.ConnectionId,
            Hash = req.ShapeHash,
            FirstSeen = tick.CurrentTime,
            LastSeen = tick.CurrentTime,
            Count = 1,
            ExampleHashes = new() { req.ValueHash }
        };
        ctx.Insert(window);
    }
}

public class DetectN1PatternRule : Rule
{
    public override void Define()
    {
        RequestWindowFact? window = null;
        N1DetectionThresholdFact? threshold = null;

        When()
            .Match<N1DetectionThresholdFact>(() => threshold)
            .Match<RequestWindowFact>(() => window,
                w => w.Count >= threshold!.CountThreshold &&
                     w.ExampleHashes.Count > 1); // Different values with same structure

        Then()
            .Do(ctx => EmitN1Alert(ctx, window!, threshold!));
    }

    private void EmitN1Alert(IContext ctx, RequestWindowFact window, N1DetectionThresholdFact threshold)
    {
        var insight = new Insight(
            "Possible N+1 Query Pattern Detected",
            $"Connection {window.ConnectionId} executing {window.Count} similar queries on {window.Namespace} " +
            $"with {window.ExampleHashes.Count} different value sets",
            InsightLevel.Warning,
            $"Namespace: {window.Namespace}\nConnection: {window.ConnectionId}\n" +
            $"Query Count: {window.Count}\nUnique Value Sets: {window.ExampleHashes.Count}\n" +
            $"Detection Window: {threshold.WindowMs}ms\nThreshold: {threshold.CountThreshold} queries",
            Category: "N+1 Detection"
        );
        ctx.Insert(insight);
    }
}

public class TrackDuplicateQueryRule : Rule
{
    public override void Define()
    {
        RequestObservedFact? req = null;
        RequestWindowFact? window = null;
        N1DetectionThresholdFact? threshold = null;
        TimeTick? tick = null;

        When()
            .Match<N1DetectionThresholdFact>(() => threshold)
            .Match<TimeTick>(() => tick)
            .Match<RequestObservedFact>(() => req)
            .Match<RequestWindowFact>(() => window,
                w => w.Namespace == req!.Collection &&
                     w.ConnectionId == req!.ConnectionId &&
                     w.Hash == req!.ValueHash && // Exact same query (value hash)
                     (tick!.CurrentTime - w.LastSeen).TotalMilliseconds < threshold!.WindowMs);

        Then()
            .Do(ctx => UpdateDuplicateWindow(ctx, window!, tick!));
    }

    private void UpdateDuplicateWindow(IContext ctx, RequestWindowFact window, TimeTick tick)
    {
        window.LastSeen = tick.CurrentTime;
        window.Count++;
        ctx.Update(window);
    }
}

public class InitDuplicateQueryWindowRule : Rule
{
    public override void Define()
    {
        RequestObservedFact? req = null;
        N1DetectionThresholdFact? threshold = null;
        TimeTick? tick = null;

        When()
            .Match<N1DetectionThresholdFact>(() => threshold)
            .Match<TimeTick>(() => tick)
            .Match<RequestObservedFact>(() => req)
            .Not<RequestWindowFact>(w =>
                w.Namespace == req!.Collection &&
                w.ConnectionId == req!.ConnectionId &&
                w.Hash == req!.ValueHash);

        Then()
            .Do(ctx => CreateDuplicateWindow(ctx, req!, tick!));
    }

    private void CreateDuplicateWindow(IContext ctx, RequestObservedFact req, TimeTick tick)
    {
        var window = new RequestWindowFact
        {
            Key = $"{req.Collection}|{req.ConnectionId}|{req.ValueHash}",
            Namespace = req.Collection,
            ConnectionId = req.ConnectionId,
            Hash = req.ValueHash,
            FirstSeen = tick.CurrentTime,
            LastSeen = tick.CurrentTime,
            Count = 1
        };
        ctx.Insert(window);
    }
}

public class DetectDuplicateQueryRule : Rule
{
    public override void Define()
    {
        RequestWindowFact? window = null;
        N1DetectionThresholdFact? threshold = null;

        When()
            .Match<N1DetectionThresholdFact>(() => threshold)
            .Match<RequestWindowFact>(() => window,
                w => w.Count >= threshold!.CountThreshold &&
                     w.ExampleHashes.Count <= 1); // Same query repeated (value hash match)

        Then()
            .Do(ctx => EmitDuplicateAlert(ctx, window!, threshold!));
    }

    private void EmitDuplicateAlert(IContext ctx, RequestWindowFact window, N1DetectionThresholdFact threshold)
    {
        var insight = new Insight(
            "Repeated Identical Query Detected",
            $"Connection {window.ConnectionId} executing {window.Count} identical queries on {window.Namespace} " +
            $"within {threshold.WindowMs}ms — consider caching",
            InsightLevel.Warning,
            $"Namespace: {window.Namespace}\nConnection: {window.ConnectionId}\n" +
            $"Query Count: {window.Count}\nDetection Window: {threshold.WindowMs}ms\n" +
            $"Threshold: {threshold.CountThreshold} queries",
            Category: "Duplicate Query"
        );
        ctx.Insert(insight);
    }
}

public class CleanupExpiredRequestWindowRule : Rule
{
    public override void Define()
    {
        RequestWindowFact? window = null;
        TimeTick? tick = null;
        N1DetectionThresholdFact? threshold = null;

        When()
            .Match<TimeTick>(() => tick)
            .Match<N1DetectionThresholdFact>(() => threshold)
            .Match<RequestWindowFact>(() => window,
                w => (tick!.CurrentTime - w.LastSeen).TotalMilliseconds > threshold!.WindowMs);

        Then()
            .Do(ctx => ctx.Retract(window!));
    }
}
