using SharpArena.Allocators;
using SharpArena.Collections;
using MongoSpyglass.Proxy.WireProtocol.Raw.Parts;
using System.Buffers.Binary;

namespace MongoSpyglass.Proxy.WireProtocol.Raw.Loaders;

internal class OpMsgLoader
{
    public static OpMsgLoader Instance { get; } = new();

    public FlagBits LoadFlags(Stream source)
    {
        if (!source.TryReadEnum<FlagBits, uint>(out var flags))
        {
            throw new InvalidOperationException("Unable to read flags");
        }

        return flags;
    }

    public OpMsg Load(Stream source, ArenaAllocator allocator)
    {
        var item = new OpMsg();
        item.Flags = LoadFlags(source);
        item.Sections = new ArenaList<byte>(allocator, 1024);

        while (true)
        {
            if (source.Position >= source.Length - (item.Flags.HasFlag(FlagBits.ChecksumPresent) ? 4 : 0))
            {
                break;
            }

            int kind = source.ReadByte();
            if (kind == -1) break;

            item.Sections.Add((byte)kind);

            if (kind == 0)
            {
                if (!source.TryReadBson(allocator, out var bsonAsBytes))
                {
                    throw new InvalidOperationException("Unable to read Kind 0 section");
                }
                foreach(var b in bsonAsBytes) item.Sections.Add(b);
            }
            else if (kind == 1)
            {
                if (!source.TryRead<int>(out var size))
                {
                    throw new InvalidOperationException("Unable to read Kind 1 section size");
                }
                
                var sizeBytes = size.AsBytes(allocator);
                foreach(var b in sizeBytes) item.Sections.Add(b);

                var remainingSize = size - 4;
                var buffer = allocator.Allocate<byte>(remainingSize);
                source.ReadExactly(buffer);
                foreach(var b in buffer) item.Sections.Add(b);
            }
            else
            {
                throw new InvalidOperationException($"Unknown section kind: {kind}");
            }
        }

        if (item.Flags.HasFlag(FlagBits.ChecksumPresent))
        {
            if (source.TryRead<uint>(out var checksum))
            {
                item.Checksum = checksum;
            }
        }

        return item;
    }
}
