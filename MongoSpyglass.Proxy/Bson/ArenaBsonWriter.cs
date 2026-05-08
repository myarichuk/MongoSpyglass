using System.Buffers;
using System.Text;
using SharpArena.Allocators;
using SharpArena.Collections;
using MongoDB.Bson;

namespace MongoSpyglass.Proxy.Bson;

public unsafe struct ArenaBsonWriter(ArenaAllocator arena, int initialCapacity = 256)
{
    private ArenaList<byte> _buffer = new(arena, initialCapacity);
    private ArenaList<int> _lengthOffsets = new(arena, 8);

    private static readonly string[] IndexStrings = GenerateIndexStrings();
    private const int IndexCacheSize = 100; 
    
    private static string[] GenerateIndexStrings()
    {
        var strings = new string[IndexCacheSize];
        for (int i = 0; i < IndexCacheSize; i++)
        {
            strings[i] = i.ToString();
        }

        return strings;
    }

    public int Position => _buffer.Length;

    public BlittableBsonDocument Commit(ArenaAllocator arena)
    {
        if (_lengthOffsets.Length != 0)
        {
            throw new InvalidOperationException("Not all documents/arrays were closed. Depth: " + _lengthOffsets.Length);
        }
        
        return ArenaBsonReader.ReadInPlace(_buffer.AsPtr, _buffer.Length, arena);
    }

    public void WriteStartDocument()
    {
        _lengthOffsets.Add(_buffer.Length);
        _buffer.Add(0); _buffer.Add(0); _buffer.Add(0); _buffer.Add(0);
    }

    public void WriteEndDocument()
    {
        if (_lengthOffsets.Length == 0)
        {
            throw new InvalidOperationException("No open document/array.");
        }

        _buffer.Add(BlittableBsonConstants.DocumentTerminator);
        
        int offsetIndex = _lengthOffsets.Length - 1;
        int startPos = _lengthOffsets[offsetIndex];
        _lengthOffsets.RemoveAt(offsetIndex);

        int totalLength = _buffer.Length - startPos;
        
        byte* pLen = _buffer.AsPtr + startPos;
        *(int*)pLen = totalLength;
    }

    public void WriteStartArray() => WriteStartDocument(); 
    public void WriteEndArray() => WriteEndDocument();

