using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;
using MongoDB.Bson;

namespace Spyglass.Collector.Shapes;

/// <summary>One BSON element read in place: its type byte, its name (UTF-8) and its value bytes.</summary>
internal readonly ref struct BsonEl(byte type, ReadOnlySpan<byte> name, ReadOnlySpan<byte> value)
{
    public readonly byte Type = type;
    public readonly ReadOnlySpan<byte> Name = name;
    public readonly ReadOnlySpan<byte> Value = value;

    public bool IsDocument => Type == BsonSpan.Document;
    public bool IsArray => Type == BsonSpan.Array;
    public bool IsNumeric => Type is BsonSpan.Double or BsonSpan.Int32 or BsonSpan.Int64 or BsonSpan.Decimal;
    public bool NameIs(ReadOnlySpan<byte> utf8) => Name.SequenceEqual(utf8);
    public bool NameStartsWithDollar => Name.Length > 0 && Name[0] == (byte)'$';
}

/// <summary>Elements of a document or array body, in order. The bytes must have passed <see cref="BsonSpan.Validate"/>.</summary>
internal ref struct BsonElements
{
    private readonly ReadOnlySpan<byte> _doc;
    private int _p;
    private readonly int _end;
    private byte _type;
    private int _nameStart, _nameLength, _valueStart, _valueLength;

    public BsonElements(ReadOnlySpan<byte> doc)
    {
        _doc = doc;
        _p = 4;
        _end = doc.Length - 1;
    }

    public readonly BsonElements GetEnumerator() => this;
    public readonly BsonEl Current => new(_type, _doc.Slice(_nameStart, _nameLength), _doc.Slice(_valueStart, _valueLength));
    /// <summary>Offsets of the current element within the document (for sorting without copying).</summary>
    public readonly (int NameStart, int NameLength, int ValueStart, int ValueLength, byte Type) Position => (_nameStart, _nameLength, _valueStart, _valueLength, _type);

    public bool MoveNext()
    {
        if (_p >= _end) return false;
        _type = _doc[_p++];
        _nameStart = _p;
        _nameLength = _doc[_p..].IndexOf((byte)0);
        _p += _nameLength + 1;
        _valueStart = _p;
        _valueLength = BsonSpan.ValueLength(_doc, _p, _type);
        _p += _valueLength;
        return true;
    }
}

/// <summary>
/// BSON read in place, for stage 1: validation with exactly <see cref="FastBson"/>'s acceptance (so a command that would
/// not decode still does not), element lookup, the facts stage 1 needs, and 03 §4 canonical JSON written straight from
/// the bytes, identical to <see cref="CanonicalJson"/> over the decoded document (a differential test pins that).
/// </summary>
internal static class BsonSpan
{
    public const byte Double = 0x01, String = 0x02, Document = 0x03, Array = 0x04, Binary = 0x05, Undefined = 0x06, ObjectId = 0x07,
        Boolean = 0x08, DateTime = 0x09, Null = 0x0A, Regex = 0x0B, DbPointer = 0x0C, JavaScript = 0x0D, Symbol = 0x0E,
        JavaScriptWithScope = 0x0F, Int32 = 0x10, Timestamp = 0x11, Int64 = 0x12, Decimal = 0x13, MinKey = 0xFF, MaxKey = 0x7F;

    /// <summary>The length of a value of <paramref name="type"/> at <paramref name="p"/> (valid bytes assumed).</summary>
    public static int ValueLength(ReadOnlySpan<byte> s, int p, byte type) => type switch
    {
        Double or DateTime or Timestamp or Int64 => 8,
        String or JavaScript or Symbol => 4 + BinaryPrimitives.ReadInt32LittleEndian(s[p..]),
        Document or Array or JavaScriptWithScope => BinaryPrimitives.ReadInt32LittleEndian(s[p..]),
        Binary => 5 + BinaryPrimitives.ReadInt32LittleEndian(s[p..]),
        Undefined or Null or MinKey or MaxKey => 0,
        ObjectId => 12,
        Boolean => 1,
        Regex => CStringsLength(s, p, 2),
        DbPointer => 4 + BinaryPrimitives.ReadInt32LittleEndian(s[p..]) + 12,
        Int32 => 4,
        Decimal => 16,
        _ => throw new FormatException($"unknown BSON type 0x{type:x2}"),
    };

