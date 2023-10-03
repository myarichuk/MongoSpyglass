using Simple.Arena;

namespace MongoSpyglass.Proxy.WireProtocol
{
    internal interface IMessageParser
    {
    }

    internal interface IMessageParser<TMongoMessage>: IMessageParser
        where TMongoMessage: unmanaged
    {
        bool TryParse(ref MsgHeader header, Stream source, Arena memoryAllocator, out TMongoMessage message);

        Span<byte> GetRawBytes(TMongoMessage message, Arena memoryAllocator);
    }
}
