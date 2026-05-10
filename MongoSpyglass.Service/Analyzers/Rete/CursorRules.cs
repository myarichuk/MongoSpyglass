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
        MessageFact? msg = null;
        SessionFact? session = null;

        When()
            .Match<MessageFact>(() => msg, m => m.Message.Tag == "from" && !m.Message.Document.IsDefault && HasCursor(m))
            .Match<SessionFact>(() => session)
            .Not<CursorFact>(c => c.Id == GetCursorIdFromResponse(msg!) && c.ConnectionId == msg!.Message.ConnectionId);

        Then()
            .Do(ctx => ProcessCursorResponse(ctx, msg!, session!));
    }

    private bool HasCursor(MessageFact m) => m.Message.Document.TryGetElementOffset("cursor", out _);

    private long GetCursorIdFromResponse(MessageFact msg)
    {
        try {
            if (msg.Message.Document.TryGetElementOffset("cursor", out var cursorOffset)) {
                var cursorDoc = msg.Message.Document.GetDocument(cursorOffset, msg.Message.Tracker.Arena);
                if (cursorDoc.TryGetElementOffset("id", out var idOffset)) {
                    return cursorDoc.GetInt64(idOffset);
                }
            }
        } catch {}
        return 0;
    }

    private void ProcessCursorResponse(IContext ctx, MessageFact msg, SessionFact session)
    {
        long id = GetCursorIdFromResponse(msg);
        if (id <= 0) return;

        try {
            if (msg.Message.Document.TryGetElementOffset("cursor", out var cursorOffset)) {
                var cursorDoc = msg.Message.Document.GetDocument(cursorOffset, msg.Message.Tracker.Arena);
                string ns = "unknown";
                if (cursorDoc.TryGetElementOffset("ns", out var nsOffset)) ns = cursorDoc.GetString(nsOffset);
                
                var cursor = new CursorFact { 
                    Id = id, 
                    Namespace = ns, 
                    ConnectionId = msg.Message.ConnectionId, 
                    SessionId = session.Id,
                    StartTime = msg.Timestamp, 
                    TotalBytes = msg.Message.MessageSizeBytes, 
                    TotalDocs = msg.Message.DocumentCount 
                };
                ctx.Insert(cursor);
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
            ctx.Retract(cursor); 
        }
    }
}

public class TrackGetMoreRequestRule : Rule
{
    public override void Define()
    {
        MessageFact? msg = null;
        CursorFact? cursor = null;

        When()
            .Match<MessageFact>(() => msg, m => m.Message.Tag == "to" && !m.Message.Document.IsDefault && HasGetMore(m))
            .Match<CursorFact>(() => cursor, c => c.Id == GetCursorId(msg!) && c.ConnectionId == msg!.Message.ConnectionId);

        Then()
            .Do(ctx => ProcessGetMoreRequest(ctx, msg!, cursor!));
    }

    private bool HasGetMore(MessageFact m) => m.Message.Document.TryGetElementOffset("getMore", out _);
    
    private long GetCursorId(MessageFact m)
    {
        if (m.Message.Document.TryGetElementOffset("getMore", out var offset))
            return m.Message.Document.GetInt64(offset);
        return 0;
    }

    private void ProcessGetMoreRequest(IContext ctx, MessageFact msg, CursorFact cursor)
    {
        if (msg.Message.Document.TryGetElementOffset("getMore", out var offset))
        {
            long id = msg.Message.Document.GetInt64(offset);
            ctx.Insert(new PendingGetMoreFact { RequestId = msg.Message.RequestId, CursorId = id });
            
            cursor.LastActivity = msg.Timestamp;
            ctx.Update(cursor);
        }
    }
}

public class HandleGetMoreResponseRule : Rule
{
    public override void Define()
    {
        MessageFact? msg = null;
        PendingGetMoreFact? pending = null;
        CursorFact? cursor = null;

        When()
            .Match<MessageFact>(() => msg, m => m.Message.Tag == "from")
            .Match<PendingGetMoreFact>(() => pending, p => p.RequestId == msg!.Message.ResponseTo)
            .Match<CursorFact>(() => cursor, c => c.Id == pending!.CursorId);

        Then()
            .Do(ctx => UpdateCursor(ctx, msg!, pending!, cursor!));
    }

    private void UpdateCursor(IContext ctx, MessageFact msg, PendingGetMoreFact pending, CursorFact cursor)
    {
        ctx.Retract(pending);
        cursor.TotalBytes += msg.Message.MessageSizeBytes;
        cursor.TotalDocs += msg.Message.DocumentCount;
        cursor.LastActivity = msg.Timestamp;
        
        try {
            if (msg.Message.Document.TryGetElementOffset("cursor", out var cursorOffset)) {
                var cursorDoc = msg.Message.Document.GetDocument(cursorOffset, msg.Message.Tracker.Arena);
                if (cursorDoc.TryGetElementOffset("id", out var idOffset)) {
                    if (cursorDoc.GetInt64(idOffset) == 0) {
                        cursor.IsClosed = true;
                        cursor.ClosureReason = "Exhausted";
                        cursor.ClosedAt = DateTime.UtcNow;
                    }
                }
            }
        } catch {}
        ctx.Update(cursor);
    }
}

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
        ctx.Update(cursor);
    }
}
