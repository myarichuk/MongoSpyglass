using System.Buffers.Binary;
using System.Text;

namespace Spyglass.Proxy.Wire;

public static class BsonType
{
    public const byte Double = 0x01, String = 0x02, Document = 0x03, Array = 0x04, Binary = 0x05,
        Undefined = 0x06, ObjectId = 0x07, Boolean = 0x08, DateTime = 0x09, Null = 0x0A, Regex = 0x0B,
        DbPointer = 0x0C, JavaScript = 0x0D, Symbol = 0x0E, JavaScriptWithScope = 0x0F, Int32 = 0x10,
        Timestamp = 0x11, Int64 = 0x12, Decimal128 = 0x13, MinKey = 0xFF, MaxKey = 0x7F;
}

/// <summary>
/// Span-level BSON access. No allocation unless a method returns a string.
/// Every length is checked against the available bytes: malformed or truncated input
/// ends enumeration (with <see cref="BsonElements.Truncated"/> or <see cref="BsonElements.Malformed"/>)
/// instead of throwing or reading past the buffer.
/// </summary>
public static class Bson
{
    /// <summary>
    /// Size in bytes of a value of <paramref name="type"/> starting at <paramref name="s"/>,
    /// <c>-1</c> if the type is unknown or the value is malformed, <c>-2</c> if more bytes are needed.
    /// </summary>
    public static int ValueSize(byte type, ReadOnlySpan<byte> s)
    {
        switch (type)
        {
            case BsonType.Double:
            case BsonType.DateTime:
            case BsonType.Timestamp:
            case BsonType.Int64:
                return Fixed(8, s);
            case BsonType.Int32:
                return Fixed(4, s);
            case BsonType.Boolean:
                return Fixed(1, s);
            case BsonType.ObjectId:
                return Fixed(12, s);
            case BsonType.Decimal128:
                return Fixed(16, s);
            case BsonType.Undefined:
            case BsonType.Null:
            case BsonType.MinKey:
            case BsonType.MaxKey:
                return 0;
            case BsonType.String:
            case BsonType.JavaScript:
            case BsonType.Symbol:
            {
                if (s.Length < 4) return -2;
                var len = BinaryPrimitives.ReadInt32LittleEndian(s);
                if (len < 1) return -1;
                return Fit(4L + len, s);
            }
            case BsonType.Document:
            case BsonType.Array:
            case BsonType.JavaScriptWithScope:
            {
                if (s.Length < 4) return -2;
                var len = BinaryPrimitives.ReadInt32LittleEndian(s);
                if (len < 5) return -1;
                return Fit(len, s);
            }
            case BsonType.Binary:
            {
                if (s.Length < 4) return -2;
                var len = BinaryPrimitives.ReadInt32LittleEndian(s);
                if (len < 0) return -1;
                return Fit(5L + len, s);
            }
            case BsonType.Regex:
            {
                var a = s.IndexOf((byte)0);
                if (a < 0) return -2;
                var b = s[(a + 1)..].IndexOf((byte)0);
                if (b < 0) return -2;
                return a + 1 + b + 1;
            }
            case BsonType.DbPointer:
            {
                if (s.Length < 4) return -2;
                var len = BinaryPrimitives.ReadInt32LittleEndian(s);
                if (len < 1) return -1;
                return Fit(4L + len + 12, s);
            }
            default:
                return -1;
        }

        static int Fixed(int n, ReadOnlySpan<byte> s) => s.Length >= n ? n : -2;
        static int Fit(long n, ReadOnlySpan<byte> s) => n > int.MaxValue ? -1 : s.Length >= n ? (int)n : -2;
    }

    /// <summary>Enumerates the elements of a document. <paramref name="doc"/> starts at the length prefix and may be truncated.</summary>
    public static BsonElements Elements(ReadOnlySpan<byte> doc) => new(doc);

    public static bool TryGetElement(ReadOnlySpan<byte> doc, ReadOnlySpan<byte> name, out BsonElement element)
    {
        foreach (var e in Elements(doc))
        {
            if (e.Name.SequenceEqual(name))
            {
                element = e;
                return true;
            }
        }
        element = default;
        return false;
    }

    public static int DeclaredLength(ReadOnlySpan<byte> doc) =>
        doc.Length >= 4 ? BinaryPrimitives.ReadInt32LittleEndian(doc) : -1;
}

