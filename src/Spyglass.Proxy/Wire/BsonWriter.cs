using System.Buffers.Binary;
using System.Text;

namespace Spyglass.Proxy.Wire;

/// <summary>
/// Minimal growable BSON writer for the handshake rewrites and the observer's redacted copies.
/// Not used on the per-frame forwarding path.
/// </summary>
public sealed class BsonWriter
{
    private byte[] _buf;
    private int _len;
    private readonly Stack<int> _open = new();

    public BsonWriter(int capacity = 256) => _buf = new byte[capacity];

    public int Length => _len;
    public ReadOnlySpan<byte> WrittenSpan => _buf.AsSpan(0, _len);
    public byte[] ToArray() => WrittenSpan.ToArray();

    public void Reset()
    {
        _len = 0;
        _open.Clear();
    }

    private Span<byte> Grow(int n)
    {
        if (_len + n > _buf.Length)
            Array.Resize(ref _buf, Math.Max(_buf.Length * 2, _len + n));
        var s = _buf.AsSpan(_len, n);
        _len += n;
        return s;
    }

    public void WriteRaw(ReadOnlySpan<byte> bytes) => bytes.CopyTo(Grow(bytes.Length));

    public void WriteInt32Raw(int v) => BinaryPrimitives.WriteInt32LittleEndian(Grow(4), v);
    public void WriteInt64Raw(long v) => BinaryPrimitives.WriteInt64LittleEndian(Grow(8), v);
    public void WriteByteRaw(byte b) => Grow(1)[0] = b;

    public void PatchInt32(int offset, int v) => BinaryPrimitives.WriteInt32LittleEndian(_buf.AsSpan(offset, 4), v);

    public void StartDocument()
    {
        _open.Push(_len);
        Grow(4);
    }

    public void EndDocument()
    {
        Grow(1)[0] = 0;
        var start = _open.Pop();
        PatchInt32(start, _len - start);
    }

    private void Name(byte type, ReadOnlySpan<byte> name)
    {
        Grow(1)[0] = type;
        WriteRaw(name);
        Grow(1)[0] = 0;
    }

    private void Name(byte type, string name)
    {
        Grow(1)[0] = type;
        var n = Encoding.UTF8.GetByteCount(name);
        Encoding.UTF8.GetBytes(name, Grow(n));
        Grow(1)[0] = 0;
    }

    /// <summary>Copies an element verbatim.</summary>
    public void WriteElement(in BsonElement e)
    {
        Name(e.Type, e.Name);
        WriteRaw(e.Value);
    }

    public void WriteElement(ReadOnlySpan<byte> name, byte type, ReadOnlySpan<byte> value)
    {
        Name(type, name);
        WriteRaw(value);
    }

    public void StartDocument(string name)
    {
        Name(BsonType.Document, name);
        StartDocument();
    }

    public void StartDocument(ReadOnlySpan<byte> name)
    {
        Name(BsonType.Document, name);
        StartDocument();
    }

    public void StartArray(ReadOnlySpan<byte> name)
    {
        Name(BsonType.Array, name);
        StartDocument();
    }

    public void StartArray(string name)
    {
        Name(BsonType.Array, name);
        StartDocument();
    }

    public void WriteString(string name, string value)
    {
        Name(BsonType.String, name);
        WriteStringValue(value);
    }

    public void WriteString(ReadOnlySpan<byte> name, string value)
    {
        Name(BsonType.String, name);
        WriteStringValue(value);
    }

    public void WriteStringUtf8(ReadOnlySpan<byte> name, ReadOnlySpan<byte> utf8)
    {
        Name(BsonType.String, name);
        WriteInt32Raw(utf8.Length + 1);
        WriteRaw(utf8);
        Grow(1)[0] = 0;
    }

    private void WriteStringValue(string value)
    {
        var n = Encoding.UTF8.GetByteCount(value);
        WriteInt32Raw(n + 1);
        Encoding.UTF8.GetBytes(value, Grow(n));
        Grow(1)[0] = 0;
    }

    public void WriteInt32(string name, int value)
    {
        Name(BsonType.Int32, name);
        WriteInt32Raw(value);
    }

    public void WriteInt32(ReadOnlySpan<byte> name, int value)
    {
        Name(BsonType.Int32, name);
        WriteInt32Raw(value);
    }

    public void WriteInt64(string name, long value)
    {
        Name(BsonType.Int64, name);
        WriteInt64Raw(value);
    }

    public void WriteBoolean(string name, bool value)
    {
        Name(BsonType.Boolean, name);
        Grow(1)[0] = value ? (byte)1 : (byte)0;
    }

    public void WriteNull(string name) => Name(BsonType.Null, name);

    public void WriteDouble(string name, double value)
    {
        Name(BsonType.Double, name);
        BinaryPrimitives.WriteDoubleLittleEndian(Grow(8), value);
    }

    /// <summary>Array index names "0", "1", ... without allocating.</summary>
    public static ReadOnlySpan<byte> IndexName(int i, Span<byte> scratch)
    {
        i.TryFormat(scratch, out var n, provider: System.Globalization.CultureInfo.InvariantCulture);
        return scratch[..n];
    }
}
