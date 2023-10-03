using MongoSpyglass.Proxy.WireProtocol;

namespace MongoSpyglass.Proxy.Tests;

public class StreamExtensionsTests
{
    [Fact]
    public void TryRead_Int32_Success()
    {
        int expected = 42;
        byte[] buffer = BitConverter.GetBytes(expected);

        using MemoryStream ms = new MemoryStream(buffer);
        Assert.True(ms.TryRead(out int actual));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void TryRead_Long_Success()
    {
        long expected = 42L;
        byte[] buffer = BitConverter.GetBytes(expected);

        using MemoryStream ms = new MemoryStream(buffer);
        Assert.True(ms.TryRead(out long actual));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void TryRead_InsufficientData_Fails()
    {
        // Only 2 bytes, but we're trying to read an int (4 bytes)
        byte[] buffer = new byte[] { 0x1, 0x2 };

        using MemoryStream ms = new MemoryStream(buffer);
        Assert.False(ms.TryRead(out int _));
    }

    [Fact]
    public void TryRead_EmptyStream_Fails()
    {
        // No bytes to read
        using MemoryStream ms = new MemoryStream();
        Assert.False(ms.TryRead(out int _));
    }

    private class NonReadableStream : MemoryStream
    {
        public override bool CanRead => false;
    }

    [Fact]
    public void TryRead_StreamCannotRead_Fails()
    {
        using var nonReadableStream = new NonReadableStream();
        Assert.False(nonReadableStream.TryRead(out int _));
    }
}