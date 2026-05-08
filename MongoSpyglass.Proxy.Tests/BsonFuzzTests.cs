using MongoSpyglass.Proxy.Bson;
using SharpArena.Allocators;
using Xunit;

namespace MongoSpyglass.Proxy.Tests;

public class BsonFuzzTests
{
    [Fact]
    public unsafe void ArenaBsonReader_ShouldNotCrash_OnRandomData()
    {
        using var arena = new ArenaAllocator();
        var random = new Random(42);
        
        for (int i = 0; i < 1000; i++)
        {
            int size = random.Next(1, 100);
            var data = new byte[size];
            random.NextBytes(data);
            
            fixed (byte* p = data)
            {
                try
                {
                    var doc = ArenaBsonReader.ReadInPlace(p, size, arena);
                    // If it didn't crash, it's a win. We can also try to access keys.
                    if (!doc.IsDefault)
                    {
                        foreach (var key in doc.KeysEnumerable)
                        {
                            var s = key.ToString();
                        }
                    }
                }
                catch (Exception)
                {
                    // Expected for random data
                }
                finally
                {
                    arena.Reset();
                }
            }
        }
    }

    [Fact]
    public unsafe void ArenaBsonReader_ShouldHandleTruncatedBson()
    {
        using var arena = new ArenaAllocator();
        // Valid small document: { "a": 1 }
        // Length(4) + Type(1) + Name("a\0") + Value(4) + End(1) = 4 + 1 + 2 + 4 + 1 = 12
        var valid = new byte[] { 12, 0, 0, 0, 0x10, (byte)'a', 0, 1, 0, 0, 0, 0 };
        
        for (int i = 1; i < valid.Length; i++)
        {
            fixed (byte* p = valid)
            {
                try
                {
                    var doc = ArenaBsonReader.ReadInPlace(p, i, arena);
                }
                catch (Exception)
                {
                    // Truncated data should be handled gracefully (either return IsDefault or throw caught exception)
                }
            }
        }
    }
}
