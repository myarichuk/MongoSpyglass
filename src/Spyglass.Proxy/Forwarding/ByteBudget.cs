namespace Spyglass.Proxy.Forwarding;

/// <summary>A process-wide byte budget: <see cref="TryAcquire"/> never blocks, it refuses (01 §13).</summary>
internal sealed class ByteBudget(long limit)
{
    private long _used;

    public long Limit => limit;

    public long Used => Volatile.Read(ref _used);

    public bool TryAcquire(long bytes)
    {
        if (Interlocked.Add(ref _used, bytes) <= limit) return true;
        Interlocked.Add(ref _used, -bytes);
        return false;
    }

    public void Release(long bytes) => Interlocked.Add(ref _used, -bytes);
}
