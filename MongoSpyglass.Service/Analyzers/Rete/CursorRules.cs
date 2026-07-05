using NRules.Fluent.Dsl;
using NRules.RuleModel;
using MongoSpyglass.Service.Analyzers.Rete;
using System.Linq;
using System;

namespace MongoSpyglass.Service.Analyzers.Rete;

public class DetectNewCursorRule : Rule
{
    public override void Define()
    {
        ResponseObservedFact? resp = null;
        SessionFact? session = null;

        When()
            .Match<ResponseObservedFact>(() => resp, r => r.CursorId.HasValue && r.CursorId > 0)
            .Match<SessionFact>(() => session)
            .Not<CursorFact>(c => c.Id == resp!.CursorId && c.ConnectionId == resp!.ConnectionId);

        Then()
            .Do(ctx => ProcessCursorResponse(ctx, resp!, session!));
    }

    private void ProcessCursorResponse(IContext ctx, ResponseObservedFact resp, SessionFact session)
    {
        if (!resp.CursorId.HasValue || resp.CursorId <= 0) return;

        var ns = resp.Namespace ?? "unknown";
        var cursor = new CursorFact
        {
            Id = resp.CursorId.Value,
            Namespace = ns,
            ConnectionId = resp.ConnectionId,
            SessionId = session.Id,
            StartTime = resp.Timestamp,
            TotalBytes = resp.MessageSizeBytes,
            TotalDocs = resp.DocumentCount
        };
        ctx.Insert(cursor);
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
            ctx.Retract(cursor); 
        }
    }
}

public class TrackGetMoreRequestRule : Rule
{
    public override void Define()
    {
        GetMoreRequestedFact? getMore = null;
        CursorFact? cursor = null;

        When()
            .Match<GetMoreRequestedFact>(() => getMore)
            .Match<CursorFact>(() => cursor, c => c.Id == getMore!.CursorId && c.ConnectionId == getMore!.ConnectionId);

        Then()
            .Do(ctx => ProcessGetMoreRequest(ctx, getMore!, cursor!));
    }

    private void ProcessGetMoreRequest(IContext ctx, GetMoreRequestedFact getMore, CursorFact cursor)
    {
        ctx.Insert(new PendingGetMoreFact
        {
            RequestId = getMore.RequestId,
            ConnectionId = getMore.ConnectionId,
            CursorId = getMore.CursorId,
            Timestamp = getMore.Timestamp
        });

        cursor.LastActivity = getMore.Timestamp;
        ctx.Update(cursor);
    }
}

public class HandleGetMoreResponseRule : Rule
{
    public override void Define()
    {
        ResponseObservedFact? resp = null;
        PendingGetMoreFact? pending = null;
        CursorFact? cursor = null;

        When()
            .Match<ResponseObservedFact>(() => resp)
            .Match<PendingGetMoreFact>(() => pending,
                p => p.RequestId == resp!.RequestId && p.ConnectionId == resp!.ConnectionId)
            .Match<CursorFact>(() => cursor,
                c => c.Id == pending!.CursorId && c.ConnectionId == pending!.ConnectionId);

        Then()
            .Do(ctx => UpdateCursor(ctx, resp!, pending!, cursor!));
    }

    private void UpdateCursor(IContext ctx, ResponseObservedFact resp, PendingGetMoreFact pending, CursorFact cursor)
    {
        ctx.Retract(pending);
        cursor.TotalBytes += resp.MessageSizeBytes;
        cursor.TotalDocs += resp.DocumentCount;
        cursor.LastActivity = resp.Timestamp;

        // Check if cursor was exhausted (cursor ID == 0 in response)
        if (resp.CursorId == 0)
        {
            cursor.IsClosed = true;
            cursor.ClosureReason = "Exhausted";
            cursor.ClosedAt = DateTime.UtcNow;
        }

        ctx.Update(cursor);
    }
}

public class ProcessKillCursorsRequestRule : Rule
{
    public override void Define()
    {
        KillCursorsRequestedFact? killReq = null;

        When()
            .Match<KillCursorsRequestedFact>(() => killReq);

        Then()
            .Do(ctx => ProcessKillRequest(ctx, killReq!));
    }

    private void ProcessKillRequest(IContext ctx, KillCursorsRequestedFact killReq)
    {
        ctx.Insert(new PendingKillFact
        {
            CursorId = killReq.CursorId,
            ConnectionId = killReq.ConnectionId
        });
        ctx.Retract(killReq);
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

public class ConnectionClosedRule : Rule
{
    public override void Define()
    {
        ConnectionClosedFact? connClosed = null;
        CursorFact? cursor = null;

        When()
            .Match<ConnectionClosedFact>(() => connClosed)
            .Match<CursorFact>(() => cursor, c => c.ConnectionId == connClosed!.ConnectionId && !c.IsClosed);

        Then()
            .Do(ctx => CloseCursor(ctx, connClosed!, cursor!));
    }

    private void CloseCursor(IContext ctx, ConnectionClosedFact conn, CursorFact cursor)
    {
        cursor.IsClosed = true;
        cursor.ClosureReason = "Connection Closed";
        cursor.ClosedAt = DateTime.UtcNow;
        cursor.OrphanedByDisconnect = true;
        ctx.Update(cursor);
    }
}

public class CursorLeakAlertRule : Rule
{
    public override void Define()
    {
        CursorFact? cursor = null;
        TimeTick? tick = null;
        CursorLeakAlertThresholdFact? threshold = null;

        When()
            .Match<TimeTick>(() => tick)
            .Match<CursorLeakAlertThresholdFact>(() => threshold)
            .Match<CursorFact>(() => cursor,
                c => !c.IsClosed &&
                     (tick!.CurrentTime - c.LastActivity).TotalHours > threshold!.IdleHoursThreshold);

        Then()
            .Do(ctx => EmitLeakAlert(ctx, cursor!, tick!, threshold!));
    }

    private void EmitLeakAlert(IContext ctx, CursorFact cursor, TimeTick tick, CursorLeakAlertThresholdFact threshold)
    {
        var idleHours = (tick.CurrentTime - cursor.LastActivity).TotalHours;
        var insight = new Insight(
            "Cursor Leak Detected",
            $"Cursor {cursor.Id} on {cursor.Namespace} from connection {cursor.ConnectionId} idle for {idleHours:F1}h",
            InsightLevel.Critical,
            $"Namespace: {cursor.Namespace}\nCursor ID: {cursor.Id}\nConnection: {cursor.ConnectionId}\n" +
            $"Idle Duration: {idleHours:F1} hours\nThreshold: {threshold.IdleHoursThreshold}h\n" +
            $"Orphaned: {cursor.OrphanedByDisconnect}\nStarted: {cursor.StartTime:O}",
            Category: "Cursor Leak"
        );
        ctx.Insert(insight);
    }
}
