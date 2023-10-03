using MongoSpyglass.Proxy.WireProtocol;
// or whatever namespace GrowableArena is in

namespace MongoSpyglass.Proxy.Tests;

public class ReadNativeStringFromStreamTests
{

    [Fact]
    public void TestReadValidNativeString()
    {
        using GrowableArena memoryAllocator = new();
        var testStream = new MemoryStream("hello\0world\0"u8.ToArray());
        var result = testStream.TryReadNativeStringFromStream(memoryAllocator, out var stringValue);

        Assert.True(result);
        Assert.Equal("hello", new string(stringValue));
    }

    [Fact]
    public void TestReadEmptyNativeString()
    {
        using GrowableArena memoryAllocator = new();
        var testStream = new MemoryStream("\0"u8.ToArray());
        var result = testStream.TryReadNativeStringFromStream(memoryAllocator, out var stringValue);

        Assert.True(result);
        Assert.Equal(string.Empty, new string(stringValue));
    }

    [Fact]
    public void TestReadInvalidUTF8()
    {
        using GrowableArena memoryAllocator = new();
        var testStream = new MemoryStream(new byte[] { 0xC3, 0x28, 0x0 });  // invalid UTF8 bytes
        var result = testStream.TryReadNativeStringFromStream(memoryAllocator, out var stringValue);

        // This behavior depends on how your converter handles invalid UTF-8 sequences.
        // Assuming you're not throwing exceptions on invalid UTF-8...
        Assert.Equal("(", new string(stringValue));
        Assert.True(result);
    }

    [Fact]
    public void TestReadEOF()
    {
        using GrowableArena memoryAllocator = new();
        var testStream = new MemoryStream("ab"u8.ToArray());  // no null terminator
        var result = testStream.TryReadNativeStringFromStream(memoryAllocator, out var stringValue);

        Assert.False(result);
        Assert.Equal(0, stringValue.Length);
    }
}