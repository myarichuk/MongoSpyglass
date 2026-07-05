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
            return default;
        }

        try
        {
            int docLen = *(int*)pBuffer;
            if (docLen < 5 || docLen > len)
            {
                return default;
            }

            var index = new ArenaDictionary<ArenaUtf8String, int>(arena);
            var keyCache = new ArenaDictionary<ArenaUtf8String, ArenaUtf8String>(arena);

            int pos = 4; // length header
            while (pos < docLen - 1)
            {
                var type = (BlittableBsonConstants.BsonType)pBuffer[pos];
                int nameStart = pos + 1;
                int nameEnd = nameStart;
                while (nameEnd < docLen && pBuffer[nameEnd] != 0) nameEnd++;
                
                if (nameEnd >= docLen)
                {
                    return default; // Malformed
                }

                var nameSpan = new ReadOnlySpan<byte>(pBuffer + nameStart, nameEnd - nameStart);
                var clonedNameSpan = ArenaUtf8String.Clone(nameSpan, arena);

                if (!keyCache.TryGetValue(clonedNameSpan, out var name))
                {
                    name = clonedNameSpan;
                    keyCache.Add(name, name);
                }

                index.Add(name, pos); // offset of the element (including type)
                if (!TrySkipElement(pBuffer, nameEnd + 1, type, docLen, out pos))
                {
                    return default; // Out of bounds or invalid
                }
            }

            return new BlittableBsonDocument(pBuffer, docLen, index);
        }
        catch
        {
            return default;
        }
    }

    public static BlittableBsonConstants.BsonType GetElementType(BlittableBsonDocument doc, int offset)
    {
        return (BlittableBsonConstants.BsonType)doc.Pointer[offset];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TrySafeReadInt32(byte* ptr, int offset, int totalLen, out int value)
    {
        if (offset + 4 > totalLen)
        {
            value = 0;
            return false;
        }
        value = *(int*)(ptr + offset);
        return true;
    }

    /// <summary>
    /// Skips over a BSON element. Returns the new position or false on invalid/truncated data.
    /// This version is hardened against malicious or truncated documents.
    /// </summary>
    public static bool TrySkipElement(byte* ptr, int dataPos, BlittableBsonConstants.BsonType type, int totalLen, out int newPos)
    {
        return TrySkipElementInternal(ptr, dataPos, type, totalLen, out newPos);
    }

    /// <summary>
    /// Backward-compat wrapper: returns position or -1 on error.
    /// </summary>
    public static int SkipElement(byte* ptr, int dataPos, BlittableBsonConstants.BsonType type, int totalLen)
    {
        return TrySkipElementInternal(ptr, dataPos, type, totalLen, out var newPos) ? newPos : -1;
    }

    private static bool TrySkipElementInternal(byte* ptr, int dataPos, BlittableBsonConstants.BsonType type, int totalLen, out int newPos)
    {
        if (dataPos >= totalLen)
        {
            newPos = 0;
            return false;
        }

        switch (type)
        {
            case BlittableBsonConstants.BsonType.Double:
                newPos = dataPos + 8;
                break;
            case BlittableBsonConstants.BsonType.String:
            case (BlittableBsonConstants.BsonType)14: // Symbol
                if (!TrySafeReadInt32(ptr, dataPos, totalLen, out int strLen) || strLen < 0)
                {
                    newPos = 0;
                    return false;
                }
                newPos = dataPos + 4 + strLen;
                break;
            case BlittableBsonConstants.BsonType.Document:
            case BlittableBsonConstants.BsonType.Array:
            case BlittableBsonConstants.BsonType.CodeWithScope:
                if (!TrySafeReadInt32(ptr, dataPos, totalLen, out int len) || len < 0)
                {
                    newPos = 0;
                    return false;
                }
                newPos = dataPos + len;
                break;
            case BlittableBsonConstants.BsonType.Binary:
                if (!TrySafeReadInt32(ptr, dataPos, totalLen, out int binLen) || binLen < 0)
                {
                    newPos = 0;
                    return false;
                }
                newPos = dataPos + 4 + 1 + binLen;
                break;
            case BlittableBsonConstants.BsonType.ObjectId:
                newPos = dataPos + 12;
                break;
            case BlittableBsonConstants.BsonType.Boolean:
                newPos = dataPos + 1;
                break;
            case BlittableBsonConstants.BsonType.DateTime:
                newPos = dataPos + 8;
                break;
            case BlittableBsonConstants.BsonType.Null:
                newPos = dataPos;
                break;
            case BlittableBsonConstants.BsonType.Int32:
                newPos = dataPos + 4;
                break;
            case BlittableBsonConstants.BsonType.Int64:
                newPos = dataPos + 8;
                break;
            case BlittableBsonConstants.BsonType.Decimal128:
                newPos = dataPos + 16;
                break;
            case BlittableBsonConstants.BsonType.Timestamp:
                newPos = dataPos + 8;
                break;
            case BlittableBsonConstants.BsonType.MinKey:
            case BlittableBsonConstants.BsonType.MaxKey:
                newPos = dataPos;
                break;
            default:
                newPos = 0;
                return false; // Unsupported or invalid type
        }

        // Validate the computed position
        if (newPos < 0 || newPos > totalLen)
        {
            newPos = 0;
            return false;
        }

        return true;
    }
}
