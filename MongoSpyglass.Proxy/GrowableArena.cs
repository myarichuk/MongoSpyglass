using Simple.Arena;

namespace MongoSpyglass.Proxy;

public class GrowableArena : IDisposable
{
    private readonly bool _isOwnerOfArenaPool;
    private readonly List<Arena> _arenas = new();
    private readonly List<IDisposable> _arenaHandles = new();
    private ArenaPool _arenaPool;
    
    public GrowableArena(ArenaPool? arenaPool = null)
    {
        _isOwnerOfArenaPool = arenaPool == null;
        _arenaPool = arenaPool ?? new ArenaPool();
    }

    public Span<T> Allocate<T>(int amountOfTs) where T : unmanaged
    {
        foreach (var arena in _arenas)
        {
            if (arena.TryAllocate(amountOfTs, out Span<T> segment))
            {
                return segment;
            }
        }

        // no existing arena could satisfy the allocation.
        _arenaHandles.Add(_arenaPool.Allocate(out var newArena));
        _arenas.Add(newArena);
        return newArena.Allocate<T>(amountOfTs);
    }

    public void Dispose()
    {
        foreach(var handle in _arenaHandles)
        {
            handle.Dispose();
        };
        
        _arenas.Clear();

        if (_isOwnerOfArenaPool)
        {
            _arenaPool.Dispose();
        }
    }
}