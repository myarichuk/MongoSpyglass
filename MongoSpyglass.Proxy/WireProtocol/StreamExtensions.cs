using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.IO;
// ReSharper disable ComplexConditionExpression

namespace MongoSpyglass.Proxy.WireProtocol
{
    public static class StreamExtensions
    {
        private static readonly RecyclableMemoryStreamManager MemoryStreamManager = new();

        public static bool TryRead<TValue>(this Stream stream, out TValue value) where TValue : unmanaged
        {
            value = default;

            if (!stream.CanRead)
            {
                return false;
            }

            var sizeOfT = Unsafe.SizeOf<TValue>();
            Span<byte> buffer = stackalloc byte[sizeOfT];
            var bytesRead = 0;
            var offset = 0;

            try
            {
                // handle partial reads
                while (bytesRead < sizeOfT)
                {
                    var read = stream.Read(buffer.Slice(offset));
                    if (read == 0)
                    {
                        return false; // EOF
                    }
                    bytesRead += read;
                    offset += read;
                }
            }
            catch
            {
                return false;
            }
            
            value = Unsafe.ReadUnaligned<TValue>(ref MemoryMarshal.GetReference(buffer));
            return true;
        }

        //assuming utf-8 encoding in the "native" string in the stream
        public static bool TryReadNativeStringFromStream(
            this Stream stream, 
            GrowableArena memoryAllocator, 
            out Span<char> stringValue)
        {
            stringValue = default;
            int totalBytesRead = 0;
            
            var byteBuffer = memoryAllocator.Allocate<byte>(256);

            int readByte;
            while ((readByte = stream.ReadByte()) != -1 && readByte != 0)
            {
                if (totalBytesRead >= byteBuffer.Length)
                {
                    // double the buffer size
                    var newBuffer = memoryAllocator.Allocate<byte>(byteBuffer.Length * 2);
                    byteBuffer.CopyTo(newBuffer);
                    byteBuffer = newBuffer;
                }
                byteBuffer[totalBytesRead++] = (byte)readByte;
            }

            if (readByte == -1)
            {
                // TODO: properly handle EOF before null-terminated character.
                return false;
            }

            var actualByteBuffer = byteBuffer.Slice(0, totalBytesRead);
            var converter = new ByteUtf16CharConverter(memoryAllocator);
            converter.ConvertToUtf16(actualByteBuffer, out stringValue);

            return true;
        }

    }
}