    private static int CStringsLength(ReadOnlySpan<byte> s, int p, int count)
    {
        var start = p;
        for (var i = 0; i < count; i++) p += s[p..].IndexOf((byte)0) + 1;
        return p - start;
    }

    // ---- validation: what FastBson.Decode accepts, nothing more ----------------------------------------------------

    public static bool Validate(ReadOnlySpan<byte> bson)
    {
        try
        {
            var p = 0;
            ValidateDocument(bson, ref p, 0);
            return p == bson.Length;
        }
        catch (FormatException) { return false; }
        catch (IndexOutOfRangeException) { return false; }
        catch (ArgumentOutOfRangeException) { return false; }
    }

    private static int I32(ReadOnlySpan<byte> s, int p) =>
        p >= 0 && p + 4 <= s.Length ? BinaryPrimitives.ReadInt32LittleEndian(s[p..]) : throw new FormatException("truncated");

    private static void Need(ReadOnlySpan<byte> s, int p, int n)
    {
        if (p + n > s.Length) throw new FormatException("truncated");
    }

    private static void ValidateDocument(ReadOnlySpan<byte> s, ref int p, int depth)
    {
        if (depth > 100) throw new FormatException("nesting too deep");
        var start = p;
        var len = I32(s, p);
        if (len < 5 || start + len > s.Length || s[start + len - 1] != 0) throw new FormatException("bad document length");
        p += 4;
        while (p < start + len - 1)
        {
            var type = s[p++];
            CString(s, ref p);
            ValidateValue(s, type, ref p, depth);
        }
        p = start + len;
    }

    private static void CString(ReadOnlySpan<byte> s, ref int p)
    {
        var nul = s[p..].IndexOf((byte)0);
        if (nul < 0) throw new FormatException("unterminated name");
        p += nul + 1;
    }

    private static void Str(ReadOnlySpan<byte> s, ref int p)
    {
        var len = I32(s, p);
        if (len < 1 || p + 4 + len > s.Length) throw new FormatException("bad string length");
        p += 4 + len;
    }

    private static void ValidateValue(ReadOnlySpan<byte> s, byte type, ref int p, int depth)
    {
        switch (type)
        {
            case Double or DateTime or Timestamp or Int64: Need(s, p, 8); p += 8; return;
            case String or JavaScript or Symbol: Str(s, ref p); return;
            case Document: ValidateDocument(s, ref p, depth + 1); return;
            case Array: ValidateDocument(s, ref p, depth + 1); return;
            case Binary:
            {
                var len = I32(s, p);
                if (len < 0 || p + 5 + len > s.Length) throw new FormatException("bad binary length");
                // FastBson: the driver's BsonBinaryData rejects a UUID subtype that is not 16 bytes
                if (s[p + 4] is 3 or 4 && len != 16) throw new FormatException("bad uuid length");
                p += 5 + len;
                return;
            }
            case Undefined or Null or MinKey or MaxKey: return;
            case ObjectId: Need(s, p, 12); p += 12; return;
            case Boolean: Need(s, p, 1); p += 1; return;
            case Regex: CString(s, ref p); CString(s, ref p); return;
            case DbPointer: Str(s, ref p); Need(s, p, 12); p += 12; return;
            case JavaScriptWithScope:
            {
                var start = p;
                var total = I32(s, p);
                p += 4;
                Str(s, ref p);
                ValidateDocument(s, ref p, depth + 1);
                if (p != start + total) throw new FormatException("bad code-with-scope length");
                return;
            }
            case Int32: Need(s, p, 4); p += 4; return;
            case Decimal: Need(s, p, 16); p += 16; return;
            default: throw new FormatException($"unknown BSON type 0x{type:x2}");
        }
    }

