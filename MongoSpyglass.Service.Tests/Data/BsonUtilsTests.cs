using Xunit;
using MongoSpyglass.Service.Data;

namespace MongoSpyglass.Service.Tests.Data;

public class BsonUtilsTests
{
    [Fact]
    public void ToJson_MalformedOpMsg_ReturnsErrorJson()
    {
        // Arrange
        // An OP_MSG needs at least some structure to fail parsing BSON instead of just returning an empty document.
        // In ParseOpMsg, if length >= 4, it reads flagBits.
        // It then looks at kind. If kind is 0 (body), it tries to read docLen.
        // To trigger BsonSerializer.Deserialize exception, we can give it an invalid document length or invalid BSON.
        // A simple malformed byte array that triggers an exception during BSON parsing.

        // Let's construct a payload that gets to BsonSerializer.Deserialize and fails.
        // Flag bits = 0 (4 bytes)
        // Kind = 0 (1 byte)
        // DocLen = 10 (4 bytes)
        // Then some garbage data for the doc.
        var malformedBytes = new byte[]
        {
            0, 0, 0, 0, // flags
            0, // kind = body
            10, 0, 0, 0, // docLen = 10
            0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF // garbage document data, not valid BSON
        };

        // Act
        var result = BsonUtils.ToJson(malformedBytes, "OP_MSG", false);

        // Assert
        Assert.Equal("{ \"error\": \"failed to parse bson\" }", result);
    }

    [Fact]
    public void ToJson_MalformedLegacyOp_ReturnsErrorJson()
    {
        // Arrange
        // For OP_QUERY, it skips:
        // flags (4) + fullCollectionName (CString) + numberToSkip (4) + numberToReturn (4)
        var malformedBytes = new byte[]
        {
            0, 0, 0, 0, // flags
            (byte)'a', 0, // collection name "a" + null terminator
            0, 0, 0, 0, // skip
            0, 0, 0, 0, // return
            // Followed by BSON document. Let's make it invalid BSON so deserialization fails.
            10, 0, 0, 0, // docLen = 10
            0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF // garbage
        };

        // Act
        var result = BsonUtils.ToJson(malformedBytes, "OP_QUERY", false);

        // Assert
        Assert.Equal("{ \"error\": \"failed to parse bson\" }", result);
    }
}
