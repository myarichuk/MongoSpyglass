using System.Buffers.Text;
using System.Text;
using System.Text.Json;
using Spyglass.Collector.Model;

namespace Spyglass.Collector.Ingest;

/// <summary><see cref="Invalid"/>: not a JSON object at all. <see cref="Rejected"/>: JSON, but not a valid record.</summary>
public enum ParseResult : byte { Ok, Ignored, Rejected, Invalid }

/// <summary>
/// One NDJSON line → a typed <see cref="Record"/> (02 §4), with the 03 §2 rules: unknown types are
/// ignored, unknown fields are kept verbatim in <see cref="Record.Extra"/>, records missing a required
/// field or carrying a field of the wrong JSON type are rejected. Absent fields keep their defaults.
/// </summary>
public static class RecordParser
{
    private sealed class Malformed(string why) : Exception(why);
    private const string NotAnObject = "not an object";

    public static ParseResult Parse(ReadOnlySpan<byte> line, out Record? record, out string? error)
    {
        record = null;
        error = null;
        try
        {
            var type = FindType(line);
            Record? r = type switch
            {
                "op" => new OpRecord(),
                "conn" => new ConnRecord(),
                "stream" => new StreamRecord(),
                "proxy" => new ProxyRecord(),
                "stats" => new StatsRecord(),
                "hello" or "cmd" or "callsite" or "uow" or "pool" => new ContextRecord { Type = type },
                null => throw new Malformed("no type"),
                _ => null,
            };
            if (r == null)
                return ParseResult.Ignored;
            Fill(line, r);
            r.Size = line.Length + 256;
            record = r;
            return ParseResult.Ok;
        }
        catch (JsonException e)
        {
            error = e.Message;
            return ParseResult.Invalid;
        }
        catch (Exception e) when (e is Malformed or InvalidOperationException or FormatException)
        {
            error = e.Message;
            return e.Message == NotAnObject ? ParseResult.Invalid : ParseResult.Rejected;
        }
    }

