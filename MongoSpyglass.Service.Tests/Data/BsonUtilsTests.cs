using MongoDB.Bson;
using Xunit;
using System;
// explicitly use MongoSpyglass.Service.Data.BsonUtils

namespace MongoSpyglass.Service.Tests.Data
{
    public class BsonUtilsTests
    {
        [Fact]
        public void ParseOpMsg_WithShortArray_ReturnsEmptyDocument()
        {
            // Arrange
            byte[] shortArray0 = Array.Empty<byte>();
            byte[] shortArray1 = new byte[] { 0 };
            byte[] shortArray2 = new byte[] { 0, 1 };
            byte[] shortArray3 = new byte[] { 0, 1, 2 };

            // Act
            BsonDocument result0 = MongoSpyglass.Service.Data.BsonUtils.ParseOpMsg(shortArray0);
            BsonDocument result1 = MongoSpyglass.Service.Data.BsonUtils.ParseOpMsg(shortArray1);
            BsonDocument result2 = MongoSpyglass.Service.Data.BsonUtils.ParseOpMsg(shortArray2);
            BsonDocument result3 = MongoSpyglass.Service.Data.BsonUtils.ParseOpMsg(shortArray3);

            // Assert
            Assert.NotNull(result0);
            Assert.Empty(result0.Elements);

            Assert.NotNull(result1);
            Assert.Empty(result1.Elements);

            Assert.NotNull(result2);
            Assert.Empty(result2.Elements);

            Assert.NotNull(result3);
            Assert.Empty(result3.Elements);
        }
    }
}
