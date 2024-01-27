using MongoSpyglass.Proxy.WireProtocol.Raw;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MongoSpyglass.Proxy.Tests
{

    public class OpQueryTests
    {
        private readonly GrowableArena _allocator = new GrowableArena();

        [Fact]
        public unsafe void TestFromBytesToBytes()
        {
            var dummyString = "someCollectionName".AsSpan();
            var expectedOpQuery = new OpQuery
            {
                Flags = WireProtocol.OperationFlags.SlaveOk,
                FullCollectionName = new Span<char>(Unsafe.AsPointer(ref MemoryMarshal.GetReference(dummyString)), dummyString.Length),
                NumberToSkip = 5,
                NumberToReturn = 10,
                Query = new Span<byte>(new byte[] { 1, 2, 3 }),
                ReturnFieldsSelector = new Span<byte>(new byte[] { 4, 5, 6 })
            };

            var bytes = expectedOpQuery.ToBytes(_allocator);

            var actualOpQuery = OpQuery.FromBytes(bytes);

            Assert.Equal(expectedOpQuery.Flags, actualOpQuery.Flags);
            Assert.True(expectedOpQuery.FullCollectionName.SequenceEqual(actualOpQuery.FullCollectionName));
            Assert.Equal(expectedOpQuery.NumberToSkip, actualOpQuery.NumberToSkip);
            Assert.Equal(expectedOpQuery.NumberToReturn, actualOpQuery.NumberToReturn);
            Assert.True(expectedOpQuery.Query.SequenceEqual(actualOpQuery.Query));
            Assert.True(expectedOpQuery.ReturnFieldsSelector.SequenceEqual(actualOpQuery.ReturnFieldsSelector));
        }
    }
}