    public void WriteName(ReadOnlySpan<char> name, BlittableBsonConstants.BsonType type)
    {
        _buffer.Add((byte)type);
        
        int byteCount = Encoding.UTF8.GetByteCount(name);
        if (byteCount <= 128)
        {
            Span<byte> nameBytes = stackalloc byte[128];
            int written = Encoding.UTF8.GetBytes(name, nameBytes);
            for (int i = 0; i < written; i++)
            {
                _buffer.Add(nameBytes[i]);
            }
        }
        else
        {
            byte[] rented = ArrayPool<byte>.Shared.Rent(byteCount);
            try
            {
                int written = Encoding.UTF8.GetBytes(name, rented);
                for (int i = 0; i < written; i++)
                {
                    _buffer.Add(rented[i]);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
        
        _buffer.Add(0); 
    }

    public void WriteInt32(ReadOnlySpan<char> name, int value)
    {
        WriteName(name, BlittableBsonConstants.BsonType.Int32);
        WriteInt32Value(value);
    }

    public void WriteInt64(ReadOnlySpan<char> name, long value)
    {
        WriteName(name, BlittableBsonConstants.BsonType.Int64);
        WriteInt64Value(value);
    }

    public void WriteDouble(ReadOnlySpan<char> name, double value)
    {
        WriteName(name, BlittableBsonConstants.BsonType.Double);
        WriteDoubleValue(value);
    }

    public void WriteBoolean(ReadOnlySpan<char> name, bool value)
    {
        WriteName(name, BlittableBsonConstants.BsonType.Boolean);
        WriteBooleanValue(value);
    }

    public void WriteString(ReadOnlySpan<char> name, ReadOnlySpan<char> value)
    {
        WriteName(name, BlittableBsonConstants.BsonType.String);
        WriteStringValue(value);
    }

    public void WriteObjectId(ReadOnlySpan<char> name, ObjectId value)
    {
        WriteName(name, BlittableBsonConstants.BsonType.ObjectId);
        WriteObjectIdValue(value);
    }

    public void WriteDateTime(ReadOnlySpan<char> name, DateTime value)
    {
        WriteName(name, BlittableBsonConstants.BsonType.DateTime);
        WriteDateTimeValue(value);
    }

    public void WriteNull(ReadOnlySpan<char> name)
    {
        WriteName(name, BlittableBsonConstants.BsonType.Null);
    }

    public void WriteBinary(ReadOnlySpan<char> name, ReadOnlySpan<byte> bytes, byte subtype = 0)
    {
        WriteName(name, BlittableBsonConstants.BsonType.Binary);
        WriteBinaryValue(bytes, subtype);
    }

    public void WriteGuid(ReadOnlySpan<char> name, Guid value)
    {
        WriteName(name, BlittableBsonConstants.BsonType.Binary);
        WriteGuidValue(value);
    }

    public void WriteStartDocument(ReadOnlySpan<char> name)
    {
        WriteName(name, BlittableBsonConstants.BsonType.Document);
        WriteStartDocument();
    }

    public void WriteStartDocument(int index)
    {
        if ((uint)index < (uint)IndexStrings.Length)
        {
            WriteStartDocument(IndexStrings[index]);
        }
        else
        {
            Span<char> name = stackalloc char[11];
            index.TryFormat(name, out int charsWritten);
            WriteStartDocument(name[..charsWritten]);
        }
    }

    public void WriteStartArray(ReadOnlySpan<char> name)
    {
        WriteName(name, BlittableBsonConstants.BsonType.Array);
        WriteStartArray();
    }

    public void WriteName(int index, BlittableBsonConstants.BsonType type)
    {
        if ((uint)index < (uint)IndexStrings.Length)
        {
            WriteName(IndexStrings[index], type);
        }
        else
        {
            Span<char> name = stackalloc char[11];
            index.TryFormat(name, out int charsWritten);
            WriteName(name[..charsWritten], type);
        }
    }

    public void WriteInt32Value(int value)
    {
        _buffer.Add((byte)value);
        _buffer.Add((byte)(value >> 8));
        _buffer.Add((byte)(value >> 16));
        _buffer.Add((byte)(value >> 24));
    }

    public void WriteInt64Value(long value)
    {
        _buffer.Add((byte)value);
        _buffer.Add((byte)(value >> 8));
        _buffer.Add((byte)(value >> 16));
        _buffer.Add((byte)(value >> 24));
        _buffer.Add((byte)(value >> 32));
        _buffer.Add((byte)(value >> 40));
        _buffer.Add((byte)(value >> 48));
        _buffer.Add((byte)(value >> 56));
    }

    public void WriteDoubleValue(double value)
    {
        long val = *(long*)&value;
        WriteInt64Value(val);
    }

    public void WriteBooleanValue(bool value) => _buffer.Add(value ? (byte)1 : (byte)0);

    public void WriteStringValue(ReadOnlySpan<char> value)
    {
        int byteCount = Encoding.UTF8.GetByteCount(value);
        WriteInt32Value(byteCount + 1);

        if (byteCount <= 512)
        {
            Span<byte> valBytes = stackalloc byte[512];
            int written = Encoding.UTF8.GetBytes(value, valBytes);
            for (int i = 0; i < written; i++)
            {
                _buffer.Add(valBytes[i]);
            }
        }
        else
        {
            byte[] rented = ArrayPool<byte>.Shared.Rent(byteCount);
            try
            {
                int written = Encoding.UTF8.GetBytes(value, rented);
                for (int i = 0; i < written; i++)
                {
                    _buffer.Add(rented[i]);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }

        _buffer.Add(0);
    }

    public void WriteObjectIdValue(ObjectId value)
    {
        var bytes = value.ToByteArray();
        for (int i = 0; i < 12; i++)
        {
            _buffer.Add(bytes[i]);
        }
    }

    public void WriteDateTimeValue(DateTime value)
    {
        long ms = (long)(value.ToUniversalTime() - DateTime.UnixEpoch).TotalMilliseconds;
        WriteInt64Value(ms);
    }

    public void WriteBinaryValue(ReadOnlySpan<byte> bytes, byte subtype = 0)
    {
        WriteInt32Value(bytes.Length);
        _buffer.Add(subtype);
        for (int i = 0; i < bytes.Length; i++)
        {
            _buffer.Add(bytes[i]);
        }
    }

    public void WriteGuidValue(Guid value)
    {
        WriteInt32Value(16);
        _buffer.Add(0x04); 
        
        Span<byte> bytes = stackalloc byte[16];
        value.TryWriteBytes(bytes, bigEndian: true, out _);
        for (int i = 0; i < 16; i++)
        {
            _buffer.Add(bytes[i]);
        }
    }

    public void WriteDecimal128(ReadOnlySpan<char> name, decimal value)
    {
        WriteName(name, BlittableBsonConstants.BsonType.Decimal128);
        WriteDecimal128Value(value);
    }

    public void WriteDecimal128Value(decimal value)
    {
        var d128 = new Decimal128(value);
        WriteInt64Value((long)d128.GetIEEELowBits());
        WriteInt64Value((long)d128.GetIEEEHighBits());
    }

    public void WriteBsonValue(ReadOnlySpan<char> name, BsonValue value)
    {
        if (value.IsInt32)
        {
            WriteInt32(name, value.AsInt32);
        }
        else if (value.IsInt64)
        {
            WriteInt64(name, value.AsInt64);
        }
        else if (value.IsDouble)
        {
            WriteDouble(name, value.AsDouble);
        }
        else if (value.IsBoolean)
        {
            WriteBoolean(name, value.AsBoolean);
        }
        else if (value.IsString)
        {
            WriteString(name, value.AsString.AsSpan());
        }
        else if (value.IsObjectId)
        {
            WriteObjectId(name, value.AsObjectId);
        }
        else if (value.BsonType == BsonType.DateTime)
        {
            WriteDateTime(name, value.ToUniversalTime());
        }
        else if (value.IsGuid)
        {
            WriteGuid(name, value.AsGuid);
        }
        else if (value.IsBsonNull)
        {
            WriteNull(name);
        }
        else
        {
            WriteName(name, (BlittableBsonConstants.BsonType)value.BsonType);
            using var ms = new System.IO.MemoryStream();
            using (var bsonWriter = new MongoDB.Bson.IO.BsonBinaryWriter(ms))
            {
                bsonWriter.WriteStartDocument();
                bsonWriter.WriteName("v");
                MongoDB.Bson.Serialization.BsonSerializer.Serialize(bsonWriter, value);
                bsonWriter.WriteEndDocument();
            }
            var bytes = ms.ToArray();
            WriteRaw(new ReadOnlySpan<byte>(bytes, 7, bytes.Length - 8));
        }
    }

    public void WriteRaw(ReadOnlySpan<byte> bytes)
    {
        for (int i = 0; i < bytes.Length; i++)
        {
            _buffer.Add(bytes[i]);
        }
    }

    public void WriteInt32(int index, int value)
    {
        if ((uint)index < (uint)IndexStrings.Length)
        {
            WriteInt32(IndexStrings[index], value);
        }
        else
        {
            Span<char> name = stackalloc char[11];
            index.TryFormat(name, out int charsWritten);
            WriteInt32(name[..charsWritten], value);
        }
    }

    public void WriteString(int index, ReadOnlySpan<char> value)
    {
        if ((uint)index < (uint)IndexStrings.Length)
        {
            WriteString(IndexStrings[index], value);
        }
        else
        {
            Span<char> name = stackalloc char[11];
            index.TryFormat(name, out var charsWritten);
            WriteString(name[..charsWritten], value);
        }
    }
}
