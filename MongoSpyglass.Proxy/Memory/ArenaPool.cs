using System.Collections.Concurrent;

namespace MongoSpyglass.Proxy.Memory;

public class ArenaPool
{
    private readonly ConcurrentStack<ArenaTracker> _pool = new();
    private int _currentPoolSize = 0;
    private readonly int _maxPoolSize;

    public static ArenaPool Shared { get; } = new(maxPoolSize: 512);

    public ArenaPool(int maxPoolSize = 512)
    {
        _maxPoolSize = maxPoolSize;
    }

    public ArenaTracker Rent()
    {
        if (_pool.TryPop(out var tracker))
        {
            Interlocked.Decrement(ref _currentPoolSize);
            tracker.AddRef(); // Initial ref for the creator
            return tracker;
        }
        var newTracker = new ArenaTracker();
        newTracker.AddRef();
        return newTracker;
    }

    public void Return(ArenaTracker tracker)
    {
        // If pool is at capacity, dispose the arena instead of pooling it
        if (_currentPoolSize >= _maxPoolSize)
        {
            tracker.Arena.Dispose();
            return;
        }

        _pool.Push(tracker);
        Interlocked.Increment(ref _currentPoolSize);
    }

    public int CurrentPoolSize => _currentPoolSize;
}
