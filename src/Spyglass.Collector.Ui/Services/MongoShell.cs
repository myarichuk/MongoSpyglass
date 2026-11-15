using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Spyglass.Collector.Ui.Services;

/// <summary>
/// Relaxed extended JSON (what the API carries) to mongosh syntax (UD-1): <c>{"$oid":"…"}</c> becomes <c>ObjectId("…")</c> and
/// so on, so a copied command runs as written. Never throws: input that cannot be converted yields null (callers copy the raw text).
/// </summary>
public static class MongoShell
{
    // the shell adds these itself and rejects a command that carries them
    private static readonly string[] Envelope = ["$db", "lsid", "$clusterTime", "txnNumber", "autocommit", "startTransaction"];

    public static string? RunCommand(string? db, string? ejson)
    {
        if (string.IsNullOrWhiteSpace(ejson) || ejson == "(undecodable)") return null;
        try
        {
            using var doc = JsonDocument.Parse(ejson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            var sb = new StringBuilder("db.getSiblingDB(").Append(Str(db ?? "admin")).Append(").runCommand(");
            Object(sb, doc.RootElement, 0, top: true);
            return sb.Append(')').ToString();
        }
        catch (JsonException) { return null; }
        catch (FormatException) { return null; }
        catch (InvalidOperationException) { return null; }
        catch (KeyNotFoundException) { return null; }
    }

    private static string Str(string s) => JsonSerializer.Serialize(s);

    private static void Indent(StringBuilder sb, int depth) => sb.Append('\n').Append(' ', depth * 2);

    private static void Write(StringBuilder sb, JsonElement e, int depth)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object: Object(sb, e, depth, top: false); break;
            case JsonValueKind.Array:
                if (e.GetArrayLength() == 0) { sb.Append("[]"); break; }
                sb.Append('[');
                var first = true;
                foreach (var x in e.EnumerateArray())
                {
                    if (!first) sb.Append(',');
                    first = false;
                    Indent(sb, depth + 1);
                    Write(sb, x, depth + 1);
                }
                Indent(sb, depth);
                sb.Append(']');
                break;
            case JsonValueKind.String: sb.Append(Str(e.GetString()!)); break;
            default: sb.Append(e.GetRawText()); break; // numbers, true, false, null
        }
    }

    private static void Object(StringBuilder sb, JsonElement o, int depth, bool top)
    {
        if (!top && Extended(o) is { } ext) { sb.Append(ext); return; }
        var props = o.EnumerateObject().Where(p => !(top && Array.IndexOf(Envelope, p.Name) >= 0)).ToList();
        if (props.Count == 0) { sb.Append("{}"); return; }
        sb.Append('{');
        for (var i = 0; i < props.Count; i++)
        {
            if (i > 0) sb.Append(',');
            Indent(sb, depth + 1);
            sb.Append(Str(props[i].Name)).Append(": ");
            Write(sb, props[i].Value, depth + 1);
        }
        Indent(sb, depth);
        sb.Append('}');
    }

    /// <summary>The shell constructor for a one-key extended-JSON wrapper, or null when <paramref name="o"/> is an ordinary document.</summary>
    private static string? Extended(JsonElement o)
    {
        var n = 0;
        string? key = null;
        JsonElement v = default;
        foreach (var p in o.EnumerateObject()) { if (n++ == 0) (key, v) = (p.Name, p.Value); }
        if (n != 1 || key == null || key[0] != '$') return null;
        var s = v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        switch (key)
        {
            case "$oid" when s != null: return $"ObjectId({Str(s)})";
            case "$date" when s != null: return $"ISODate({Str(s)})";
            case "$date" when v.ValueKind == JsonValueKind.Object && v.TryGetProperty("$numberLong", out var ms) && ms.GetString() is { } msv:
                return $"new Date({msv})";
            case "$date" when v.ValueKind == JsonValueKind.Number: return $"new Date({v.GetRawText()})";
            case "$numberLong" when s != null: return $"NumberLong({Str(s)})";
            case "$numberInt" when s != null: return s;
            case "$numberDouble" when s != null: return s; // also NaN / Infinity / -Infinity, which are shell globals
            case "$numberDecimal" when s != null: return $"NumberDecimal({Str(s)})";
            case "$uuid" when s != null: return $"UUID({Str(s)})";
            case "$symbol" when s != null: return Str(s);
            case "$minKey": return "MinKey()";
            case "$maxKey": return "MaxKey()";
            case "$undefined": return "undefined";
            case "$regularExpression" when v.ValueKind == JsonValueKind.Object:
                return $"new RegExp({Str(v.GetProperty("pattern").GetString() ?? "")}, {Str(v.GetProperty("options").GetString() ?? "")})";
            case "$timestamp" when v.ValueKind == JsonValueKind.Object:
                return $"Timestamp({v.GetProperty("t").GetRawText()}, {v.GetProperty("i").GetRawText()})";
            case "$binary" when v.ValueKind == JsonValueKind.Object:
            {
                var b64 = v.GetProperty("base64").GetString() ?? "";
                var sub = int.Parse(v.GetProperty("subType").GetString() ?? "0", NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                var bytes = Convert.FromBase64String(b64);
                return sub == 4 && bytes.Length == 16
                    ? $"UUID({Str(new Guid(bytes, bigEndian: true).ToString())})"
                    : $"BinData({sub}, {Str(b64)})";
            }
            default: return null;
        }
    }
}
