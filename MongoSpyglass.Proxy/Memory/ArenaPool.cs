using System.Collections.Concurrent;

namespace MongoSpyglass.Proxy.Memory;

public class ArenaPool
{
    private readonly ConcurrentStack<ArenaTracker> _pool = new();
    public static ArenaPool Shared { get; } = new();

    public ArenaTracker Rent()
    {
        if (_pool.TryPop(out var tracker)) 
        {
            tracker.AddRef(); // Initial ref for the creator
            return tracker;
        }
        var newTracker = new ArenaTracker();
        newTracker.AddRef();
        return newTracker;
    }

    public void Return(ArenaTracker tracker)
    {
        _pool.Push(tracker);
    }
}
