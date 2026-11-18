using System.Collections.Concurrent;

namespace Spyglass.Collector.Pipeline;

/// <summary>
/// The hand-off between pipeline stages: a lock-free queue and an event that blocks without spinning.
/// BlockingCollection's wait spins (and yields with Thread.Sleep) before it blocks; a stage that keeps up finds its queue
/// empty after every batch, so the spinning grew as the stage got cheaper (stage 1's CPU per record rose 20 → 27 µs when
/// shape normalization fell from 10 to 4 µs), and on a shared machine it took cycles from the other stages.
/// </summary>
internal sealed class WorkQueue<T>
{
    private readonly ConcurrentQueue<T> _queue = new();
    private readonly ManualResetEventSlim _signal = new(false, spinCount: 0);
    private volatile bool _completed;
    /// <summary>Adds between their completed-check and their enqueue: the consumer must not finish while one is in flight.</summary>
    private int _adding;

    public int Count => _queue.Count;
    public bool IsCompleted => _completed && Volatile.Read(ref _adding) == 0 && _queue.IsEmpty;

    public void Add(T item)
    {
        // an Add that passed the check must not be stranded by a CompleteAdding plus the consumer's final empty check
        // before its enqueue: IsCompleted stays false while one is between the two
        Interlocked.Increment(ref _adding);
        try
        {
            if (_completed) throw new InvalidOperationException("the queue is completed");
            _queue.Enqueue(item);
            _signal.Set();
        }
        finally { Interlocked.Decrement(ref _adding); }
    }

    public bool TryTake(out T item) => _queue.TryDequeue(out item!);

    /// <summary>Waits up to <paramref name="timeoutMs"/> for an item.</summary>
    public bool TryTake(out T item, int timeoutMs)
    {
        if (_queue.TryDequeue(out item!)) return true;
        _signal.Reset();
        // an Add between the failed dequeue and the reset may have had its Set cleared: look again before waiting
        if (_queue.TryDequeue(out item!)) return true;
        if (_completed || timeoutMs == 0) return false;
        _signal.Wait(timeoutMs);
        return _queue.TryDequeue(out item!);
    }

    public void CompleteAdding()
    {
        _completed = true;
        _signal.Set();
    }
}
