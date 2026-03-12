using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Simple.Arena;
using MongoSpyglass.Proxy.WireProtocol;
using MongoSpyglass.Proxy.WireProtocol.Raw;

namespace MongoSpyglass.Proxy.Tests
{
    public class MongoProtocolTests
    {
        [Fact]
        public void TestTryReadEnum()
        {
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);
            writer.Write((int)OpCode.OP_MSG);
            ms.Position = 0;

            bool result = ms.TryReadEnum<OpCode>(out var opCode);
            Assert.True(result);
            Assert.Equal(OpCode.OP_MSG, opCode);
        }

        [Fact]
        public void TestTryReadHeaderFromStream()
        {
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);
            writer.Write((int)16); // length
            writer.Write((int)1); // request ID
            writer.Write((int)0); // response to
            writer.Write((int)OpCode.OP_MSG); // op code
            ms.Position = 0;

            var msgHeader = new MsgHeader();
            bool result = TryReadHeaderFromStream(ms, ref msgHeader);

            Assert.True(result);
            Assert.Equal(16, msgHeader.MessageLength);
            Assert.Equal(1, msgHeader.RequestID);
            Assert.Equal(0, msgHeader.ResponseTo);
            Assert.Equal(OpCode.OP_MSG, msgHeader.OpCode);
        }

        private static unsafe bool TryReadHeaderFromStream(Stream stream, ref MsgHeader header)
        {
            Span<byte> buffer = stackalloc byte[sizeof(MsgHeader)];
            var readBytes = stream.Read(buffer);
            if (readBytes != sizeof(MsgHeader)) return false;
            fixed (byte* pBuffer = &System.Runtime.InteropServices.MemoryMarshal.GetReference(buffer))
            {
                var pHeader = (MsgHeader*)pBuffer;
                header = *pHeader;
            }
            return true;
        }
    }
}
