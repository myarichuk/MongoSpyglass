namespace MongoSpyglass.Proxy.WireProtocol.Raw.Loaders;

internal class MsgHeaderLoader : MsgHeaderLoaderBase<Stream>
{
    public static MsgHeaderLoader Instance { get; } = new();

    public override int LoadMessageLength(Stream source, GrowableArena allocator)
    {
        if (!source.TryRead<int>(out var fetchedValue))
        {
            throw new InvalidOperationException("Unable to read message length");
        }

        return fetchedValue;
    }

    public override OpCode LoadOpCode(Stream source, GrowableArena allocator)
    {
        if (!source.TryReadEnum<OpCode>(out var opCode))
        {
            throw new InvalidOperationException("Unable to read op-code");
        }

        return opCode;
    }

    public override int LoadRequestID(Stream source, GrowableArena allocator)
    {
        if (!source.TryRead<int>(out var fetchedValue))
        {
            throw new InvalidOperationException("Unable to read request Id");
        }

        return fetchedValue;
    }

    public override int LoadResponseTo(Stream source, GrowableArena allocator)
    {
        if (!source.TryRead<int>(out var fetchedValue))
        {
            throw new InvalidOperationException("Unable to read responseTo value");
        }

        return fetchedValue;
    }
}
