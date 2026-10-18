using System.Collections.Concurrent;
using Spyglass.Collector.Model;

namespace Spyglass.Collector.Storage;

/// <summary>
/// 03 §7 write path: one thread, transactions of ≥ 500 rows or every 250 ms. Under backpressure the raw
/// operations are sampled (every slow, errored or alerted op kept, plus 1/<see cref="SampleEvery"/> of the
/// rest); aggregates never pass through here, so they are never what gets dropped. Retention runs here too.
/// </summary>
internal sealed class StorageWriter
{
    public const int BatchRows = 500, Capacity = 200_000, SampleEvery = 10;
    private static readonly TimeSpan BatchInterval = TimeSpan.FromMilliseconds(250);

    private readonly IOperationStore _store;
    private readonly IAggregateStore _aggregates;
    private readonly CollectorOptions _options;
    private readonly BlockingCollection<Operation> _queue = new(new ConcurrentQueue<Operation>());
    private readonly Func<long> _nowUs;
    private Thread? _thread;
    private long _seq;

    public long Written, Sampled, Pruned;

    public StorageWriter(IOperationStore store, IAggregateStore aggregates, CollectorOptions options, Func<long> nowUs)
    {
        _store = store;
        _aggregates = aggregates;
        _options = options;
        _nowUs = nowUs;
    }

    public int Backlog => _queue.Count;

    public void Start()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "collector-storage" };
        _thread.Start();
    }

    /// <summary>Analysis thread. Never blocks.</summary>
    public void Enqueue(Operation op)
    {
        var backlog = _queue.Count;
        if (backlog > Capacity / 2 && !Keep(op))
        {
            // half full: sample; full: keep only what matters
            if (backlog >= Capacity || ++_seq % SampleEvery != 0)
            {
                Interlocked.Increment(ref Sampled);
                return;
            }
        }
        _queue.Add(op);
    }

    private bool Keep(Operation op) =>
        op.IsError || op.Alerts is { Count: > 0 } || op.ServerLatencyUs > _options.SlowShapeUs;

    public void Complete() => _queue.CompleteAdding();
    public void Join() => _thread?.Join();

    private void Loop()
    {
        var batch = new List<Operation>(BatchRows);
        var nextRetention = Environment.TickCount64 + 10_000;
        var deadline = Environment.TickCount64 + (long)BatchInterval.TotalMilliseconds;
        while (!_queue.IsCompleted)
        {
            var wait = (int)Math.Max(0, deadline - Environment.TickCount64);
            if (_queue.TryTake(out var op, wait))
            {
                batch.Add(op);
                while (batch.Count < BatchRows * 4 && _queue.TryTake(out var more)) batch.Add(more);
            }
            if (batch.Count >= BatchRows || Environment.TickCount64 >= deadline)
            {
                Write(batch);
                deadline = Environment.TickCount64 + (long)BatchInterval.TotalMilliseconds;
            }
            if (Environment.TickCount64 >= nextRetention)
            {
                Retention();
                nextRetention = Environment.TickCount64 + 60_000;
            }
        }
        while (_queue.TryTake(out var rest)) batch.Add(rest);
        Write(batch);
    }

    private void Write(List<Operation> batch)
    {
        if (batch.Count == 0) return;
        try
        {
            _store.Append(batch);
            Interlocked.Add(ref Written, batch.Count);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"storage: {e.GetType().Name}: {e.Message} ({batch.Count} ops lost)");
        }
        batch.Clear();
    }

    private void Retention()
    {
        try
        {
            var now = _nowUs();
            Interlocked.Add(ref Pruned, _store.Prune(now - (long)_options.OpRetention.TotalMicroseconds, _options.OpStoreBytes));
            _aggregates.Rollup(now);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"retention: {e.GetType().Name}: {e.Message}");
        }
    }
}