public readonly ref struct BsonElement
{
    public readonly byte Type;
    public readonly ReadOnlySpan<byte> Name;
    public readonly ReadOnlySpan<byte> Value;
    /// <summary>Offset of the type byte within the enumerated document.</summary>
    public readonly int Offset;
    /// <summary>Total element size: type byte + name + NUL + value.</summary>
    public readonly int Size;

    public BsonElement(byte type, ReadOnlySpan<byte> name, ReadOnlySpan<byte> value, int offset, int size)
    {
        Type = type;
        Name = name;
        Value = value;
        Offset = offset;
        Size = size;
    }

    public bool NameIs(ReadOnlySpan<byte> utf8) => Name.SequenceEqual(utf8);

    public string NameString => Encoding.UTF8.GetString(Name);

    /// <summary>String value bytes without the length prefix and the trailing NUL. Empty for non-strings.</summary>
    public ReadOnlySpan<byte> StringBytes =>
        Type is BsonType.String or BsonType.Symbol or BsonType.JavaScript && Value.Length >= 5 ? Value[4..^1] : default;

    public string? AsString() => Type == BsonType.String ? Encoding.UTF8.GetString(StringBytes) : null;

    /// <summary>Numeric value as a long (int32, int64, double, bool); null for other types.</summary>
    public long? AsInt64() => Type switch
    {
        BsonType.Int32 => BinaryPrimitives.ReadInt32LittleEndian(Value),
        BsonType.Int64 => BinaryPrimitives.ReadInt64LittleEndian(Value),
        BsonType.Double => BinaryPrimitives.ReadDoubleLittleEndian(Value) is var d && !double.IsNaN(d) && d >= long.MinValue && d <= long.MaxValue ? (long)d : null,
        BsonType.Boolean => Value[0] != 0 ? 1 : 0,
        _ => null
    };

    public double? AsDouble() => Type switch
    {
        BsonType.Double => BinaryPrimitives.ReadDoubleLittleEndian(Value),
        BsonType.Int32 => BinaryPrimitives.ReadInt32LittleEndian(Value),
        BsonType.Int64 => BinaryPrimitives.ReadInt64LittleEndian(Value),
        BsonType.Boolean => Value[0] != 0 ? 1 : 0,
        _ => null
    };

    public bool IsDocument => Type is BsonType.Document or BsonType.Array;
}

public ref struct BsonElements
{
    private readonly ReadOnlySpan<byte> _doc;
    private readonly int _end;
    private readonly bool _whole; // the declared length fits in the span: running out of bytes is malformation, not truncation
    private int _pos;
    private BsonElement _current;

    public bool Truncated { get; private set; }
    public bool Malformed { get; private set; }
    /// <summary>True when the enumerator reached the document's terminating NUL.</summary>
    public bool Complete { get; private set; }

    public BsonElements(ReadOnlySpan<byte> doc)
    {
        _doc = doc;
        _pos = 4;
        _current = default;
        Truncated = Malformed = Complete = false;
        if (doc.Length < 4)
        {
            Truncated = true;
            _end = 0;
            _whole = false;
            return;
        }
        var declared = BinaryPrimitives.ReadInt32LittleEndian(doc);
        if (declared < 5)
        {
            Malformed = true;
            _end = 0;
            _whole = true;
            return;
        }
        _whole = declared <= doc.Length;
        _end = Math.Min(declared, doc.Length);
    }

    public readonly BsonElements GetEnumerator() => this;

    public readonly BsonElement Current => _current;

    private void Short()
    {
        if (_whole) Malformed = true; else Truncated = true;
        _pos = _end;
    }

    public bool MoveNext()
    {
        if (_pos >= _end) { if (!Complete && !Malformed) Short(); return false; }
        var type = _doc[_pos];
        if (type == 0)
        {
            Complete = true;
            _pos = _end;
            return false;
        }
        var rest = _doc[(_pos + 1).._end];
        var nul = rest.IndexOf((byte)0);
        if (nul < 0) { Short(); return false; }
        var valueStart = _pos + 1 + nul + 1;
        var size = Bson.ValueSize(type, _doc[valueStart.._end]);
        if (size == -2) { Short(); return false; }
        if (size < 0) { Malformed = true; _pos = _end; return false; }
        _current = new BsonElement(type, rest[..nul], _doc.Slice(valueStart, size), _pos, valueStart + size - _pos);
        _pos = valueStart + size;
        return true;
    }
}
