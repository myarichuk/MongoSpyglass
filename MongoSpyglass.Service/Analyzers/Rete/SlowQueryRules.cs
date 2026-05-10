using NRules.Fluent.Dsl;
using NRules.RuleModel;
using MongoSpyglass.Service.Analyzers.Rete;
using System.Linq;
using System;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.IO;

namespace MongoSpyglass.Service.Analyzers.Rete;

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

public class CleanupPendingRequestRule : Rule
{
    public override void Define()
    {
        MessageFact? msg = null;
        PendingRequestFact? pending = null;

        When()
            .Match<MessageFact>(() => msg, m => m.Message.Tag == "from" && m.Message.DurationMs <= 100)
            .Match<PendingRequestFact>(() => pending, p => p.RequestId == msg!.Message.ResponseTo);

        Then()
            .Do(ctx => ctx.Retract(pending!));
    }
}
