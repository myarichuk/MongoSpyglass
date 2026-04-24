using SharpArena.Allocators;
using SharpArena.Collections;
using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace MongoSpyglass.Proxy.WireProtocol
{
    public enum BsonType : byte
    {
        EndOfDocument = 0x00,
        Double = 0x01,
        String = 0x02,
        Document = 0x03,
        Array = 0x04,
        Binary = 0x05,
        Undefined = 0x06,
        ObjectId = 0x07,
        Boolean = 0x08,
        DateTime = 0x09,
        Null = 0x0A,
        Regex = 0x0B,
        DbPointer = 0x0C,
        JavaScript = 0x0D,
        Symbol = 0x0E,
        JavaScriptWithScope = 0x0F,
        Int32 = 0x10,
        Timestamp = 0x11,
        Int64 = 0x12,
        Decimal128 = 0x13,
        MinKey = 0xFF,
        MaxKey = 0x7F
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BsonElementDescriptor
    {
        public BsonType Type;
        public uint NameHash;
        public int ValueOffset;
        public int ValueLength;
    }

    public ref struct ArenaBsonReader
    {
        private ReadOnlySpan<byte> _bsonData;
        private ArenaAllocator _allocator;
        private ArenaList<BsonElementDescriptor> _elements;

        public ArenaList<BsonElementDescriptor> Elements => _elements;

        public ArenaBsonReader(ReadOnlySpan<byte> bsonData, ArenaAllocator allocator)
        {
            _bsonData = bsonData;
            _allocator = allocator;
            _elements = new ArenaList<BsonElementDescriptor>(allocator, 16);
            Parse();
        }

        private void Parse()
        {
            if (_bsonData.Length < 5) return; // At least length + null terminator

            int totalLength = BinaryPrimitives.ReadInt32LittleEndian(_bsonData.Slice(0, 4));
            if (totalLength > _bsonData.Length) return; // Truncated or invalid

            int offset = 4;
            while (offset < totalLength - 1)
            {
                if (offset >= _bsonData.Length) break;

                BsonType type = (BsonType)_bsonData[offset];
                if (type == BsonType.EndOfDocument) break;
                offset++;

                // Read C-String name
                int nameOffset = offset;
                while (offset < _bsonData.Length && _bsonData[offset] != 0)
                {
                    offset++;
                }

                if (offset >= _bsonData.Length) break;

                ReadOnlySpan<byte> nameSpan = _bsonData.Slice(nameOffset, offset - nameOffset);
                uint nameHash = ComputeHash(nameSpan);
                offset++; // Skip null terminator

                int valueOffset = offset;
                int valueLength = GetValueLength(type, offset);

                if (valueLength < 0 || offset + valueLength > _bsonData.Length)
                {
                    // Invalid or truncated document
                    break;
                }

                var descriptor = new BsonElementDescriptor
                {
                    Type = type,
                    NameHash = nameHash,
                    ValueOffset = valueOffset,
                    ValueLength = valueLength
                };
                _elements.Add(in descriptor);

                offset += valueLength;
            }
        }

        private int GetValueLength(BsonType type, int offset)
        {
            if (offset >= _bsonData.Length) return -1;

            switch (type)
            {
                case BsonType.Double: return 8;
                case BsonType.String:
                case BsonType.Symbol:
                case BsonType.JavaScript:
                    if (offset + 4 > _bsonData.Length) return -1;
                    return 4 + BinaryPrimitives.ReadInt32LittleEndian(_bsonData.Slice(offset, 4));
                case BsonType.Document:
                case BsonType.Array:
                    if (offset + 4 > _bsonData.Length) return -1;
                    return BinaryPrimitives.ReadInt32LittleEndian(_bsonData.Slice(offset, 4));
                case BsonType.Binary:
                    if (offset + 5 > _bsonData.Length) return -1;
                    int binLen = BinaryPrimitives.ReadInt32LittleEndian(_bsonData.Slice(offset, 4));
                    return 4 + 1 + binLen; // length (4) + subtype (1) + data
                case BsonType.Undefined: return 0;
                case BsonType.ObjectId: return 12;
                case BsonType.Boolean: return 1;
                case BsonType.DateTime: return 8;
                case BsonType.Null: return 0;
                case BsonType.Regex:
                    // Need to scan two C-strings
                    int regexLen = 0;
                    while (offset + regexLen < _bsonData.Length && _bsonData[offset + regexLen] != 0) regexLen++;
                    regexLen++; // null
                    while (offset + regexLen < _bsonData.Length && _bsonData[offset + regexLen] != 0) regexLen++;
                    regexLen++; // null
                    return regexLen;
                case BsonType.DbPointer:
                    if (offset + 4 > _bsonData.Length) return -1;
                    int ptrStrLen = BinaryPrimitives.ReadInt32LittleEndian(_bsonData.Slice(offset, 4));
                    return 4 + ptrStrLen + 12;
                case BsonType.JavaScriptWithScope:
                    if (offset + 4 > _bsonData.Length) return -1;
                    return BinaryPrimitives.ReadInt32LittleEndian(_bsonData.Slice(offset, 4));
                case BsonType.Int32: return 4;
                case BsonType.Timestamp: return 8;
                case BsonType.Int64: return 8;
                case BsonType.Decimal128: return 16;
                case BsonType.MinKey: return 0;
                case BsonType.MaxKey: return 0;
                default: return -1; // Unknown
            }
        }

        // FNV-1a Hash for byte span
        public static uint ComputeHash(ReadOnlySpan<byte> data)
        {
            uint hash = 2166136261;
            foreach (byte b in data)
            {
                hash ^= b;
                hash *= 16777619;
            }
            return hash;
        }

        public static uint ComputeHash(string str)
        {
            int maxBytes = Encoding.UTF8.GetMaxByteCount(str.Length);
            Span<byte> buffer = maxBytes <= 256 ? stackalloc byte[maxBytes] : new byte[maxBytes];
            int bytesWritten = Encoding.UTF8.GetBytes(str, buffer);
            return ComputeHash(buffer.Slice(0, bytesWritten));
        }

        public bool TryFindElement(uint nameHash, out BsonElementDescriptor element)
        {
            for (int i = 0; i < _elements.Length; i++)
            {
                if (_elements[i].NameHash == nameHash)
                {
                    element = _elements[i];
                    return true;
                }
            }
            element = default;
            return false;
        }

        public bool TryFindElement(string name, out BsonElementDescriptor element)
        {
            return TryFindElement(ComputeHash(name), out element);
        }

        public ReadOnlySpan<byte> GetValueSpan(BsonElementDescriptor element)
        {
            return _bsonData.Slice(element.ValueOffset, element.ValueLength);
        }

        public string GetStringValue(BsonElementDescriptor element)
        {
            if (element.Type != BsonType.String && element.Type != BsonType.Symbol)
                throw new InvalidOperationException("Element is not a string.");

            var span = GetValueSpan(element);
            if (span.Length < 5) return string.Empty;

            int len = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(0, 4));
            // len includes null terminator
            return Encoding.UTF8.GetString(span.Slice(4, len - 1));
        }

        public ReadOnlySpan<byte> GetStringSpan(BsonElementDescriptor element)
        {
            if (element.Type != BsonType.String && element.Type != BsonType.Symbol)
                throw new InvalidOperationException("Element is not a string.");

            var span = GetValueSpan(element);
            if (span.Length < 5) return ReadOnlySpan<byte>.Empty;

            int len = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(0, 4));
            // len includes null terminator
            return span.Slice(4, len - 1);
        }
    }
}
