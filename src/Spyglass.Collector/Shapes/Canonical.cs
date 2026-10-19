using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;
using MongoDB.Bson;

namespace Spyglass.Collector.Shapes;

/// <summary>
/// BSON bytes → <see cref="BsonDocument"/> in one pass over the span. The driver's
/// <c>BsonSerializer.Deserialize</c> costs ~11 µs for a small command (serializer lookup, contexts, a
/// stream); stage 1 decodes every op, so it gets a direct walk.
/// </summary>
public static class FastBson
{
    /// <exception cref="FormatException">the bytes are not one well-formed BSON document (the only failure).</exception>
    public static BsonDocument Decode(ReadOnlySpan<byte> bson)
    {
        var p = 0;
        BsonDocument doc;
        try { doc = ReadDocument(bson, ref p); }
        catch (ArgumentException e) { throw new FormatException(e.Message, e); } // a value's constructor rejected it (e.g. a UUID of the wrong length)
        catch (IndexOutOfRangeException e) { throw new FormatException("truncated", e); }
        if (p != bson.Length) throw new FormatException("trailing bytes after the document");
        return doc;
    }

    private static int Int32(ReadOnlySpan<byte> s, int p) =>
        p + 4 <= s.Length ? BinaryPrimitives.ReadInt32LittleEndian(s[p..]) : throw new FormatException("truncated");

    private static BsonDocument ReadDocument(ReadOnlySpan<byte> s, ref int p, int depth = 0)
    {
        if (depth > 100) throw new FormatException("nesting too deep");
        var start = p;
        var len = Int32(s, p);
        if (len < 5 || start + len > s.Length || s[start + len - 1] != 0) throw new FormatException("bad document length");
        p += 4;
        var doc = new BsonDocument(allowDuplicateNames: true);
        while (p < start + len - 1)
        {
            var type = s[p++];
            var name = CString(s, ref p);
            doc.Add(name, ReadValue(s, type, ref p, depth));
        }
        p = start + len;
        return doc;
    }

    private static BsonArray ReadArray(ReadOnlySpan<byte> s, ref int p, int depth)
    {
        var start = p;
        var len = Int32(s, p);
        if (len < 5 || start + len > s.Length || s[start + len - 1] != 0) throw new FormatException("bad array length");
        p += 4;
        var arr = new BsonArray();
        while (p < start + len - 1)
        {
            var type = s[p++];
            SkipCString(s, ref p); // "0", "1", …
            arr.Add(ReadValue(s, type, ref p, depth));
        }
        p = start + len;
        return arr;
    }

    private static string CString(ReadOnlySpan<byte> s, ref int p)
    {
        var nul = s[p..].IndexOf((byte)0);
        if (nul < 0) throw new FormatException("unterminated name");
        var v = Encoding.UTF8.GetString(s.Slice(p, nul));
        p += nul + 1;
        return v;
    }

    private static void SkipCString(ReadOnlySpan<byte> s, ref int p)
    {
        var nul = s[p..].IndexOf((byte)0);
        if (nul < 0) throw new FormatException("unterminated name");
        p += nul + 1;
    }

    private static string String(ReadOnlySpan<byte> s, ref int p)
    {
        var len = Int32(s, p);
        if (len < 1 || p + 4 + len > s.Length) throw new FormatException("bad string length");
        var v = Encoding.UTF8.GetString(s.Slice(p + 4, len - 1));
        p += 4 + len;
        return v;
    }

