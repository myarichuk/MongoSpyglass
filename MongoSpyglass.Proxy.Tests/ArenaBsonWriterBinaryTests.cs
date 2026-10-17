using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoSpyglass.Proxy.Bson;
using SharpArena.Allocators;

namespace MongoSpyglass.Proxy.Tests;

public class ArenaBsonWriterBinaryTests
{
    [Theory]
    [InlineData(BsonBinarySubType.Binary)]
    [InlineData(BsonBinarySubType.UuidLegacy)]
    [InlineData(BsonBinarySubType.UuidStandard)]
    [InlineData(BsonBinarySubType.MD5)]
    public void WriteBsonValue_Binary_RoundTripsBytesAndSubtype(BsonBinarySubType subType)
    {
        var bytes = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();
        using var arena = new ArenaAllocator();

        var writer = new ArenaBsonWriter(arena);
        writer.WriteStartDocument();
        writer.WriteBsonValue("v", new BsonBinaryData(bytes, subType));
        writer.WriteEndDocument();
        var doc = writer.Commit(arena);

        var roundTripped = BsonSerializer.Deserialize<BsonDocument>(doc.AsReadOnlySpan().ToArray())["v"].AsBsonBinaryData;
        Assert.Equal(subType, roundTripped.SubType);
        Assert.Equal(bytes, roundTripped.Bytes);
    }
}
