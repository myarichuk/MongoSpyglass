using System.Runtime.CompilerServices;
using SharpArena.Allocators;
using SharpArena.Collections;

namespace MongoSpyglass.Proxy.Bson;

public static unsafe class ArenaBsonReader
{
    public static BlittableBsonDocument Read(byte[] bytes, ArenaAllocator arena) => 
        Read(new ReadOnlySpan<byte>(bytes), arena);

    public static BlittableBsonDocument Read(ReadOnlySpan<byte> bytes, ArenaAllocator arena)
    {
        var len = bytes.Length;
        var pBuffer = (byte*)arena.Alloc((nuint)len);
        bytes.CopyTo(new Span<byte>(pBuffer, len));
        return ReadInPlace(pBuffer, len, arena);
    }

    public static BlittableBsonDocument ReadInPlace(byte* pBuffer, int len, ArenaAllocator arena)
    {
        if (len < 5)
        {
            throw new ArgumentException("Invalid BSON length", nameof(len));
        }

        var index = new ArenaDictionary<ArenaUtf8String, int>(arena);
        var keyCache = new ArenaDictionary<ArenaUtf8String, ArenaUtf8String>(arena);

        int pos = 4; // length header
        while (pos < len - 1)
        {
            var type = (BlittableBsonConstants.BsonType)pBuffer[pos];
            int nameStart = pos + 1;
            int nameEnd = nameStart;
            while (nameEnd < len && pBuffer[nameEnd] != 0) nameEnd++;
            
            if (nameEnd >= len)
            {
                throw new IndexOutOfRangeException("BSON element name terminator not found");
            }

            var nameSpan = new ReadOnlySpan<byte>(pBuffer + nameStart, nameEnd - nameStart);
            var clonedNameSpan = ArenaUtf8String.Clone(nameSpan, arena);

            if (!keyCache.TryGetValue(clonedNameSpan, out var name))
            {
                name = clonedNameSpan;
                keyCache.Add(name, name);
            }

            index.Add(name, pos); // offset of the element (including type)
            pos = SkipElement(pBuffer, nameEnd + 1, type);
            
            if (pos > len)
            {
                throw new IndexOutOfRangeException("BSON element exceeds document length");
            }
        }

        return new BlittableBsonDocument(pBuffer, len, index);
    }

    public static BlittableBsonConstants.BsonType GetElementType(BlittableBsonDocument doc, int offset)
    {
        return (BlittableBsonConstants.BsonType)doc.Pointer[offset];
    }

    public static int SkipElement(byte* ptr, int dataPos, BlittableBsonConstants.BsonType type) =>
        type switch
        {
            BlittableBsonConstants.BsonType.Double => dataPos + 8,
            BlittableBsonConstants.BsonType.String => dataPos + 4 + *(int*)(ptr + dataPos),
            BlittableBsonConstants.BsonType.Document => dataPos + *(int*)(ptr + dataPos),
            BlittableBsonConstants.BsonType.Array => dataPos + *(int*)(ptr + dataPos),
            BlittableBsonConstants.BsonType.Binary => dataPos + 4 + 1 + *(int*)(ptr + dataPos),
            BlittableBsonConstants.BsonType.ObjectId => dataPos + 12,
            BlittableBsonConstants.BsonType.Boolean => dataPos + 1,
            BlittableBsonConstants.BsonType.DateTime => dataPos + 8,
            BlittableBsonConstants.BsonType.Null => dataPos,
            BlittableBsonConstants.BsonType.Int32 => dataPos + 4,
            BlittableBsonConstants.BsonType.Int64 => dataPos + 8,
            BlittableBsonConstants.BsonType.Decimal128 => dataPos + 16,
            (BlittableBsonConstants.BsonType)14 => dataPos + 4 + *(int*)(ptr + dataPos), // Symbol
            (BlittableBsonConstants.BsonType)15 => dataPos + *(int*)(ptr + dataPos), // CodeWithScope
            (BlittableBsonConstants.BsonType)17 => dataPos + 8, // Timestamp
            (BlittableBsonConstants.BsonType)255 => dataPos, // MinKey
            (BlittableBsonConstants.BsonType)127 => dataPos, // MaxKey
            _ => throw new NotSupportedException($"BSON type {type} ({(int)type}) not yet supported in scanner")
        };
}
