using SharpArena.Allocators;
using System.Collections.Concurrent;

namespace MongoSpyglass.Proxy.Memory;

public class ArenaPool
{
    private readonly ConcurrentStack<ArenaAllocator> _pool = new();
    public static ArenaPool Shared { get; } = new();

    public ArenaAllocator Rent()
    {
        if (_pool.TryPop(out var arena)) return arena;
        return new ArenaAllocator();
    }

    public void Return(ArenaAllocator arena)
    {
        arena.Reset();
        _pool.Push(arena);
    }
}
