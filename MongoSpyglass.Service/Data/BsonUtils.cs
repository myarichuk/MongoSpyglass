using System.Buffers.Binary;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;

namespace MongoSpyglass.Service.Data;

public static class BsonUtils
{
    public static BsonDocument ParseOpMsg(byte[] bytes)
    {
        var result = new BsonDocument();
        ReadOnlyMemory<byte> memory = bytes;
        
        if (memory.Length < 4) return result;
        
        int flagBits = BinaryPrimitives.ReadInt32LittleEndian(memory.Span);
        bool checksumPresent = (flagBits & 1) != 0;
        int dataLen = memory.Length - (checksumPresent ? 4 : 0);
        
        int pos = 4;
        while (pos < dataLen)
        {
            byte kind = memory.Span[pos++];
            if (kind == 0) // Body
            {
                int docLen = BinaryPrimitives.ReadInt32LittleEndian(memory.Span.Slice(pos));
                var doc = BsonSerializer.Deserialize<BsonDocument>(memory.Slice(pos, docLen).ToArray());
                foreach (var el in doc) result[el.Name] = el.Value;
                pos += docLen;
            }
            else if (kind == 1) // Sequence
            {
                if (pos + 4 > dataLen) break;
                int seqSize = BinaryPrimitives.ReadInt32LittleEndian(memory.Span.Slice(pos));
                int seqEnd = pos + seqSize;
                if (seqEnd > dataLen) seqEnd = dataLen;
                pos += 4;
                
                // Read identifier
                int identStart = pos;
                while (pos < seqEnd && memory.Span[pos] != 0) pos++;
                if (pos >= seqEnd) break;

                string identifier = System.Text.Encoding.UTF8.GetString(memory.Span.Slice(identStart, pos - identStart).ToArray());
                pos++; // null
                
                var array = new BsonArray();
                while (pos + 4 <= seqEnd)
                {
                    int docLen = BinaryPrimitives.ReadInt32LittleEndian(memory.Span.Slice(pos));
                    if (pos + docLen > seqEnd) break;
                    array.Add(BsonSerializer.Deserialize<BsonDocument>(memory.Slice(pos, docLen).ToArray()));
                    pos += docLen;
                }
                result[identifier] = array;
                pos = seqEnd; // Ensure we advance to the end of the sequence
            }
            else break;
        }

        return result;
    }

    public static string ToJson(byte[]? bytes, string opCode, bool indent)
    {
        if (bytes == null || bytes.Length == 0) return "{}";
        try 
        {
            BsonDocument doc;
            if (opCode == "OP_MSG")
            {
                doc = ParseOpMsg(bytes);
            }
            else
            {
                doc = ParseLegacyOp(bytes, opCode);
            }
            return doc.ToJson(new JsonWriterSettings { Indent = indent });
        } 
        catch 
        {
            return "{ \"error\": \"failed to parse bson\" }";
        }
    }

    public static BsonValue ToBsonValue(byte[]? bytes, string opCode)
    {
        if (bytes == null || bytes.Length == 0) return BsonNull.Value;
        try 
        {
            if (opCode == "OP_MSG")
            {
                return ParseOpMsg(bytes);
            }
            return ParseLegacyOp(bytes, opCode);
        }
        catch { return "Error parsing BSON"; }
    }

    private static BsonDocument ParseLegacyOp(byte[] bytes, string opCode)
    {
        int offset = 0;
        ReadOnlySpan<byte> span = bytes;

        if (opCode == "OP_QUERY")
        {
            // flags (4) + fullCollectionName (CString) + numberToSkip (4) + numberToReturn (4)
            offset = 4;
            while (offset < span.Length && span[offset] != 0) offset++;
            offset++; // null terminator
            offset += 8; // skip/return
        }
        else if (opCode == "OP_REPLY")
        {
            // responseFlags (4) + cursorId (8) + startingFrom (4) + numberReturned (4)
            offset = 20;
        }
        else if (opCode == "OP_UPDATE")
        {
            // ZERO (4) + fullCollectionName (CString) + flags (4)
            offset = 4;
            while (offset < span.Length && span[offset] != 0) offset++;
            offset++;
            offset += 4;
        }
        else if (opCode == "OP_INSERT")
        {
            // flags (4) + fullCollectionName (CString)
            offset = 4;
            while (offset < span.Length && span[offset] != 0) offset++;
            offset++;
        }
        else if (opCode == "OP_DELETE")
        {
            // ZERO (4) + fullCollectionName (CString) + flags (4)
            offset = 4;
            while (offset < span.Length && span[offset] != 0) offset++;
            offset++;
            offset += 4;
        }

        if (offset >= span.Length) return new BsonDocument();

        // The BSON document follows the header
        return BsonSerializer.Deserialize<BsonDocument>(span.Slice(offset).ToArray());
    }
}
