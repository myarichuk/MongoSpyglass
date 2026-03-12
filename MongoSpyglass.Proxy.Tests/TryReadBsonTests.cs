using System;
using System.IO;
using Xunit;
using Simple.Arena;
using MongoSpyglass.Proxy.WireProtocol;

namespace MongoSpyglass.Proxy.Tests
{
    public class TryReadBsonTests
    {
        [Fact]
        public void TestTryReadBson_ReadsCorrectLength()
        {
            using var arena = new GrowableArena();
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);

            // Document length including length bytes = 8
            writer.Write((int)8);
            writer.Write((byte)1); // byte 1
            writer.Write((byte)2); // byte 2
            writer.Write((byte)3); // byte 3
            writer.Write((byte)0); // null terminator

            ms.Position = 0;

            bool result = ms.TryReadBson(arena, out var bsonAsBytes);
            Assert.True(result);
            Assert.Equal(8, bsonAsBytes.Length);
            Assert.Equal(8, BitConverter.ToInt32(bsonAsBytes.Slice(0, 4)));
            Assert.Equal(1, bsonAsBytes[4]);
            Assert.Equal(2, bsonAsBytes[5]);
            Assert.Equal(3, bsonAsBytes[6]);
            Assert.Equal(0, bsonAsBytes[7]);
        }

        [Fact]
        public void TestTryReadBson_WithPartialReads()
        {
            using var arena = new GrowableArena();

            var ms = new MemoryStream();
            var writer = new BinaryWriter(ms);
            writer.Write((int)10);
            writer.Write((byte)1);
            writer.Write((byte)2);
            writer.Write((byte)3);
            writer.Write((byte)4);
            writer.Write((byte)5);
            writer.Write((byte)0); // null terminator

            var partialStream = new PartialReadStream(ms.ToArray(), 2);

            bool result = partialStream.TryReadBson(arena, out var bsonAsBytes);
            Assert.True(result);
            Assert.Equal(10, bsonAsBytes.Length);
            Assert.Equal(10, BitConverter.ToInt32(bsonAsBytes.Slice(0, 4)));
            Assert.Equal(1, bsonAsBytes[4]);
            Assert.Equal(2, bsonAsBytes[5]);
            Assert.Equal(3, bsonAsBytes[6]);
            Assert.Equal(4, bsonAsBytes[7]);
            Assert.Equal(5, bsonAsBytes[8]);
            Assert.Equal(0, bsonAsBytes[9]);
        }
    }
}