    // ---- lookup and facts ------------------------------------------------------------------------------------------

    /// <summary>The first element named <paramref name="name"/> (BsonDocument.TryGetValue's choice when names repeat).</summary>
    public static bool TryGet(ReadOnlySpan<byte> doc, ReadOnlySpan<byte> name, out BsonEl el)
    {
        foreach (var e in new BsonElements(doc))
            if (e.NameIs(name)) { el = e; return true; }
        el = default;
        return false;
    }

    public static int Count(ReadOnlySpan<byte> doc)
    {
        var n = 0;
        foreach (var _ in new BsonElements(doc)) n++;
        return n;
    }

    public static bool First(ReadOnlySpan<byte> doc, out BsonEl el)
    {
        var e = new BsonElements(doc);
        if (e.MoveNext()) { el = e.Current; return true; }
        el = default;
        return false;
    }

    public static string Utf8String(ReadOnlySpan<byte> stringValue) => Encoding.UTF8.GetString(StringBytes(stringValue));

    /// <summary>A string value's bytes, without the length prefix and the terminating NUL.</summary>
    public static ReadOnlySpan<byte> StringBytes(ReadOnlySpan<byte> value) => value.Slice(4, value.Length - 5);

    /// <summary>BsonValue.ToInt64 for the numeric types.</summary>
    public static long ToInt64(in BsonEl e) => e.Type switch
    {
        Int32 => BinaryPrimitives.ReadInt32LittleEndian(e.Value),
        Int64 => BinaryPrimitives.ReadInt64LittleEndian(e.Value),
        Double => (long)BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(e.Value)),
        Decimal => Decimal128.ToInt64(Dec(e.Value)),
        _ => throw new FormatException("not a number"),
    };

    /// <summary>BsonValue.ToInt32 for the numeric types.</summary>
    public static int ToInt32(in BsonEl e) => e.Type switch
    {
        Int32 => BinaryPrimitives.ReadInt32LittleEndian(e.Value),
        Int64 => (int)BinaryPrimitives.ReadInt64LittleEndian(e.Value),
        Double => (int)BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(e.Value)),
        Decimal => Decimal128.ToInt32(Dec(e.Value)),
        _ => throw new FormatException("not a number"),
    };

    /// <summary>BsonValue.ToBoolean.</summary>
    public static bool ToBoolean(in BsonEl e) => e.Type switch
    {
        Boolean => e.Value[0] != 0,
        Double => BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(e.Value)) is var d && !(d == 0 || double.IsNaN(d)),
        Int32 => BinaryPrimitives.ReadInt32LittleEndian(e.Value) != 0,
        Int64 => BinaryPrimitives.ReadInt64LittleEndian(e.Value) != 0,
        Decimal => !Decimal128.IsZero(Dec(e.Value)) && !Decimal128.IsNaN(Dec(e.Value)),
        String => e.Value.Length > 5,
        Null or Undefined => false,
        _ => true,
    };

    public static Decimal128 Dec(ReadOnlySpan<byte> v) =>
        Decimal128.FromIEEEBits(BinaryPrimitives.ReadUInt64LittleEndian(v[8..]), BinaryPrimitives.ReadUInt64LittleEndian(v));

    // ---- 03 §4 canonical JSON from the bytes -------------------------------------------------------------------------

    /// <summary>One element of a document being written in sorted order: offsets into the document.</summary>
    internal struct Entry
    {
        public int NameStart, NameLength, ValueStart, ValueLength;
        public byte Type;
    }

    /// <summary>
    /// The document's elements sorted by name, stably (CanonicalJson's insertion sort keeps repeated names in order).
    /// Names compare as .NET strings do, ordinally by UTF-16 code unit; for ASCII names that is the byte order.
    /// </summary>
    public static int Sorted(ReadOnlySpan<byte> doc, Span<Entry> into)
    {
        var n = 0;
        var e = new BsonElements(doc);
        while (e.MoveNext())
        {
            var (ns, nl, vs, vl, t) = e.Position;
            into[n++] = new Entry { NameStart = ns, NameLength = nl, ValueStart = vs, ValueLength = vl, Type = t };
        }
        for (var i = 1; i < n; i++)
        {
            var x = into[i];
            var j = i - 1;
            while (j >= 0 && CompareNames(doc.Slice(into[j].NameStart, into[j].NameLength), doc.Slice(x.NameStart, x.NameLength)) > 0)
            {
                into[j + 1] = into[j];
                j--;
            }
            into[j + 1] = x;
        }
        return n;
    }

    public static int CompareNames(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        if (System.Text.Ascii.IsValid(a) && System.Text.Ascii.IsValid(b)) return a.SequenceCompareTo(b);
        // as strings: UTF-16 ordinal, invalid UTF-8 replaced (what decoding to a BsonDocument does)
        return string.CompareOrdinal(Encoding.UTF8.GetString(a), Encoding.UTF8.GetString(b));
    }

    public static bool NamesEqual(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b) =>
        a.SequenceEqual(b) || (!System.Text.Ascii.IsValid(a) || !System.Text.Ascii.IsValid(b)) && CompareNames(a, b) == 0;

    public static void WritePropertyName(Utf8JsonWriter w, ReadOnlySpan<byte> name)
    {
        if (System.Text.Unicode.Utf8.IsValid(name)) w.WritePropertyName(name);
        else w.WritePropertyName(Encoding.UTF8.GetString(name)); // replaced, as decoding does
    }

    public static void WriteString(Utf8JsonWriter w, ReadOnlySpan<byte> utf8)
    {
        if (System.Text.Unicode.Utf8.IsValid(utf8)) w.WriteStringValue(utf8);
        else w.WriteStringValue(Encoding.UTF8.GetString(utf8));
    }

    /// <summary>Canonical JSON of a document body: keys sorted at every level, arrays in order.</summary>
    public static void WriteDocument(Utf8JsonWriter w, ReadOnlySpan<byte> doc)
    {
        var count = Count(doc);
        Entry[]? rented = null;
        var entries = count <= 32 ? stackalloc Entry[count] : (rented = System.Buffers.ArrayPool<Entry>.Shared.Rent(count));
        try
        {
            var n = Sorted(doc, entries);
            w.WriteStartObject();
            for (var i = 0; i < n; i++)
            {
                var x = entries[i];
                WritePropertyName(w, doc.Slice(x.NameStart, x.NameLength));
                WriteValue(w, x.Type, doc.Slice(x.ValueStart, x.ValueLength));
            }
            w.WriteEndObject();
        }
        finally
        {
            if (rented != null) System.Buffers.ArrayPool<Entry>.Shared.Return(rented);
        }
    }

    /// <summary>Canonical JSON of one value (CanonicalJson.Write over the decoded value).</summary>
    public static void WriteValue(Utf8JsonWriter w, byte type, ReadOnlySpan<byte> v)
    {
        switch (type)
        {
            case Document:
                WriteDocument(w, v);
                return;
            case Array:
                w.WriteStartArray();
                foreach (var e in new BsonElements(v)) WriteValue(w, e.Type, e.Value);
                w.WriteEndArray();
                return;
            case String or Symbol or DbPointer:
            {
                // a symbol is a string, a DBPointer its namespace (FastBson decodes them so)
                var len = BinaryPrimitives.ReadInt32LittleEndian(v);
                WriteString(w, v.Slice(4, len - 1));
                return;
            }
            case Int32: w.WriteNumberValue(BinaryPrimitives.ReadInt32LittleEndian(v)); return;
            case Int64: w.WriteNumberValue(BinaryPrimitives.ReadInt64LittleEndian(v)); return;
            case Double:
            {
                var d = BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(v));
                if (double.IsFinite(d))
                {
                    Span<byte> s = stackalloc byte[34];
                    d.TryFormat(s, out var n, "R", CultureInfo.InvariantCulture);
                    if (s[..n].IndexOfAny((byte)'.', (byte)'E') < 0) { ".0"u8.CopyTo(s[n..]); n += 2; } // keep 1.0 ≠ 1
                    w.WriteRawValue(s[..n], skipInputValidation: true);
                }
                else Wrapped(w, "$numberDouble"u8, double.IsNaN(d) ? "NaN"u8 : d > 0 ? "Infinity"u8 : "-Infinity"u8);
                return;
            }
            case Boolean: w.WriteBooleanValue(v[0] != 0); return;
            case Null: w.WriteNullValue(); return;
            case ObjectId:
            {
                Span<char> hex = stackalloc char[24];
                Convert.TryToHexStringLower(v[..12], hex, out _);
                w.WriteStartObject();
                w.WriteString("$oid"u8, hex);
                w.WriteEndObject();
                return;
            }
            case DateTime:
                w.WriteStartObject();
                w.WriteNumber("$date"u8, BinaryPrimitives.ReadInt64LittleEndian(v));
                w.WriteEndObject();
                return;
            case Decimal:
                w.WriteStartObject();
                w.WriteString("$numberDecimal"u8, Dec(v).ToString());
                w.WriteEndObject();
                return;
            case Binary:
            {
                var len = BinaryPrimitives.ReadInt32LittleEndian(v);
                Span<char> sub = stackalloc char[2];
                v[4].TryFormat(sub, out _, "x2", CultureInfo.InvariantCulture);
                w.WriteStartObject();
                w.WriteStartObject("$binary"u8);
                w.WriteBase64String("base64"u8, v.Slice(5, len));
                w.WriteString("subType"u8, sub);
                w.WriteEndObject();
                w.WriteEndObject();
                return;
            }
            case Regex:
            {
                var pattern = v[..v.IndexOf((byte)0)];
                var options = v[(pattern.Length + 1)..];
                options = options[..options.IndexOf((byte)0)];
                w.WriteStartObject();
                w.WriteStartObject("$regularExpression"u8);
                w.WritePropertyName("pattern"u8);
                WriteString(w, pattern);
                w.WritePropertyName("options"u8);
                WriteString(w, options);
                w.WriteEndObject();
                w.WriteEndObject();
                return;
            }
            case Timestamp:
            {
                var ts = BinaryPrimitives.ReadInt64LittleEndian(v);
                w.WriteStartObject();
                w.WriteStartObject("$timestamp"u8);
                w.WriteNumber("t"u8, (int)(ts >> 32));
                w.WriteNumber("i"u8, (int)ts);
                w.WriteEndObject();
                w.WriteEndObject();
                return;
            }
            case JavaScript:
                w.WriteStartObject();
                w.WritePropertyName("$code"u8);
                WriteString(w, StringBytes(v));
                w.WriteEndObject();
                return;
            case JavaScriptWithScope:
            {
                var codeLen = BinaryPrimitives.ReadInt32LittleEndian(v[4..]);
                w.WriteStartObject();
                w.WritePropertyName("$code"u8);
                WriteString(w, v.Slice(8, codeLen - 1));
                w.WritePropertyName("$scope"u8);
                WriteDocument(w, v[(8 + codeLen)..]);
                w.WriteEndObject();
                return;
            }
            case MinKey: w.WriteStartObject(); w.WriteNumber("$minKey"u8, 1); w.WriteEndObject(); return;
            case MaxKey: w.WriteStartObject(); w.WriteNumber("$maxKey"u8, 1); w.WriteEndObject(); return;
            case Undefined: w.WriteStartObject(); w.WriteBoolean("$undefined"u8, true); w.WriteEndObject(); return;
            default: throw new FormatException($"unknown BSON type 0x{type:x2}");
        }
    }

    private static void Wrapped(Utf8JsonWriter w, ReadOnlySpan<byte> key, ReadOnlySpan<byte> value)
    {
        w.WriteStartObject();
        w.WriteString(key, value);
        w.WriteEndObject();
    }
}
