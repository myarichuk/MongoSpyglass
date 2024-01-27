using System.Runtime.InteropServices;
using System.Text;
using Microsoft.IO;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;
using Simple.Arena;

namespace MongoSpyglass.Proxy;

internal unsafe class Utils
{
    private static readonly RecyclableMemoryStreamManager RecyclableMemoryStreamManager = new();

    public static BsonDocument DeserializeBsonFromStream(Stream stream)
    {
        using var bsonReader = new BsonBinaryReader(stream);
        var document = BsonSerializer.Deserialize<BsonDocument>(bsonReader);
        return document;
    }

    public static int ReadInt32FromStream(Stream stream)
    {
        Span<byte> buffer = stackalloc byte[4];
        stream.Read(buffer);

        return BitConverter.ToInt32(buffer);
    }

    public static Span<byte> Int32AsBytes(int num)
    {
        var numberSpan = MemoryMarshal.CreateSpan(ref num, 1);
        return MemoryMarshal.AsBytes(numberSpan);
    }

    public static string PtrToStringUtf8(byte* ptr, int length) => Encoding.UTF8.GetString(ptr, length);

    public static Span<byte> ReadCStringFromStream(Stream stream, Arena memoryAllocator)
    {
        using var bufferStream = RecyclableMemoryStreamManager.GetStream();

        int readByte;
        int stringSize = 0;
        while ((readByte = stream.ReadByte()) != 0)
        {
            bufferStream.WriteByte((byte)readByte);
            stringSize++;
        }

        var stringBytes = memoryAllocator.Allocate<byte>(stringSize);

        var bytes = bufferStream.GetBuffer();
        for (var index = 0; index < bufferStream.Length; index++)
        {
            stringBytes[index] = bytes[index];
        }

        return stringBytes;
    }

    public static Span<byte> ReadDocumentFromStream(Stream stream, Arena memoryAllocator)
    {
        // Read the length of the BSON document (first 4 bytes)
        byte[] lengthBytes = new byte[4]; //error handling, take into account max length and such...
        stream.Read(lengthBytes, 0, 4);

        // Convert the 4-byte length field to an int
        int length = BitConverter.ToInt32(lengthBytes, 0);

        var documentBytes = memoryAllocator.Allocate<byte>(length);
            
        for (int index = 0; index < 4; index++)
        {
            documentBytes[index] = lengthBytes[index];
        }


        // Read the rest of the BSON document
        stream.Read(documentBytes.Slice(4));

        return documentBytes;
    }


}