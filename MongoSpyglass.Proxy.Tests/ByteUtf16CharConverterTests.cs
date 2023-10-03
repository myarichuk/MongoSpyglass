using System;
using System.Text;
using MongoSpyglass.Proxy;
using MongoSpyglass.Proxy.WireProtocol;
using Xunit;

namespace MongoSpyglass.Proxy.Tests
{
    public class ByteUtf16CharConverterTests : IDisposable
    {
        private readonly GrowableArena _allocator = new();

        [Fact]
        public void TestAsciiConversion()
        {
            Span<byte> asciiBytes = "Hello"u8.ToArray();
            var converter = new ByteUtf16CharConverter(_allocator);

            int written = converter.ConvertToUtf16(asciiBytes, out Span<char> result);

            Assert.Equal("Hello", new string(result.Slice(0, written)));
        }

        //note: BPM (Basic Multilingual Plane) - any non-ascii unicode character up to U+FFFF
        [Fact]
        public void TestBpmUtf8Conversion()
        {
            Span<byte> multiBytes = "こんにちは"u8.ToArray(); //"hello" in Japanese :)
            var converter = new ByteUtf16CharConverter(_allocator);

            int written = converter.ConvertToUtf16(multiBytes, out Span<char> result);

            Assert.Equal("こんにちは", new string(result.Slice(0, written)));
        }

        [Fact]
        public void TestInvalidUtf8Sequence()
        {
            // "H" + "e" + "l" + "l" + "o" + 0xC3 (invalid standalone byte) + "W" + "o" + "r" + "l" + "d"
            Span<byte> invalidBytes = new byte[] { 72, 101, 108, 108, 111, 0xC3, 87, 111, 114, 108, 100 };
            var converter = new ByteUtf16CharConverter(_allocator);

            int written = converter.ConvertToUtf16(invalidBytes, out Span<char> result);

            // Expecting the invalid byte to be skipped
            Assert.Equal("HelloWorld", new string(result.Slice(0, written)));
        }

        [Fact]
        public void TestMultibyteUtf8Conversion()
        {
            // Unicode emojis have code points that fall outside of BMP
            Span<byte> multiBytes = "🙂🙃"u8.ToArray(); // UTF-8 encoded smiley and upside-down smiley
            var converter = new ByteUtf16CharConverter(_allocator);

            int written = converter.ConvertToUtf16(multiBytes, out Span<char> result);

            Assert.Equal("🙂🙃", new string(result.Slice(0, written)));
        }


        public void Dispose() => _allocator.Dispose();
    }
}