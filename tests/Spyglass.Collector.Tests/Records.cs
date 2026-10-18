using System.Text;
using System.Text.Json;
using MongoDB.Bson;

namespace Spyglass.Collector.Tests;

/// <summary>Wire records as the proxy writes them (02 §4.2, absent = default).</summary>
internal static class Records
{
    public const string Proxy = "8a1e2b3c-0000-4000-8000-000000000001";
    public static long T0 = (DateTime.UtcNow - DateTime.UnixEpoch).Ticks / 10;

    public static string Op(int requestId, string command, BsonDocument? cmd = null, string db = "shop", string? coll = "orders",
        long ts = 0, long sid = 7, long connSeq = 1, string status = "ok", long latencyUs = 500, int? nReturned = null,
        long? cursorId = null, int respBytes = 200, string? codeName = null, int? code = null, bool stream = false,
        bool truncated = false, string member = "localhost:27017", string src = Proxy)
    {
        var o = new Dictionary<string, object?>
        {
            ["type"] = "op", ["ts"] = ts == 0 ? T0 : ts, ["src"] = src, ["member"] = member, ["serverConnectionId"] = sid,
            ["requestId"] = requestId, ["connSeq"] = connSeq, ["command"] = command, ["db"] = db, ["coll"] = coll,
            ["status"] = status, ["reqBytes"] = 120,
        };
        if (stream) o["stream"] = true;
        if (status is not ("unacknowledged" or "connection_closed"))
        {
            o["respBytes"] = respBytes;
            o["t"] = new Dictionary<string, long> { ["reqLast_us"] = 2, ["respFirst_us"] = 2 + latencyUs, ["respLast_us"] = 4 + latencyUs };
            var reply = new Dictionary<string, object?> { ["ok"] = status == "error" ? 0 : 1 };
            if (nReturned != null) reply["nReturned"] = nReturned;
            if (cursorId != null) reply["cursorId"] = cursorId.Value.ToString();
            if (codeName != null) reply["codeName"] = codeName;
            if (code != null) reply["code"] = code;
            o["reply"] = reply;
        }
        else o["t"] = new Dictionary<string, long> { ["reqLast_us"] = 2 };
        cmd ??= new BsonDocument(command, (BsonValue?)coll ?? 1);
        if (!cmd.Contains("$db") && !cmd.Contains("$redacted")) cmd["$db"] = db; // 01 §9.1: a redacted body is exactly {$redacted: name}
        o["cmd"] = Convert.ToBase64String(cmd.ToBson());
        if (truncated) o["cmdTruncated"] = true;
        return JsonSerializer.Serialize(o);
    }

    public static string Conn(long connSeq, string app, string ev = "open", long sid = 7, string src = Proxy, string member = "localhost:27017") =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["type"] = "conn", ["ts"] = T0, ["src"] = src, ["event"] = ev, ["member"] = member, ["connSeq"] = connSeq,
            ["serverConnectionId"] = sid, ["client"] = "10.0.0.1:5000", ["app"] = app, ["driver"] = "mongo-csharp-driver",
        });

    public static byte[] Batch(params string[] lines) => Encoding.UTF8.GetBytes(string.Join('\n', lines) + "\n");
}
