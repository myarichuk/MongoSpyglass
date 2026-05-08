using System.Runtime.InteropServices;
using SharpArena.Collections;
using SharpArena.Allocators;
using MongoDB.Bson;
using System.Runtime.CompilerServices;

namespace MongoSpyglass.Proxy.Bson;

[StructLayout(LayoutKind.Sequential)]
public readonly unsafe struct BlittableBsonDocument
{
    private readonly byte* _bsonBytes;
    private readonly int _length;
    private readonly ArenaDictionary<ArenaUtf8String, int> _index;

    public int Length => _length;

    internal BlittableBsonDocument(byte* bsonBytes, int length, ArenaDictionary<ArenaUtf8String, int> index)
    {
        _bsonBytes = bsonBytes;
        _length = length;
        _index = index;
    }

    public byte* Pointer => _bsonBytes;
    public bool IsDefault => _bsonBytes == null;

    public bool ContainsKey(ReadOnlySpan<char> name) => TryGetElementOffset(name, out _);

    public ReadOnlySpan<byte> AsReadOnlySpan() => new(_bsonBytes, _length);

    public KeysCollection Keys => new(_index);

    public readonly struct KeysCollection(ArenaDictionary<ArenaUtf8String, int> index) : IEnumerable<ArenaUtf8String>
    {
        public Enumerator GetEnumerator() => new(index);
        IEnumerator<ArenaUtf8String> IEnumerable<ArenaUtf8String>.GetEnumerator() => GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

        public struct Enumerator(ArenaDictionary<ArenaUtf8String, int> index) : IEnumerator<ArenaUtf8String>
        {
            private IEnumerator<ArenaUtf8String> _inner = index.Keys.GetEnumerator();
            public ArenaUtf8String Current => _inner.Current;
            object? System.Collections.IEnumerator.Current => Current;
            public bool MoveNext() => _inner.MoveNext();
            public void Reset() => _inner.Reset();
            public void Dispose() => _inner.Dispose();
        }
    }

    public IEnumerable<ArenaUtf8String> KeysEnumerable => _index.Keys;

    public bool TryGetElementOffset(ReadOnlySpan<char> name, out int offset)
    {
        if (_length == 0)
        {
            offset = 0;
            return false;
        }
        return _index.TryGetValue(name, out offset);
    }

    public int GetInt32(ReadOnlySpan<char> name)
    {
        if (!TryGetElementOffset(name, out var offset))
        {
            throw new KeyNotFoundException($"Property '{name.ToString()}' not found.");
        }
        return GetInt32(offset);
    }

    public long GetInt64(ReadOnlySpan<char> name)
    {
        if (!TryGetElementOffset(name, out var offset))
        {
            throw new KeyNotFoundException($"Property '{name.ToString()}' not found.");
        }
        return GetInt64(offset);
    }

    public double GetDouble(ReadOnlySpan<char> name)
    {
        if (!TryGetElementOffset(name, out var offset))
        {
            throw new KeyNotFoundException($"Property '{name.ToString()}' not found.");
        }
        return GetDouble(offset);
    }

    public bool GetBoolean(ReadOnlySpan<char> name)
    {
        if (!TryGetElementOffset(name, out var offset))
        {
            throw new KeyNotFoundException($"Property '{name.ToString()}' not found.");
        }
        return GetBoolean(offset);
    }

    public ReadOnlySpan<byte> GetStringBytes(ReadOnlySpan<char> name)
    {
        if (!TryGetElementOffset(name, out var offset))
        {
            throw new KeyNotFoundException($"Property '{name.ToString()}' not found.");
        }
        return GetStringBytes(offset);
    }

    public ObjectId GetObjectId(ReadOnlySpan<char> name)
    {
        if (!TryGetElementOffset(name, out var offset))
        {
            throw new KeyNotFoundException($"Property '{name.ToString()}' not found.");
        }
        return GetObjectId(offset);
    }

    public DateTime GetDateTime(ReadOnlySpan<char> name)
    {
        if (!TryGetElementOffset(name, out var offset))
        {
            throw new KeyNotFoundException($"Property '{name.ToString()}' not found.");
        }
        return GetDateTime(offset);
    }

    public Guid GetGuid(ReadOnlySpan<char> name)
    {
        if (!TryGetElementOffset(name, out var offset))
        {
            throw new KeyNotFoundException($"Property '{name.ToString()}' not found.");
        }
        return GetGuid(offset);
    }

    public decimal GetDecimal128(ReadOnlySpan<char> name)
    {
        if (!TryGetElementOffset(name, out var offset))
        {
            throw new KeyNotFoundException($"Property '{name.ToString()}' not found.");
        }
        return GetDecimal128(offset);
    }

    public BlittableBsonDocument GetDocument(ReadOnlySpan<char> name, ArenaAllocator arena)
    {
        if (!TryGetElementOffset(name, out var offset))
        {
            throw new KeyNotFoundException($"Property '{name.ToString()}' not found.");
        }
        return GetDocument(offset, arena);
    }

    public BlittableBsonDocument GetDocument(ArenaUtf8String name, ArenaAllocator arena)
    {
        if (!_index.TryGetValue(name, out var offset))
        {
            throw new KeyNotFoundException($"Property '{name}' not found.");
        }
        return GetDocument(offset, arena);
    }

    public T Get<T>(ArenaUtf8String name)
    {
        if (!_index.TryGetValue(name, out var offset))
        {
            throw new KeyNotFoundException($"Property '{name}' not found.");
        }
        return Get<T>(offset);
    }

    public BlittableBsonArray GetArray(ReadOnlySpan<char> name, ArenaAllocator arena)
    {
        if (!TryGetElementOffset(name, out var offset))
        {
            throw new KeyNotFoundException($"Property '{name.ToString()}' not found.");
        }
        return GetArray(offset, arena);
    }

    public string GetString(ReadOnlySpan<char> name)
    {
        var bytes = GetStringBytes(name);
        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    public int GetInt32(int offset)
    {
        var p = GetDataPointerAtOffset(offset, out var type, out _);
        return type switch
        {
            BlittableBsonConstants.BsonType.Int32 => *(int*)p,
            BlittableBsonConstants.BsonType.Int64 => (int)*(long*)p,
            BlittableBsonConstants.BsonType.Double => (int)*(double*)p,
            _ => throw new InvalidCastException($"Cannot cast {type} to Int32")
        };
    }

    public long GetInt64(int offset)
    {
        var p = GetDataPointerAtOffset(offset, out var type, out _);
        return type switch
        {
            BlittableBsonConstants.BsonType.Int64 => *(long*)p,
            BlittableBsonConstants.BsonType.Int32 => *(int*)p,
            BlittableBsonConstants.BsonType.Double => (long)*(double*)p,
            _ => throw new InvalidCastException($"Cannot cast {type} to Int64")
        };
    }

    public double GetDouble(int offset)
    {
        var p = GetDataPointerAtOffset(offset, out var type, out _);
        return type switch
        {
            BlittableBsonConstants.BsonType.Double => *(double*)p,
            BlittableBsonConstants.BsonType.Int32 => *(int*)p,
            BlittableBsonConstants.BsonType.Int64 => *(long*)p,
            _ => throw new InvalidCastException($"Cannot cast {type} to Double")
        };
    }

    public bool GetBoolean(int offset)
    {
        var p = GetDataPointerAtOffset(offset, out var type, out _);
        if (type != BlittableBsonConstants.BsonType.Boolean)
        {
            throw new InvalidCastException($"Cannot cast {type} to Boolean");
        }

        return *p != 0;
    }

    public ReadOnlySpan<byte> GetStringBytes(int offset)
    {
        var p = GetDataPointerAtOffset(offset, out var type, out _);
        if (type != BlittableBsonConstants.BsonType.String)
        {
            throw new InvalidCastException($"Cannot cast {type} to String");
        }

        int strLen = *(int*)p;
        return new ReadOnlySpan<byte>(p + 4, strLen - 1);
    }

    public string GetString(int offset) => System.Text.Encoding.UTF8.GetString(GetStringBytes(offset));

    public ObjectId GetObjectId(int offset)
    {
        var p = GetDataPointerAtOffset(offset, out var type, out _);
        if (type != BlittableBsonConstants.BsonType.ObjectId)
        {
            throw new InvalidCastException($"Cannot cast {type} to ObjectId");
        }

        byte[] bytes = new byte[12];
        for (int i = 0; i < 12; i++)
        {
            bytes[i] = p[i];
        }

        return new ObjectId(bytes);
    }

    public DateTime GetDateTime(int offset)
    {
        var p = GetDataPointerAtOffset(offset, out var type, out _);
        if (type != BlittableBsonConstants.BsonType.DateTime)
        {
            throw new InvalidCastException($"Cannot cast {type} to DateTime");
        }

        long ms = *(long*)p;
        return DateTime.UnixEpoch.AddMilliseconds(ms);
    }

    public Guid GetGuid(int offset)
    {
        var p = GetDataPointerAtOffset(offset, out var type, out _);
        if (type != BlittableBsonConstants.BsonType.Binary)
        {
            throw new InvalidCastException($"Cannot cast {type} to Guid");
        }

        int len = *(int*)p;
        byte subtype = p[4];
        if (len != 16)
        {
            throw new InvalidOperationException($"Invalid Guid length: {len}");
        }

        var span = new ReadOnlySpan<byte>(p + 5, 16);
        if (subtype == 4)
        {
            return new Guid(span, bigEndian: true);
        }

        if (subtype == 3)
        {
            return new Guid(span);
        }

        throw new InvalidOperationException($"Unsupported Guid subtype: {subtype}");
    }

    public decimal GetDecimal128(int offset)
    {
        var p = GetDataPointerAtOffset(offset, out var type, out _);
        if (type != BlittableBsonConstants.BsonType.Decimal128)
        {
            throw new InvalidCastException($"Cannot cast {type} to Decimal128");
        }

        long low = *(long*)p;
        long high = *(long*)(p + 8);
        var d128 = Decimal128.FromIEEEBits((ulong)high, (ulong)low);
        return Decimal128.ToDecimal(d128);
    }

    public BlittableBsonDocument GetDocument(int offset, ArenaAllocator arena)
    {
        var p = GetDataPointerAtOffset(offset, out var type, out var length);
        if (type != BlittableBsonConstants.BsonType.Document)
        {
            throw new InvalidCastException($"Cannot cast {type} to Document");
        }

        return ArenaBsonReader.ReadInPlace(p, length, arena);
    }

    public BlittableBsonArray GetArray(int offset, ArenaAllocator arena)
    {
        var p = GetDataPointerAtOffset(offset, out var type, out var length);
        if (type != BlittableBsonConstants.BsonType.Array)
        {
            throw new InvalidCastException($"Cannot cast {type} to Array");
        }

        return new BlittableBsonArray(p, length, arena);
    }

    public T Get<T>(ReadOnlySpan<char> name)
    {
        if (!TryGetElementOffset(name, out var offset))
        {
            throw new KeyNotFoundException($"Property '{name.ToString()}' not found.");
        }
        return Get<T>(offset);
    }

    public T Get<T>(int offset)
    {
        if (typeof(T) == typeof(int))
        {
            return (T)(object)GetInt32(offset);
        }

        if (typeof(T) == typeof(long))
        {
            return (T)(object)GetInt64(offset);
        }

        if (typeof(T) == typeof(double))
        {
            return (T)(object)GetDouble(offset);
        }

        if (typeof(T) == typeof(bool))
        {
            return (T)(object)GetBoolean(offset);
        }

        if (typeof(T) == typeof(string))
        {
            return (T)(object)GetString(offset);
        }

        if (typeof(T) == typeof(ObjectId))
        {
            return (T)(object)GetObjectId(offset);
        }

        if (typeof(T) == typeof(DateTime))
        {
            return (T)(object)GetDateTime(offset);
        }

        if (typeof(T) == typeof(Guid))
        {
            return (T)(object)GetGuid(offset);
        }

        if (typeof(T) == typeof(decimal))
        {
            return (T)(object)GetDecimal128(offset);
        }

        var p = GetDataPointerAtOffset(offset, out var type, out var length);
        return BlittableConverter<T>.Instance.Read(p, type, length);
    }

    private byte* GetDataPointerAtOffset(int offset, out BlittableBsonConstants.BsonType type, out int length)
    {
        type = (BlittableBsonConstants.BsonType)_bsonBytes[offset];
        var ptr = _bsonBytes + offset + 1;
        while (*ptr != 0) ptr++; 
        var dataPtr = ptr + 1;
        int dataPos = (int)(dataPtr - _bsonBytes);
        int endPos = ArenaBsonReader.SkipElement(_bsonBytes, dataPos, type, _length);
        length = endPos - dataPos;
        return dataPtr;
    }
}
