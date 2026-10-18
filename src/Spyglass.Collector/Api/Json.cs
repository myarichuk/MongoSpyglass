using System.Buffers;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using Spyglass.Collector.Analysis;
using Spyglass.Collector.Model;

namespace Spyglass.Collector.Api;

/// <summary>API/SSE JSON for collector entities (03 §8). Field names follow the stored model (03 §3.3).</summary>
public static class Json
{
    private static readonly JsonWriterSettings Shell = new() { OutputMode = JsonOutputMode.RelaxedExtendedJson, Indent = true };

    public static byte[] Write(Action<Utf8JsonWriter> body)
    {
        var buf = new ArrayBufferWriter<byte>(512);
        using (var w = new Utf8JsonWriter(buf)) body(w);
        return buf.WrittenSpan.ToArray();
    }

    /// <summary>The live-feed row and the <c>/ops</c> list item.</summary>
    public static void OpSummary(Utf8JsonWriter w, Operation o)
    {
        var r = o.Op;
        w.WriteStartObject();
        w.WriteString("id", o.Id);
        w.WriteNumber("ts", r.Ts);
        w.WriteString("proxyId", r.ProxyId);
        w.WriteString("member", r.Member);
        if (o.App != null) w.WriteString("app", o.App);
        w.WriteString("command", r.Command);
        if (r.Db != null) w.WriteString("db", r.Db);
        if (r.Coll != null) w.WriteString("coll", r.Coll);
        w.WriteString("status", r.Status);
        if (r.ServerLatencyUs is { } lat) w.WriteNumber("serverLatency_us", lat);
        if (r.Reply?.NReturned is { } nr) w.WriteNumber("nReturned", nr);
        w.WriteNumber("reqBytes", r.ReqBytes);
        w.WriteNumber("respBytes", r.RespBytes);
        w.WriteString("shapeHash", o.ShapeHash);
        w.WriteNumber("replyCount", o.ReplyCount);
        if (r.Reply?.CodeName is { } cn) w.WriteString("codeName", cn);
        if (o.Alerts is { Count: > 0 })
        {
            w.WriteStartArray("alerts");
            foreach (var a in o.Alerts) w.WriteStringValue(a);
            w.WriteEndArray();
        }
        if (Volatile.Read(ref o.Ctx) is { } c)
        {
            w.WriteBoolean("joined", true);
            if (c.ClientUs is { } cu) w.WriteNumber("client_us", cu);
            if (c.PoolWaitUs is { } pw) w.WriteNumber("poolWait_us", pw);
            Opt(w, "uowId", c.UowId);
            Opt(w, "uowName", c.UowName);
        }
        w.WriteEndObject();
    }

