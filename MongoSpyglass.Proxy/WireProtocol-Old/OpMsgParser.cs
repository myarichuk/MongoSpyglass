using Simple.Arena;

namespace MongoSpyglass.Proxy.WireProtocol
{
    internal class OpMsgParser: IMessageParser<OpMsg>
    {
        public bool TryParse(ref MsgHeader header, Stream source, Arena memoryAllocator, out OpMsg message)
        {
            throw new NotImplementedException();
        }

        public Span<byte> GetRawBytes(OpMsg message, Arena memoryAllocator)
        {
            throw new NotImplementedException();
        }
    }
}
