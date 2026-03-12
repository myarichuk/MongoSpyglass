using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;
using MongoDB.Bson;
using System.Runtime.InteropServices;
using Simple.Arena;

namespace MongoSpyglass.Proxy.WireProtocol
{
    public static class StructExtensions
    {
        public static unsafe Span<byte> AsBytes<T>(this T value, GrowableArena allocator) where T : unmanaged
        {
            var outputValue = allocator.Allocate<byte>(sizeof(T));
            var pValue = new Span<byte>((byte*)&value, sizeof(T)); //since T is unmanaged, no need for fixed

            pValue.CopyTo(outputValue);

            return outputValue;
        }

        public static string AsString(this Span<char> data) =>
            new(data);

        public static unsafe BsonDocument AsBson(this Span<byte> data)
        {              
            if(data.Length == 0)
            {
                return BsonDocument.Parse("{}");
            }

            using var memoryStream = new UnmanagedMemoryStream(
                (byte*)data.ToIntPtr().ToPointer(), 
                data.Length);

            using var reader = new BsonBinaryReader(memoryStream);

            return BsonSerializer.Deserialize<BsonDocument>(reader);
        }
    }
}
