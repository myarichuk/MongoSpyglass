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
                int seqSize = BinaryPrimitives.ReadInt32LittleEndian(memory.Span.Slice(pos));
                int seqEnd = pos + seqSize;
                pos += 4;
                
                // Read identifier
                int identStart = pos;
                while (pos < seqEnd && memory.Span[pos] != 0) pos++;
                string identifier = System.Text.Encoding.UTF8.GetString(memory.Span.Slice(identStart, pos - identStart).ToArray());
                pos++; // null
                
                var array = new BsonArray();
                while (pos < seqEnd)
                {
                    int docLen = BinaryPrimitives.ReadInt32LittleEndian(memory.Span.Slice(pos));
                    array.Add(BsonSerializer.Deserialize<BsonDocument>(memory.Slice(pos, docLen).ToArray()));
                    pos += docLen;
                }
                result[identifier] = array;
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
                doc = BsonSerializer.Deserialize<BsonDocument>(bytes);
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
            return BsonSerializer.Deserialize<BsonDocument>(bytes);
        }
        catch { return "Error parsing BSON"; }
    }
}