    private static BsonValue ReadValue(ReadOnlySpan<byte> s, byte type, ref int p, int depth)
    {
        switch (type)
        {
            case 0x01: { var v = BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(Need(s, p, 8))); p += 8; return new BsonDouble(v); }
            case 0x02: return new BsonString(String(s, ref p));
            case 0x03: return ReadDocument(s, ref p, depth + 1);
            case 0x04: return ReadArray(s, ref p, depth + 1);
            case 0x05:
            {
                var len = Int32(s, p);
                if (len < 0 || p + 5 + len > s.Length) throw new FormatException("bad binary length");
                var sub = (BsonBinarySubType)s[p + 4];
                var data = s.Slice(p + 5, len).ToArray();
                p += 5 + len;
                return new BsonBinaryData(data, sub);
            }
            case 0x06: return BsonUndefined.Value;
            case 0x07: { var v = new ObjectId(Need(s, p, 12).ToArray()); p += 12; return v; }
            case 0x08: { var v = Need(s, p, 1)[0] != 0; p += 1; return (BsonBoolean)v; }
            case 0x09: { var v = BinaryPrimitives.ReadInt64LittleEndian(Need(s, p, 8)); p += 8; return new BsonDateTime(v); }
            case 0x0A: return BsonNull.Value;
            case 0x0B: { var pattern = CString(s, ref p); var options = CString(s, ref p); return new BsonRegularExpression(pattern, options); }
            case 0x0C: { var ns = String(s, ref p); p += 12; Need(s, p - 12, 12); return new BsonString(ns); } // DBPointer: keep the namespace
            case 0x0D: return new BsonJavaScript(String(s, ref p));
            case 0x0E: return new BsonString(String(s, ref p)); // symbol
            case 0x0F:
            {
                var start = p;
                var total = Int32(s, p);
                p += 4;
                var code = String(s, ref p);
                var scope = ReadDocument(s, ref p, depth + 1);
                if (p != start + total) throw new FormatException("bad code-with-scope length");
                return new BsonJavaScriptWithScope(code, scope);
            }
            case 0x10: { var v = Int32(s, p); p += 4; return new BsonInt32(v); }
            case 0x11: { var v = BinaryPrimitives.ReadInt64LittleEndian(Need(s, p, 8)); p += 8; return new BsonTimestamp(v); }
            case 0x12: { var v = BinaryPrimitives.ReadInt64LittleEndian(Need(s, p, 8)); p += 8; return new BsonInt64(v); }
            case 0x13:
            {
                var b = Need(s, p, 16);
                var lo = BinaryPrimitives.ReadUInt64LittleEndian(b);
                var hi = BinaryPrimitives.ReadUInt64LittleEndian(b[8..]);
                p += 16;
                return new BsonDecimal128(Decimal128.FromIEEEBits(hi, lo));
            }
            case 0xFF: return BsonMinKey.Value;
            case 0x7F: return BsonMaxKey.Value;
            default: throw new FormatException($"unknown BSON type 0x{type:x2}");
        }
    }

    private static ReadOnlySpan<byte> Need(ReadOnlySpan<byte> s, int p, int n) =>
        p + n <= s.Length ? s.Slice(p, n) : throw new FormatException("truncated");
}

/// <summary>
/// 03 §4 canonical form: compact relaxed Extended JSON v2 with keys sorted (ordinal) at every level, arrays
/// in order. Written straight to UTF-8 with the sort done while writing (no sorted copy), and hashed from
/// the bytes.
/// </summary>
public static class CanonicalJson
{
    [ThreadStatic] private static ArrayBufferWriter<byte>? t_buffer;
    [ThreadStatic] private static Utf8JsonWriter? t_writer;

    /// <summary>Canonical UTF-8 of <paramref name="v"/>, valid until this thread's next call.</summary>
    public static ReadOnlySpan<byte> Bytes(BsonValue v)
    {
        var buf = t_buffer ??= new ArrayBufferWriter<byte>(512);
        buf.ResetWrittenCount();
        var w = t_writer ??= new Utf8JsonWriter(buf, new JsonWriterOptions { SkipValidation = true });
        w.Reset(buf);
        Write(w, v);
        w.Flush();
        return buf.WrittenSpan;
    }

    public static string String(BsonValue v) => Encoding.UTF8.GetString(Bytes(v));

