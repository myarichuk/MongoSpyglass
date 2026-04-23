using System;
using System.Text;
using SharpArena.Allocators;
using MongoSpyglass.Proxy;

namespace MongoSpyglass.Proxy.WireProtocol;

public readonly struct ByteUtf16CharConverter
{
    private readonly ArenaAllocator _memoryAllocator;

    public ByteUtf16CharConverter(ArenaAllocator memoryAllocator) =>
        _memoryAllocator = memoryAllocator;

    public int ConvertToUtf16(Span<byte> source, out Span<char> destination)
    {
        // By using UTF8 decoder that drops invalid sequences. Wait, .NET Core provides drop fallback.
        // It's not worth allocating a Decoder object.
        // What we had previously with Rune was better for ignoring invalid sequences without allocations but had double loop.
        // Let's implement a quick single loop decoding or copy back the old implementation.
        // Let's copy back old Rune implementation but single loop. Wait, we don't know the exact length in advance,
        // but since totalCharCount <= source.Length, we can allocate `source.Length` initially.

        destination = _memoryAllocator.Allocate<char>(source.Length);

        int srcIndex = 0;
        int destIndex = 0;

        while (srcIndex < source.Length)
        {
            if (System.Text.Rune.DecodeFromUtf8(source.Slice(srcIndex), out System.Text.Rune rune, out int bytesConsumed) == System.Buffers.OperationStatus.Done)
            {
                srcIndex += bytesConsumed;
                if (rune.IsAscii || rune.Value <= 0xFFFF)
                {
                    destination[destIndex++] = (char)rune.Value;
                }
                else
                {
                    destination[destIndex++] = (char)(((rune.Value - 0x010000) >> 10) + 0xD800);
                    destination[destIndex++] = (char)((rune.Value - 0x010000) % 0x0400 + 0xDC00);
                }
            }
            else
            {
                // ignore invalid UTF-16 data
                srcIndex++;
            }
        }
        
        destination = destination.Slice(0, destIndex);
        return destIndex;
    }
}
