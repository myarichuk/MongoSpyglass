using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

// ReSharper disable ComplexConditionExpression

namespace MongoSpyglass.Proxy.WireProtocol
{
    public static class StreamExtensions
    {
        public static bool TryReadEnum<TEnum, TEnumType>(this Stream stream, out TEnum enumValue) 
            where TEnum : struct
            where TEnumType: unmanaged
        {
            enumValue = default;

            if(!stream.TryRead<TEnumType>(out var fetchedValue) || 
               !Enum.TryParse<TEnum>(fetchedValue.ToString(), out var parsedEnumValue))
            {                
                return false;
            }
            
            enumValue = parsedEnumValue;
            return true;
        }

        public static bool TryReadEnum<TEnum>(this Stream stream, out TEnum enumValue) 
            where TEnum : struct
        {
            enumValue = default;

            if(!stream.TryRead<int>(out var fetchedValue) || 
               !Enum.TryParse<TEnum>(fetchedValue.ToString(), out var parsedEnumValue))
            {                
                return false;
            }
            
            enumValue = parsedEnumValue;
            return true;
        }


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

        public static unsafe bool TryReadBson(this Stream stream, GrowableArena allocator, out Span<byte> bsonAsBytes)
        {
            bsonAsBytes = default;
            
            //read the length of the BSON document (first 4 bytes)
            if(!stream.TryRead<int>(out var length) || length == 0)
            {
                return false;
            }

            bsonAsBytes = allocator.Allocate<byte>(length);            
            var lengthBytes = length.AsBytes(allocator);

            //first push the length into the document bytes
            for (int index = 0; index < 4; index++)
            {
                bsonAsBytes[index] = lengthBytes[index];
            }

            //read the rest of the BSON document
            try
            {
                stream.ReadExactly(bsonAsBytes[4..]);
            }
            catch (EndOfStreamException)
            {
                return false;
            }

            return true;
        }
    }
}