    /// <summary>The joined <c>cmd</c> (03 §3.3 "from cmd"), with the callsite's frames when known.</summary>
    public static void Context(Utf8JsonWriter w, Operation o, CmdRecord c, CallsiteRecord? callsite)
    {
        w.WriteStartObject();
        w.WriteString("src", c.Src);
        w.WriteNumber("ts", c.Ts);
        Opt(w, "operationId", c.OperationId);
        w.WriteString("status", c.Status);
        if (c.ClientUs is { } cu) w.WriteNumber("client_us", cu);
        if (c.PoolWaitUs is { } pw) w.WriteNumber("poolWait_us", pw);
        if (o.ClientOverheadUs is { } co) w.WriteNumber("clientOverhead_us", co);
        if (c.Attempt is { } at) w.WriteNumber("attempt", at);
        if (c.UowId != null || c.UowName != null)
        {
            w.WriteStartObject("uow");
            Opt(w, "id", c.UowId);
            Opt(w, "name", c.UowName);
            Opt(w, "kind", c.UowKind);
            w.WriteEndObject();
        }
        Opt(w, "traceparent", c.Traceparent);
        Opt(w, "callsite", c.Callsite);
        if (callsite != null)
        {
            Opt(w, "lang", callsite.Lang);
            w.WriteStartArray("frames");
            foreach (var (fn, file, line) in callsite.Frames)
            {
                w.WriteStartObject();
                w.WriteString("fn", fn);
                Opt(w, "file", file);
                if (line is { } l) w.WriteNumber("line", l);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }
        if (c.CursorId is { } cid) w.WriteString("cursorId", cid.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Opt(w, "errorCode", c.ErrorCode);
        if (c.ErrorLabels is { Count: > 0 })
        {
            w.WriteStartArray("errorLabels");
            foreach (var l in c.ErrorLabels) w.WriteStringValue(l);
            w.WriteEndArray();
        }
        if (c.Extra != null) { w.WritePropertyName("extra"); w.WriteRawValue(c.Extra); }
        w.WriteEndObject();
    }

    /// <summary><c>/ops/{id}</c>: everything, the command as shell-syntax EJSON, the timing waterfall.</summary>
    public static void OpDetail(Utf8JsonWriter w, Operation o, CursorInfo? cursor, CallsiteRecord? callsite = null)
    {
        var r = o.Op;
        w.WriteStartObject();
        w.WriteString("id", o.Id);
        w.WriteNumber("ts", r.Ts);
        w.WriteString("proxyId", r.ProxyId);
        w.WriteString("member", r.Member);
        if (r.ServerConnectionId is { } sid) w.WriteNumber("serverConnectionId", sid);
        w.WriteNumber("requestId", r.RequestId);
        w.WriteNumber("connSeq", r.ConnSeq);
        w.WriteString("opCode", r.OpCode);
        w.WriteString("command", r.Command);
        Opt(w, "db", r.Db);
        Opt(w, "coll", r.Coll);
        Opt(w, "app", o.App);
        Opt(w, "driver", o.Driver);
        w.WriteString("status", r.Status);
        w.WriteBoolean("ack", r.Ack);
        w.WriteBoolean("stream", r.Stream);
        w.WriteNumber("replyCount", o.ReplyCount);
        w.WriteNumber("reqBytes", r.ReqBytes);
        w.WriteNumber("respBytes", r.RespBytes);
        if (r.ServerLatencyUs is { } lat) w.WriteNumber("serverLatency_us", lat);
        w.WriteStartObject("timings");
        w.WriteNumber("reqFirst_us", 0);
        w.WriteNumber("reqLast_us", r.ReqLastUs);
        if (r.RespFirstUs is { } rf) w.WriteNumber("respFirst_us", rf);
        if (r.RespLastUs is { } rl) w.WriteNumber("respLast_us", rl);
        w.WriteEndObject();
        if (r.Reply is { } f)
        {
            w.WriteStartObject("reply");
            if (f.Ok is { } ok) w.WriteNumber("ok", ok);
            if (f.Code is { } code) w.WriteNumber("code", code);
            Opt(w, "codeName", f.CodeName);
            Opt(w, "errmsg", f.Errmsg);
            if (f.CursorId is { } cid) w.WriteString("cursorId", cid.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (f.NReturned is { } nr) w.WriteNumber("nReturned", nr);
            if (f.N is { } n) w.WriteNumber("n", n);
            if (f.NModified is { } nm) w.WriteNumber("nModified", nm);
            w.WriteNumber("writeErrors", f.WriteErrors);
            w.WriteBoolean("writeConcernError", f.WriteConcernError);
            w.WriteNumber("upserted", f.Upserted);
            w.WriteBoolean("parsed", f.Parsed);
            w.WriteEndObject();
        }
        Opt(w, "lsid", r.Lsid);
        if (r.TxnNumber is { } txn) w.WriteNumber("txnNumber", txn);
        Opt(w, "comment", r.Comment);
        w.WriteString("shapeHash", o.ShapeHash);
        Opt(w, "valueHash", o.ValueHash);
        if (o.ShapeJson != null) { w.WritePropertyName("shape"); w.WriteRawValue(o.ShapeJson); }
        if (r.Cmd != null)
        {
            string ejson;
            try { ejson = Shapes.FastBson.Decode(r.Cmd).ToJson(Shell); }
            catch (Exception) { ejson = "(undecodable)"; }
            w.WriteString("command_ejson", ejson);
        }
        w.WriteBoolean("cmdTruncated", r.CmdTruncated);
        if (r.SeqDocs is { Count: > 0 })
        {
            w.WriteStartObject("seqDocs");
            foreach (var (id, n) in r.SeqDocs) w.WriteNumber(id, n);
            w.WriteEndObject();
        }
        Opt(w, "compressor", r.Compressor);
        if (o.Alerts is { Count: > 0 })
        {
            w.WriteStartArray("alerts");
            foreach (var a in o.Alerts) w.WriteStringValue(a);
            w.WriteEndArray();
        }
        if (cursor != null) { w.WritePropertyName("cursor"); Cursor(w, cursor); }
        if (Volatile.Read(ref o.Ctx) is { } ctx) { w.WritePropertyName("context"); Context(w, o, ctx, callsite); }
        if (r.Extra != null) { w.WritePropertyName("extra"); w.WriteRawValue(r.Extra); }
        w.WriteEndObject();
    }

    public static void Cursor(Utf8JsonWriter w, CursorInfo c)
    {
        w.WriteStartObject();
        w.WriteString("cursorId", c.CursorId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        w.WriteString("proxyId", c.ProxyId);
        w.WriteString("member", c.Member);
        w.WriteString("openOpId", c.OpenOpId);
        w.WriteString("shapeHash", c.ShapeHash);
        Opt(w, "app", c.App);
        Opt(w, "db", c.Db);
        Opt(w, "coll", c.Coll);
        w.WriteNumber("openedTs", c.OpenedTs);
        w.WriteNumber("lastActivityTs", c.LastActivityTs);
        w.WriteNumber("documents", c.Documents);
        w.WriteNumber("getMores", c.GetMores);
        Opt(w, "closedReason", c.ClosedReason);
        if (c.ClosedTs is { } ct) w.WriteNumber("closedTs", ct);
        w.WriteEndObject();
    }

    public static void Alert(Utf8JsonWriter w, Alert a)
    {
        w.WriteStartObject();
        w.WriteString("id", a.Id);
        w.WriteString("rule", a.Rule);
        w.WriteStartObject("scope");
        foreach (var (k, v) in a.Scope) if (v != null) w.WriteString(k, v);
        w.WriteEndObject();
        w.WriteString("severity", a.Severity.ToString().ToLowerInvariant());
        w.WriteString("state", a.Open ? "open" : "resolved");
        w.WriteBoolean("muted", a.Muted);
        w.WriteNumber("firstSeen", a.FirstSeen);
        w.WriteNumber("lastSeen", a.LastSeen);
        w.WriteNumber("occurrences", a.Occurrences);
        w.WriteStartArray("sampleOpIds");
        foreach (var id in a.SampleOpIds) w.WriteStringValue(id);
        w.WriteEndArray();
        w.WriteString("summary", a.Summary);
        Opt(w, "details", a.Details);
        if (a.ResolvedAt is { } r) w.WriteNumber("resolvedAt", r);
        w.WriteEndObject();
    }

    private static void Opt(Utf8JsonWriter w, string name, string? v)
    {
        if (v != null) w.WriteString(name, v);
    }
}
