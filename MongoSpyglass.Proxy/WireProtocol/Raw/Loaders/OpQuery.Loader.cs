namespace MongoSpyglass.Proxy.WireProtocol.Raw.Loaders;

internal class OpQueryLoader : OpQueryLoaderBase<Stream>
{
    public static OpQueryLoader Instance { get; } = new();

    public override OperationFlags LoadFlags(Stream source, GrowableArena allocator)
    {
        if (!source.TryReadEnum<OperationFlags>(out var flags))
        {
            throw new InvalidOperationException("Unable to read operation flags");
        }

        return flags;
    }

    public override Span<char> LoadFullCollectionName(Stream source, GrowableArena allocator)
    {
        if (!source.TryReadNativeStringFromStream(allocator, out var collectionName))
        {
            throw new InvalidOperationException("Unable to read full collection name");
        }

        return collectionName;
    }

    public override int LoadNumberToReturn(Stream source, GrowableArena allocator)
    {
        if (!source.TryRead<int>(out var fetchedValue))
        {
            throw new InvalidOperationException("Unable to read 'number to return'");
        }

        return fetchedValue;
    }

    public override int LoadNumberToSkip(Stream source, GrowableArena allocator)
    {
        if (!source.TryRead<int>(out var fetchedValue))
        {
            throw new InvalidOperationException("Unable to read 'number to skip'");
        }

        return fetchedValue;
    }

    public override Span<byte> LoadQuery(Stream source, GrowableArena allocator)
    {
        if (!source.TryReadBson(allocator, out var bsonAsBytes))
        {
            throw new InvalidOperationException("Unable to read query");
        }

        return bsonAsBytes;
    }

    public override Span<byte> LoadReturnFieldsSelector(Stream source, GrowableArena allocator)
    {
        if (!source.TryReadBson(allocator, out var bsonAsBytes))
        {
            return default; //optional field
        }

        return bsonAsBytes;        
    }
}