    private static string? FindType(ReadOnlySpan<byte> line)
    {
        var reader = new Utf8JsonReader(line);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) throw new Malformed(NotAnObject);
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var isType = reader.ValueTextEquals("type"u8);
            reader.Read();
            if (isType) return reader.TokenType == JsonTokenType.String ? reader.GetString() : throw new Malformed("type is not a string");
            reader.Skip();
        }
        return null;
    }

    private static void Fill(ReadOnlySpan<byte> line, Record r)
    {
        var reader = new Utf8JsonReader(line);
        reader.Read();
        StringBuilder? extra = null;
        bool hasTs = false, hasSrc = false;
        var seen = new Required();
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var nameStart = (int)reader.TokenStartIndex;
            if (reader.ValueTextEquals("type"u8)) { reader.Read(); continue; }
            if (reader.ValueTextEquals("ts"u8)) { reader.Read(); r.Ts = Long(ref reader); hasTs = true; continue; }
            if (reader.ValueTextEquals("src"u8)) { reader.Read(); r.Src = Str(ref reader); hasSrc = true; continue; }

            var known = r switch
            {
                OpRecord op => Op(ref reader, op, ref seen),
                ConnRecord c => Conn(ref reader, c, ref seen),
                StreamRecord s => Stream(ref reader, s, ref seen),
                ProxyRecord p => Proxy(ref reader, p),
                StatsRecord st => Stats(ref reader, st),
                _ => false,
            };
            if (known) continue;

            // unknown (or, for context records in M1, every) field: keep it verbatim
            reader.Read();
            reader.Skip();
            var end = (int)reader.BytesConsumed;
            extra ??= new StringBuilder("{");
            if (extra.Length > 1) extra.Append(',');
            extra.Append(Encoding.UTF8.GetString(line[nameStart..end]));
        }
        if (!hasTs) throw new Malformed("ts missing");
        if (!hasSrc || r.Src.Length == 0) throw new Malformed("src missing");
        seen.Check(r);
        if (extra != null) r.Extra = extra.Append('}').ToString();
    }

    /// <summary>02 §4.2 required fields per type.</summary>
    private struct Required
    {
        public bool Member, RequestId, ConnSeq, Command, Status, ReqBytes, Event, Seq;

        public readonly void Check(Record r)
        {
            switch (r)
            {
                case OpRecord when !(Member && RequestId && ConnSeq && Command && Status && ReqBytes):
                    throw new Malformed("op: member, requestId, connSeq, command, status and reqBytes are required");
                case ConnRecord when !(Member && ConnSeq && Event):
                    throw new Malformed("conn: event, member and connSeq are required");
                case StreamRecord when !(Member && RequestId && Seq):
                    throw new Malformed("stream: member, requestId and seq are required");
            }
        }
    }

    // Each returns false when the property is not one of its fields (the reader is then still on the name).

    private static bool Op(ref Utf8JsonReader x, OpRecord op, ref Required seen)
    {
        if (x.ValueTextEquals("member"u8)) { x.Read(); op.Member = Str(ref x); seen.Member = true; }
        else if (x.ValueTextEquals("serverConnectionId"u8)) { x.Read(); op.ServerConnectionId = NLong(ref x); }
        else if (x.ValueTextEquals("requestId"u8)) { x.Read(); op.RequestId = Int(ref x); seen.RequestId = true; }
        else if (x.ValueTextEquals("connSeq"u8)) { x.Read(); op.ConnSeq = Long(ref x); seen.ConnSeq = true; }
        else if (x.ValueTextEquals("opCode"u8)) { x.Read(); op.OpCode = NStr(ref x) ?? "OP_MSG"; }
        else if (x.ValueTextEquals("command"u8)) { x.Read(); op.Command = Str(ref x); seen.Command = true; }
        else if (x.ValueTextEquals("db"u8)) { x.Read(); op.Db = NStr(ref x); }
        else if (x.ValueTextEquals("coll"u8)) { x.Read(); op.Coll = NStr(ref x); }
        else if (x.ValueTextEquals("stream"u8)) { x.Read(); op.Stream = Bool(ref x); }
        else if (x.ValueTextEquals("status"u8)) { x.Read(); op.Status = Str(ref x); seen.Status = true; }
        else if (x.ValueTextEquals("reqBytes"u8)) { x.Read(); op.ReqBytes = Int(ref x); seen.ReqBytes = true; }
        else if (x.ValueTextEquals("respBytes"u8)) { x.Read(); op.RespBytes = Int(ref x); }
        else if (x.ValueTextEquals("t"u8))
        {
            x.Read();
            Object(ref x);
            while (x.Read() && x.TokenType == JsonTokenType.PropertyName)
            {
                if (x.ValueTextEquals("reqLast_us"u8)) { x.Read(); op.ReqLastUs = Long(ref x); }
                else if (x.ValueTextEquals("respFirst_us"u8)) { x.Read(); op.RespFirstUs = NLong(ref x); }
                else if (x.ValueTextEquals("respLast_us"u8)) { x.Read(); op.RespLastUs = NLong(ref x); }
                else { x.Read(); x.Skip(); }
            }
        }
        else if (x.ValueTextEquals("reply"u8)) { x.Read(); op.Reply = x.TokenType == JsonTokenType.Null ? null : Reply(ref x); }
        else if (x.ValueTextEquals("lsid"u8)) { x.Read(); op.Lsid = NStr(ref x); }
        else if (x.ValueTextEquals("txnNumber"u8)) { x.Read(); op.TxnNumber = NLong(ref x); }
        else if (x.ValueTextEquals("comment"u8)) { x.Read(); op.Comment = NStr(ref x); }
        else if (x.ValueTextEquals("cmd"u8)) { x.Read(); op.Cmd = x.TokenType == JsonTokenType.Null ? null : x.GetBytesFromBase64(); }
        else if (x.ValueTextEquals("cmdTruncated"u8)) { x.Read(); op.CmdTruncated = Bool(ref x); }
        else if (x.ValueTextEquals("compressor"u8)) { x.Read(); op.Compressor = NStr(ref x); }
        else if (x.ValueTextEquals("seqDocs"u8))
        {
            x.Read();
            if (x.TokenType == JsonTokenType.Null) return true;
            Object(ref x);
            op.SeqDocs = [];
            while (x.Read() && x.TokenType == JsonTokenType.PropertyName)
            {
                var id = x.GetString()!;
                x.Read();
                op.SeqDocs.Add((id, Int(ref x)));
            }
        }
        else return false;
        return true;
    }

    private static ReplyFacts Reply(ref Utf8JsonReader x)
    {
        Object(ref x);
        var f = new ReplyFacts();
        while (x.Read() && x.TokenType == JsonTokenType.PropertyName)
        {
            if (x.ValueTextEquals("ok"u8)) { x.Read(); f.Ok = NDouble(ref x); }
            else if (x.ValueTextEquals("code"u8)) { x.Read(); f.Code = (int?)NLong(ref x); }
            else if (x.ValueTextEquals("codeName"u8)) { x.Read(); f.CodeName = NStr(ref x); }
            else if (x.ValueTextEquals("errmsg"u8)) { x.Read(); f.Errmsg = NStr(ref x); }
            else if (x.ValueTextEquals("cursorId"u8)) { x.Read(); f.CursorId = CursorId(ref x); }
            else if (x.ValueTextEquals("nReturned"u8)) { x.Read(); f.NReturned = (int?)NLong(ref x); }
            else if (x.ValueTextEquals("n"u8)) { x.Read(); f.N = NLong(ref x); }
            else if (x.ValueTextEquals("nModified"u8)) { x.Read(); f.NModified = NLong(ref x); }
            else if (x.ValueTextEquals("writeErrors"u8)) { x.Read(); f.WriteErrors = (int)(NLong(ref x) ?? 0); }
            else if (x.ValueTextEquals("writeConcernError"u8)) { x.Read(); f.WriteConcernError = Bool(ref x); }
            else if (x.ValueTextEquals("upserted"u8)) { x.Read(); f.Upserted = (int)(NLong(ref x) ?? 0); }
            else if (x.ValueTextEquals("cursorsKilled"u8)) { x.Read(); f.CursorsKilled = (int)(NLong(ref x) ?? 0); }
            else if (x.ValueTextEquals("cursorsNotFound"u8)) { x.Read(); f.CursorsNotFound = (int)(NLong(ref x) ?? 0); }
            else if (x.ValueTextEquals("parsed"u8)) { x.Read(); f.Parsed = x.TokenType != JsonTokenType.False; }
            else { x.Read(); x.Skip(); }
        }
        return f;
    }

    private static bool Conn(ref Utf8JsonReader x, ConnRecord c, ref Required seen)
    {
        if (x.ValueTextEquals("event"u8)) { x.Read(); c.Event = Str(ref x); seen.Event = true; }
        else if (x.ValueTextEquals("member"u8)) { x.Read(); c.Member = Str(ref x); seen.Member = true; }
        else if (x.ValueTextEquals("connSeq"u8)) { x.Read(); c.ConnSeq = Long(ref x); seen.ConnSeq = true; }
        else if (x.ValueTextEquals("serverConnectionId"u8)) { x.Read(); c.ServerConnectionId = NLong(ref x); }
        else if (x.ValueTextEquals("client"u8)) { x.Read(); c.Client = NStr(ref x); }
        else if (x.ValueTextEquals("tls"u8)) { x.Read(); c.Tls = Bool(ref x); }
        else if (x.ValueTextEquals("app"u8)) { x.Read(); c.App = NStr(ref x); }
        else if (x.ValueTextEquals("driver"u8)) { x.Read(); c.Driver = NStr(ref x); }
        else if (x.ValueTextEquals("driverVersion"u8)) { x.Read(); c.DriverVersion = NStr(ref x); }
        else if (x.ValueTextEquals("exporter"u8)) { x.Read(); c.Exporter = NStr(ref x); }
        else if (x.ValueTextEquals("os"u8)) { x.Read(); c.Os = NStr(ref x); }
        else if (x.ValueTextEquals("platform"u8)) { x.Read(); c.Platform = NStr(ref x); }
        else if (x.ValueTextEquals("closeReason"u8)) { x.Read(); c.CloseReason = NStr(ref x); }
        else if (x.ValueTextEquals("bytesIn"u8)) { x.Read(); c.BytesIn = NLong(ref x) ?? 0; }
        else if (x.ValueTextEquals("bytesOut"u8)) { x.Read(); c.BytesOut = NLong(ref x) ?? 0; }
        else return false;
        return true;
    }

    private static bool Stream(ref Utf8JsonReader x, StreamRecord s, ref Required seen)
    {
        if (x.ValueTextEquals("member"u8)) { x.Read(); s.Member = Str(ref x); seen.Member = true; }
        else if (x.ValueTextEquals("serverConnectionId"u8)) { x.Read(); s.ServerConnectionId = NLong(ref x); }
        else if (x.ValueTextEquals("requestId"u8)) { x.Read(); s.RequestId = Int(ref x); seen.RequestId = true; }
        else if (x.ValueTextEquals("seq"u8)) { x.Read(); s.Seq = Int(ref x); seen.Seq = true; }
        else if (x.ValueTextEquals("respBytes"u8)) { x.Read(); s.RespBytes = (int)(NLong(ref x) ?? 0); }
        else if (x.ValueTextEquals("reply"u8))
        {
            x.Read();
            if (x.TokenType == JsonTokenType.Null) return true;
            var f = Reply(ref x);
            (s.CursorId, s.NReturned, s.Ok) = (f.CursorId, f.NReturned, f.Ok);
        }
        else return false;
        return true;
    }

    private static bool Proxy(ref Utf8JsonReader x, ProxyRecord p)
    {
        if (x.ValueTextEquals("version"u8)) { x.Read(); p.Version = NStr(ref x) ?? ""; }
        else if (x.ValueTextEquals("schema"u8)) { x.Read(); p.Schema = (int)(NLong(ref x) ?? 0); }
        else if (x.ValueTextEquals("topology"u8)) { x.Read(); p.Topology = NStr(ref x) ?? ""; }
        else if (x.ValueTextEquals("setName"u8)) { x.Read(); p.SetName = NStr(ref x); }
        else if (x.ValueTextEquals("members"u8))
        {
            x.Read();
            if (x.TokenType != JsonTokenType.StartArray) throw new Malformed("members is not an array");
            while (x.Read() && x.TokenType == JsonTokenType.StartObject)
            {
                string member = "", upstream = "";
                while (x.Read() && x.TokenType == JsonTokenType.PropertyName)
                {
                    if (x.ValueTextEquals("member"u8)) { x.Read(); member = NStr(ref x) ?? ""; }
                    else if (x.ValueTextEquals("upstream"u8)) { x.Read(); upstream = NStr(ref x) ?? ""; }
                    else { x.Read(); x.Skip(); }
                }
                p.Members.Add((member, upstream));
            }
        }
        else if (x.ValueTextEquals("config"u8))
        {
            x.Read();
            Object(ref x);
            while (x.Read() && x.TokenType == JsonTokenType.PropertyName)
            {
                if (x.ValueTextEquals("compression"u8)) { x.Read(); p.Compression = NStr(ref x); }
                else if (x.ValueTextEquals("values"u8)) { x.Read(); p.Values = NStr(ref x); }
                else { x.Read(); x.Skip(); }
            }
        }
        else return false;
        return true;
    }

    private static bool Stats(ref Utf8JsonReader x, StatsRecord s)
    {
        if (x.ValueTextEquals("dropped"u8))
        {
            x.Read();
            Object(ref x);
            while (x.Read() && x.TokenType == JsonTokenType.PropertyName)
            {
                var k = x.GetString()!;
                x.Read();
                s.Dropped[k] = NLong(ref x) ?? 0;
            }
        }
        else if (x.ValueTextEquals("sent"u8))
        {
            x.Read();
            Object(ref x);
            while (x.Read() && x.TokenType == JsonTokenType.PropertyName)
            {
                if (x.ValueTextEquals("records"u8)) { x.Read(); s.SentRecords = NLong(ref x) ?? 0; }
                else if (x.ValueTextEquals("batches"u8)) { x.Read(); s.SentBatches = NLong(ref x) ?? 0; }
                else { x.Read(); x.Skip(); }
            }
        }
        else return false;
        return true;
    }

    // ---- typed value readers: wrong JSON type → the record is rejected ----

    private static void Object(ref Utf8JsonReader x)
    {
        if (x.TokenType != JsonTokenType.StartObject) throw new Malformed("expected an object");
    }

    private static string Str(ref Utf8JsonReader x) =>
        x.TokenType == JsonTokenType.String ? x.GetString()! : throw new Malformed("expected a string");

    private static string? NStr(ref Utf8JsonReader x) => x.TokenType == JsonTokenType.Null ? null : Str(ref x);

    private static long Long(ref Utf8JsonReader x) =>
        x.TokenType == JsonTokenType.Number && x.TryGetInt64(out var v) ? v : throw new Malformed("expected an integer");

    private static long? NLong(ref Utf8JsonReader x) => x.TokenType == JsonTokenType.Null ? null : Long(ref x);

    private static int Int(ref Utf8JsonReader x) =>
        x.TokenType == JsonTokenType.Number && x.TryGetInt32(out var v) ? v : throw new Malformed("expected an int32");

    private static double? NDouble(ref Utf8JsonReader x) =>
        x.TokenType switch
        {
            JsonTokenType.Null => null,
            JsonTokenType.Number => x.GetDouble(),
            _ => throw new Malformed("expected a number"),
        };

    private static bool Bool(ref Utf8JsonReader x) =>
        x.TokenType switch
        {
            JsonTokenType.True => true,
            JsonTokenType.False or JsonTokenType.Null => false,
            _ => throw new Malformed("expected a boolean"),
        };

    /// <summary>02 §4: int64 identifiers are decimal strings.</summary>
    private static long? CursorId(ref Utf8JsonReader x)
    {
        if (x.TokenType == JsonTokenType.Null) return null;
        if (x.TokenType != JsonTokenType.String) throw new Malformed("cursorId must be a decimal string");
        return Utf8Parser.TryParse(x.ValueSpan, out long v, out var n) && n == x.ValueSpan.Length ? v : throw new Malformed("cursorId must be a decimal string");
    }
}
