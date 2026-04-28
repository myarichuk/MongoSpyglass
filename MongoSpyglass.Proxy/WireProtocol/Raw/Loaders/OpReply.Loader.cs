using SharpArena.Allocators;

namespace MongoSpyglass.Proxy.WireProtocol.Raw.Loaders;

internal class OpReplyLoader : OpReplyLoaderBase<Stream>
{
    public static OpReplyLoader Instance { get; } = new();

    public override int LoadResponseFlags(Stream source, ArenaAllocator allocator)
    {
        if (!source.TryRead<int>(out var flags))
        {
            throw new InvalidOperationException("Unable to read response flags");
        }
        return flags;
    }

    public override long LoadCursorID(Stream source, ArenaAllocator allocator)
    {
        if (!source.TryRead<long>(out var cursorId))
        {
            throw new InvalidOperationException("Unable to read cursor ID");
        }
        return cursorId;
    }

    public override int LoadStartingFrom(Stream source, ArenaAllocator allocator)
    {
        if (!source.TryRead<int>(out var startingFrom))
        {
            throw new InvalidOperationException("Unable to read starting from");
        }
        return startingFrom;
    }

    public override int LoadNumberReturned(Stream source, ArenaAllocator allocator)
    {
        if (!source.TryRead<int>(out var numberReturned))
        {
            throw new InvalidOperationException("Unable to read number returned");
        }
        return numberReturned;
    }

    public override Span<byte> LoadDocuments(Stream source, ArenaAllocator allocator)
    {
        // OP_REPLY can contain multiple BSON documents. 
        // We'll read all remaining bytes in the stream as documents for now.
        // In a real implementation, we might want to count based on NumberReturned.
        
        var remaining = (int)(source.Length - source.Position);
        if (remaining <= 0) return Span<byte>.Empty;

        var buffer = allocator.Allocate<byte>(remaining);
        source.ReadExactly(buffer);
        return buffer;
    }
}
