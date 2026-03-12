using MongoSpyglass.Proxy.WireProtocol;
using MongoSpyglass.Proxy.WireProtocol.Raw;
using MongoSpyglass.Proxy.WireProtocol.Raw.Loaders;
using Simple.Arena;

namespace MongoSpyglass.Proxy.Tests
{
    public class SerializationTests
    {
        [Fact]
        public void OpMsg_ToBytes_preserves_payload()
        {
            using var allocator = new GrowableArena();
            var data = allocator.Allocate<byte>(5);
            data[0] = 0x11;
            data[1] = 0x22;
            data[2] = 0x33;
            data[3] = 0x44;
            data[4] = 0x55;

            var opMsg = new OpMsg
            {
                Flags = FlagBits.ChecksumPresent,
                Kind = 0,
                DataSection = data
            };

            var bytes = opMsg.ToBytes(allocator);

            Assert.Equal(10, bytes.Length);
            Assert.True(BitConverter.ToUInt32(bytes[..4]) == (uint)FlagBits.ChecksumPresent);
            Assert.Equal((byte)0, bytes[4]);
            Assert.Equal(new byte[] { 0x11, 0x22, 0x33, 0x44, 0x55 }, bytes[5..].ToArray());
        }

        [Fact]
        public void OpQuery_ToBytes_roundtrips_with_loader()
        {
            using var allocator = new GrowableArena();
            var query = new byte[] { 5, 0, 0, 0, 0 };
            var selector = new byte[] { 5, 0, 0, 0, 0 };

            var opQuery = new OpQuery
            {
                Flags = OperationFlags.SlaveOk,
                FullCollectionName = "admin.$cmd".AsSpan(),
                NumberToSkip = 0,
                NumberToReturn = -1,
                Query = query,
                ReturnFieldsSelector = selector
            };

            var bytes = opQuery.ToBytes(allocator);

            using var stream = new MemoryStream(bytes.ToArray());
            var parsed = OpQueryLoader.Instance.Load(stream, allocator);

            Assert.Equal(OperationFlags.SlaveOk, parsed.Flags);
            Assert.Equal("admin.$cmd", parsed.FullCollectionName.AsString());
            Assert.Equal(0, parsed.NumberToSkip);
            Assert.Equal(-1, parsed.NumberToReturn);
            Assert.Equal(query, parsed.Query.ToArray());
            Assert.Equal(selector, parsed.ReturnFieldsSelector.ToArray());
        }
    }
}
