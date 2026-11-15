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
        catch (Exception) { return null; } // never throws: a copy button must not take the page down
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

    /// <summary>
    /// The shell constructor for a one-key extended-JSON wrapper, or null when <paramref name="o"/> is an ordinary document.
    /// Relaxed EJSON is ambiguous with real documents, so every payload is validated before it is emitted as code (FC-4):
    /// a value without the wrapper's exact shape makes the object an ordinary (quoted) document.
    /// </summary>
    private static string? Extended(JsonElement o)
    {
        var n = 0;
        string? key = null;
        JsonElement v = default;
        foreach (var p in o.EnumerateObject()) { if (n++ == 0) (key, v) = (p.Name, p.Value); }
        if (n != 1 || key == null || key[0] != '$') return null;
        var s = v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var inv = CultureInfo.InvariantCulture;
        switch (key)
        {
            case "$oid" when s is { Length: 24 } && s.All(Uri.IsHexDigit): return $"ObjectId({Str(s)})";
            case "$date" when s != null && DateTimeOffset.TryParse(s, inv, DateTimeStyles.AssumeUniversal, out _): return $"ISODate({Str(s)})";
            case "$date" when v.ValueKind == JsonValueKind.Object && v.TryGetProperty("$numberLong", out var ms) && ms.ValueKind == JsonValueKind.String
                              && long.TryParse(ms.GetString(), NumberStyles.AllowLeadingSign, inv, out var msv):
                return $"new Date({msv.ToString(inv)})";
            case "$date" when v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var dn): return $"new Date({dn.ToString(inv)})";
            case "$numberLong" when s != null && long.TryParse(s, NumberStyles.AllowLeadingSign, inv, out _): return $"NumberLong({Str(s)})";
            case "$numberInt" when s != null && int.TryParse(s, NumberStyles.AllowLeadingSign, inv, out var ni): return ni.ToString(inv);
            case "$numberDouble" when s is "NaN" or "Infinity" or "-Infinity": return s; // shell globals
            case "$numberDouble" when s != null && double.TryParse(s, NumberStyles.Float, inv, out var nd) && double.IsFinite(nd): return nd.ToString("R", inv);
            case "$numberDecimal" when s != null && (s is "NaN" or "Infinity" or "-Infinity" || decimal.TryParse(s, NumberStyles.Float, inv, out _)): return $"NumberDecimal({Str(s)})";
            case "$uuid" when s != null && Guid.TryParse(s, out _): return $"UUID({Str(s)})";
            case "$symbol" when s != null: return Str(s);
            case "$minKey": return "MinKey()";
            case "$maxKey": return "MaxKey()";
            case "$undefined": return "undefined";
            case "$regularExpression" when v.ValueKind == JsonValueKind.Object
                                           && v.TryGetProperty("pattern", out var pat) && pat.ValueKind == JsonValueKind.String
                                           && v.TryGetProperty("options", out var opt) && opt.ValueKind == JsonValueKind.String:
                return $"new RegExp({Str(pat.GetString()!)}, {Str(opt.GetString()!)})";
            case "$timestamp" when v.ValueKind == JsonValueKind.Object
                                   && v.TryGetProperty("t", out var t) && t.ValueKind == JsonValueKind.Number && t.TryGetUInt32(out var tv)
                                   && v.TryGetProperty("i", out var i) && i.ValueKind == JsonValueKind.Number && i.TryGetUInt32(out var iv):
                return $"Timestamp({tv.ToString(inv)}, {iv.ToString(inv)})";
            case "$binary" when v.ValueKind == JsonValueKind.Object
                                && v.TryGetProperty("base64", out var b64e) && b64e.ValueKind == JsonValueKind.String
                                && v.TryGetProperty("subType", out var ste) && ste.ValueKind == JsonValueKind.String
                                && ste.GetString() is { Length: > 0 and <= 2 } sts
                                && int.TryParse(sts, NumberStyles.AllowHexSpecifier, inv, out var sub):
            {
                var b64 = b64e.GetString()!;
                var bytes = new byte[b64.Length * 3 / 4 + 3];
                if (!Convert.TryFromBase64String(b64, bytes, out var len)) return null;
                return sub == 4 && len == 16
                    ? $"UUID({Str(new Guid(bytes.AsSpan(0, 16), bigEndian: true).ToString())})"
                    : $"BinData({sub.ToString(inv)}, {Str(b64)})";
            }
            default: return null;
        }
    }
}
