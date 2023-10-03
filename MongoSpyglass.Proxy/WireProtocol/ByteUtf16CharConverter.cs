using System.Buffers;
using System.Text;

// ReSharper disable ComplexConditionExpression

namespace MongoSpyglass.Proxy.WireProtocol;

public readonly struct ByteUtf16CharConverter
{
    private readonly GrowableArena _memoryAllocator;

    public ByteUtf16CharConverter(GrowableArena memoryAllocator) => 
        _memoryAllocator = memoryAllocator;

    public int ConvertToUtf16(Span<byte> source, out Span<char> destination)
    {
        int srcIndex = 0;
        int destIndex = 0;

        // count the number of UTF-16 code units needed
        int totalCharCount = 0;

        while (srcIndex < source.Length)
        {
            if (Rune.DecodeFromUtf8(source.Slice(srcIndex), out Rune rune, out int bytesConsumed) == OperationStatus.Done)
            {
                srcIndex += bytesConsumed;
                totalCharCount += rune.IsAscii || rune.Value <= 0xFFFF ? 1 : 2; // 2 for surrogate pairs
            }
            else
            {
                // ignore invalid UTF-16 data
                srcIndex++;
            }
        }

        destination = _memoryAllocator.Allocate<char>(totalCharCount);

        // now do the actual conversion
        srcIndex = 0;

        while (srcIndex < source.Length)
        {
            if (Rune.DecodeFromUtf8(source.Slice(srcIndex), out Rune rune, out int bytesConsumed) == OperationStatus.Done)
            {
                srcIndex += bytesConsumed;
                destIndex += RuneToChars(rune, destination.Slice(destIndex));
            }
            else
            {
                // ignore invalid UTF-16 data
                srcIndex++;
            }
        }

        return destIndex;
    }

    private static int RuneToChars(Rune rune, Span<char> destination)
    {
        /*
         This handles (both are <= 16 bit)
          - any ascii character
          - any character from the Basic Multilingual Plane
            (pretty much any unicode character up to U+FFFF)
        */
        if (rune.IsAscii || rune.Value <= 0xFFFF)
        {
            destination[0] = (char)rune.Value;
            return 1;
        }

        //this handles Supplementary Planes (any unicode character above U+FFFF, also "multibyte")
        //can only be represented by 32 bit (called "surrogate pair")
        destination[0] = (char)(((rune.Value - 0x010000) >> 10) + 0xD800); //get high surrogate
        destination[1] = (char)((rune.Value - 0x010000) % 0x0400 + 0xDC00); //get low surrogate
        
        return 2;
    }
}