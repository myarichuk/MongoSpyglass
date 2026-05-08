using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SharpArena.Allocators;
using SharpArena.Collections;
using MongoDB.Bson;

namespace MongoSpyglass.Proxy.Bson;

public readonly unsafe struct BlittableBsonArray : IReadOnlyList<BlittableBsonArray.Element>
{
    private readonly byte* _bsonBytes;
    private readonly int _length;
    private readonly ArenaAllocator _arena;
    private readonly ArenaList<int> _index;

    internal BlittableBsonArray(byte* bsonBytes, int length, ArenaAllocator arena)
    {
        _bsonBytes = bsonBytes;
        _length = length;
        _arena = arena;

        _index = new ArenaList<int>(arena, initialCapacity: 16);
        
        int pos = 4;
        while (pos < length - 1)
        {
            var type = (BlittableBsonConstants.BsonType)bsonBytes[pos];
            int nameStart = pos + 1;
            int nameEnd = nameStart;
            while (bsonBytes[nameEnd] != 0) nameEnd++;

            _index.Add(pos);

            var dataPtr = bsonBytes + nameEnd + 1;
            int dataPos = (int)(dataPtr - bsonBytes);
            pos = ArenaBsonReader.SkipElement(bsonBytes, dataPos, type, length);
        }
    }

    public int Count => _index.Length;

    public Element this[int index]
    {
        get
        {
            int pos = _index[index];
            var type = (BlittableBsonConstants.BsonType)_bsonBytes[pos];
            
            int nameStart = pos + 1;
            int nameEnd = nameStart;
            while (_bsonBytes[nameEnd] != 0) nameEnd++;

            var dataPtr = _bsonBytes + nameEnd + 1;
            int dataPos = (int)(dataPtr - _bsonBytes);
            int endPos = ArenaBsonReader.SkipElement(_bsonBytes, dataPos, type, _length);

            return new Element(dataPtr, type, endPos - dataPos, _arena);
        }
    }

    public Enumerator GetEnumerator() => new(this);
    IEnumerator<Element> IEnumerable<Element>.GetEnumerator() => GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public struct Enumerator : IEnumerator<Element>
    {
        private readonly BlittableBsonArray _array;
        private int _index;
        private Element _current;

        internal Enumerator(BlittableBsonArray array)
        {
            _array = array;
            _index = -1;
            _current = default;
        }

        public bool MoveNext()
        {
            if (++_index >= _array.Count)
            {
                return false;
            }

            _current = _array[_index];
            return true;
        }

        public void Reset() => _index = -1;
        public Element Current => _current;
        object IEnumerator.Current => Current;
        public void Dispose() { }
    }

    public readonly struct Element
    {
        private readonly byte* _p;
        private readonly BlittableBsonConstants.BsonType _type;
        private readonly int _length;
        private readonly ArenaAllocator _arena;

        internal Element(byte* p, BlittableBsonConstants.BsonType type, int length, ArenaAllocator arena)
        {
            _p = p;
            _type = type;
            _length = length;
            _arena = arena;
        }

        public BlittableBsonConstants.BsonType Type => _type;

        public int GetInt32() => _type switch
        {
            BlittableBsonConstants.BsonType.Int32 => *(int*)_p,
            BlittableBsonConstants.BsonType.Int64 => (int)*(long*)_p,
            BlittableBsonConstants.BsonType.Double => (int)*(double*)_p,
            _ => throw new InvalidCastException($"Cannot cast {_type} to Int32")
        };

        public string GetString()
        {
            if (_type != BlittableBsonConstants.BsonType.String)
            {
                throw new InvalidCastException($"Cannot cast {_type} to String");
            }

            int len = *(int*)_p;
            return System.Text.Encoding.UTF8.GetString(_p + 4, len - 1);
        }

        public T Get<T>()
        {
            if (typeof(T) == typeof(int))
            {
                return (T)(object)GetInt32();
            }

            if (typeof(T) == typeof(string))
            {
                return (T)(object)GetString();
            }

            if (typeof(T) == typeof(long))
            {
                if (_type != BlittableBsonConstants.BsonType.Int64)
                {
                    throw new InvalidCastException($"Cannot cast {_type} to Int64");
                }

                return (T)(object)*(long*)_p;
            }
            if (typeof(T) == typeof(double))
            {
                if (_type != BlittableBsonConstants.BsonType.Double)
                {
                    throw new InvalidCastException($"Cannot cast {_type} to Double");
                }

                return (T)(object)*(double*)_p;
            }
            if (typeof(T) == typeof(bool))
            {
                if (_type != BlittableBsonConstants.BsonType.Boolean)
                {
                    throw new InvalidCastException($"Cannot cast {_type} to Boolean");
                }

                return (T)(object)(*_p != 0);
            }
            if (typeof(T) == typeof(ObjectId))
            {
                if (_type != BlittableBsonConstants.BsonType.ObjectId)
                {
                    throw new InvalidCastException($"Cannot cast {_type} to ObjectId");
                }

                byte[] bytes = new byte[12];
                for(int i=0; i<12; i++)
                {
                    bytes[i] = _p[i];
                }

                return (T)(object)new ObjectId(bytes);
            }
            if (typeof(T) == typeof(DateTime))
            {
                if (_type != BlittableBsonConstants.BsonType.DateTime)
                {
                    throw new InvalidCastException($"Cannot cast {_type} to DateTime");
                }

                return (T)(object)DateTime.UnixEpoch.AddMilliseconds(*(long*)_p);
            }

            return BlittableConverter<T>.Instance.Read(_p, _type, _length);
        }

        public BlittableBsonDocument GetDocument()
        {
            if (_type != BlittableBsonConstants.BsonType.Document)
            {
                throw new InvalidCastException($"Cannot cast {_type} to Document");
            }

            return ArenaBsonReader.ReadInPlace(_p, _length, _arena);
        }
    }
}
