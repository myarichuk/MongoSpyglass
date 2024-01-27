using System.Runtime.InteropServices;
using MongoSpyglass.Proxy.WireProtocol;

namespace MongoSpyglass.Proxy.Tests;

public class StructExtensionsTests: IDisposable
{
    private readonly GrowableArena _allocator = new();

    [Fact]
    public void TestAsBytesForInt32()
    {
        int value = 42;
        var bytes = value.AsBytes(_allocator);
        Assert.Equal(value, MemoryMarshal.Read<int>(bytes));
    }


    [Fact]
    public void TestAsBytesForFloat()
    {
        float value = 3.14f;
        var bytes = value.AsBytes(_allocator);

        Assert.Equal(value, MemoryMarshal.Read<float>(bytes));
    }

    [Fact]
    public void TestAsBytesForDouble()
    {
        double value = 3.141592653589793;
        var bytes = value.AsBytes(_allocator);

        Assert.Equal(value, MemoryMarshal.Read<double>(bytes));
    }

    [Fact]
    public void TestAsBytesForChar()
    {
        char value = 'A';
        var bytes = value.AsBytes(_allocator);
        Assert.Equal(value, MemoryMarshal.Read<char>(bytes));
    }

    [Fact]
    public void TestAsBytesForStruct()
    {
        var value = new SampleStruct
        {
            IntField = 42,
            FloatField = 3.14f
        };
            
        var bytes = value.AsBytes(_allocator);

        Assert.Equal(Marshal.SizeOf<SampleStruct>(), bytes.Length);
        Assert.Equal(value, MemoryMarshal.Read<SampleStruct>(bytes));

    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SampleStruct
    {
        public int IntField;
        public float FloatField;

        public bool Equals(SampleStruct other)
        {
            return IntField == other.IntField && FloatField.Equals(other.FloatField);
        }

        public override bool Equals(object? obj)
        {
            return obj is SampleStruct other && Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(IntField, FloatField);
        }
    }

    public void Dispose() => _allocator.Dispose();
}