using BenchmarkDotNet.Attributes;
using MongoSpyglass.Proxy;
using MongoSpyglass.Proxy.WireProtocol;

[MemoryDiagnoser]
public class StringReadBenchmarks
{
    private MemoryStream _testStreamA;
    private MemoryStream _testStreamB;
    private GrowableArena _memoryAllocator;

    [IterationSetup]
    public void Setup()
    {
        _memoryAllocator = new GrowableArena();
        
        // Setup a test stream filled with some data
        byte[] byteArray = "This is a test string.\0"u8.ToArray();
        _testStreamA = new MemoryStream(byteArray);
        _testStreamB = new MemoryStream(byteArray);
        _testStreamA.Position = 0;
        _testStreamB.Position = 0;
    }

    [IterationCleanup]
    public void Cleanup() => 
        _memoryAllocator.Dispose();
    
    [Benchmark]
    public void CustomReadCStringFromStream()
    {
        for (int i = 0; i < 1000000; i++)
        {
            _testStreamA.TryReadNativeStringFromStream(_memoryAllocator, out _);
        }
    }

    [Benchmark]
    public void StreamReader()
    {
        for (int i = 0; i < 1000000; i++)
        {
            using var reader = new StreamReader(_testStreamB, System.Text.Encoding.UTF8, leaveOpen: true);
            reader.ReadToEnd();
        }
    }
}