    private static void Write(Utf8JsonWriter w, BsonValue v)
    {
        switch (v.BsonType)
        {
            case BsonType.Document:
            {
                var d = v.AsBsonDocument;
                var n = d.ElementCount;
                var order = n <= 32 ? stackalloc int[n] : new int[n];
                for (var i = 0; i < n; i++) order[i] = i;
                // insertion sort: documents here are small, and this allocates nothing
                for (var i = 1; i < n; i++)
                {
                    var x = order[i];
                    var j = i - 1;
                    while (j >= 0 && string.CompareOrdinal(d.GetElement(order[j]).Name, d.GetElement(x).Name) > 0) { order[j + 1] = order[j]; j--; }
                    order[j + 1] = x;
                }
                w.WriteStartObject();
                foreach (var i in order)
                {
                    var e = d.GetElement(i);
                    w.WritePropertyName(e.Name);
                    Write(w, e.Value);
                }
                w.WriteEndObject();
                return;
            }
            case BsonType.Array:
                w.WriteStartArray();
                foreach (var x in v.AsBsonArray) Write(w, x);
                w.WriteEndArray();
                return;
            case BsonType.String: w.WriteStringValue(v.AsString); return;
            case BsonType.Int32: w.WriteNumberValue(v.AsInt32); return;
            case BsonType.Int64: w.WriteNumberValue(v.AsInt64); return; // relaxed: a plain number
            case BsonType.Double:
            {
                var d = v.AsDouble;
                if (double.IsFinite(d))
                {
                    var s = d.ToString("R", CultureInfo.InvariantCulture);
                    w.WriteRawValue(s.Contains('.') || s.Contains('E') ? s : s + ".0", skipInputValidation: true); // keep 1.0 ≠ 1
                }
                else Wrapped(w, "$numberDouble", double.IsNaN(d) ? "NaN" : d > 0 ? "Infinity" : "-Infinity");
                return;
            }
            case BsonType.Boolean: w.WriteBooleanValue(v.AsBoolean); return;
            case BsonType.Null: w.WriteNullValue(); return;
            case BsonType.ObjectId: Wrapped(w, "$oid", v.AsObjectId.ToString()); return;
            case BsonType.DateTime:
                w.WriteStartObject();
                w.WriteNumber("$date", v.AsBsonDateTime.MillisecondsSinceEpoch);
                w.WriteEndObject();
                return;
            case BsonType.Decimal128: Wrapped(w, "$numberDecimal", v.AsDecimal128.ToString()); return;
            case BsonType.Binary:
            {
                var b = v.AsBsonBinaryData;
                w.WriteStartObject();
                w.WriteStartObject("$binary");
                w.WriteBase64String("base64", b.Bytes);
                w.WriteString("subType", ((byte)b.SubType).ToString("x2", CultureInfo.InvariantCulture));
                w.WriteEndObject();
                w.WriteEndObject();
                return;
            }
            case BsonType.RegularExpression:
            {
                var r = v.AsBsonRegularExpression;
                w.WriteStartObject();
                w.WriteStartObject("$regularExpression");
                w.WriteString("pattern", r.Pattern);
                w.WriteString("options", r.Options);
                w.WriteEndObject();
                w.WriteEndObject();
                return;
            }
            case BsonType.Timestamp:
            {
                var t = v.AsBsonTimestamp;
                w.WriteStartObject();
                w.WriteStartObject("$timestamp");
                w.WriteNumber("t", t.Timestamp);
                w.WriteNumber("i", t.Increment);
                w.WriteEndObject();
                w.WriteEndObject();
                return;
            }
            case BsonType.JavaScript: Wrapped(w, "$code", v.AsBsonJavaScript.Code); return;
            case BsonType.JavaScriptWithScope:
                w.WriteStartObject();
                w.WriteString("$code", v.AsBsonJavaScriptWithScope.Code);
                w.WritePropertyName("$scope");
                Write(w, v.AsBsonJavaScriptWithScope.Scope);
                w.WriteEndObject();
                return;
            case BsonType.MinKey: w.WriteStartObject(); w.WriteNumber("$minKey", 1); w.WriteEndObject(); return;
            case BsonType.MaxKey: w.WriteStartObject(); w.WriteNumber("$maxKey", 1); w.WriteEndObject(); return;
            case BsonType.Undefined: w.WriteStartObject(); w.WriteBoolean("$undefined", true); w.WriteEndObject(); return;
            case BsonType.Symbol: w.WriteStringValue(v.AsBsonSymbol.Name); return;
            default: w.WriteStringValue(v.ToString()); return;
        }
    }

    private static void Wrapped(Utf8JsonWriter w, string key, string value)
    {
        w.WriteStartObject();
        w.WriteString(key, value);
        w.WriteEndObject();
    }
}
