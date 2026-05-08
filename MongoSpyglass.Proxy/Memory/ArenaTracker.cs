using SharpArena.Allocators;
using System.Threading;

namespace MongoSpyglass.Proxy.Memory;

public class ArenaTracker
{
    public ArenaAllocator Arena { get; } = new();
    private int _refCount;
    public void AddRef() => Interlocked.Increment(ref _refCount);
    public void Release() 
    {
        if (Interlocked.Decrement(ref _refCount) == 0)
        {
            // If the arena has grown too large (e.g. 1MB), dispose it instead of pooling
            // to prevent "bloated" arenas from consuming all memory.
            if (Arena.AllocatedBytes > 1024 * 1024)
            {
                Arena.Dispose();
                // We don't return it to the pool, so it will be GC'd
            }
            else
            {
                Arena.Reset();
                ArenaPool.Shared.Return(this);
            }
        }
    }
}
