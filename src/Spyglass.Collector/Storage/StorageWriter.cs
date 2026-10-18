using System.Collections.Concurrent;
using Spyglass.Collector.Analysis;
using Spyglass.Collector.Api;
using Spyglass.Collector.Model;

namespace Spyglass.Collector.Storage;

/// <summary>
/// 03 §7 write path: one thread, transactions of ≥ 500 rows or every 250 ms. Under backpressure the raw
/// operations are sampled (every slow, errored or alerted op kept, plus 1/<see cref="SampleEvery"/> of the
/// rest); aggregates never pass through here, so they are never what gets dropped. Retention runs here too.
/// Appends, updates and callsites share one FIFO: an update can never overtake the append of its op.
/// </summary>
internal sealed class StorageWriter
{
    public const int BatchRows = 500, Capacity = 200_000, SampleEvery = 10;
    private static readonly TimeSpan BatchInterval = TimeSpan.FromMilliseconds(250);

    private readonly IOperationStore _store;
    private readonly IAggregateStore _aggregates;
    private readonly ICallsiteStore _callsites;
    private readonly CollectorOptions _options;
    private readonly LiveHub _live;
    /// <summary>An <see cref="Operation"/>, <see cref="OpUpdate"/>, <see cref="CallsiteRecord"/>, callsite touches, or a barrier.</summary>
    private readonly BlockingCollection<object> _queue = new(new ConcurrentQueue<object>());
    private readonly Func<long> _nowUs;
    private Thread? _thread;
    private long _seq;
    private int _ops;

    public long Written, Sampled, Pruned, Updated;
    /// <summary>Environment.TickCount64 at the loop's last turn (every 250 ms, or between retention chunks).</summary>
    public long LastBeat = Environment.TickCount64;
    /// <summary>Environment.TickCount64 of the first failed write since the last good one; 0 while writes succeed.</summary>
    public long FailingSince;
    public string? LastError;
    public bool Alive => _thread?.IsAlive == true;

    public StorageWriter(IOperationStore store, IAggregateStore aggregates, ICallsiteStore callsites, CollectorOptions options, Func<long> nowUs, LiveHub live)
    {
        _store = store;
        _aggregates = aggregates;
        _callsites = callsites;
        _options = options;
        _nowUs = nowUs;
        _live = live;
    }

    public int Backlog => Volatile.Read(ref _ops);

    public void Start()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "collector-storage" };
        _thread.Start();
    }

    /// <summary>Analysis thread. Never blocks.</summary>
    public void Enqueue(Operation op)
    {
        var backlog = Volatile.Read(ref _ops);
        if (backlog > Capacity / 2 && !Keep(op))
        {
            // half full: sample; full: keep only what matters
            if (backlog >= Capacity || ++_seq % SampleEvery != 0)
            {
                Interlocked.Increment(ref Sampled);
                return;
            }
        }
        Interlocked.Increment(ref _ops);
        _queue.Add(op);
    }

    /// <summary>Never sampled: an update is small, and its op may be the one that mattered.</summary>
    public void Enqueue(OpUpdate update) => _queue.Add(update);

    public void Enqueue(CallsiteRecord callsite) => _queue.Add(callsite);

    public void Touch(List<string> callsites) => _queue.Add(callsites);

    /// <summary>Completes once everything enqueued before it is written.</summary>
    public Task Barrier()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Add(tcs);
        return tcs.Task;
    }

    private bool Keep(Operation op) =>
        op.IsError || op.Alerts is { Count: > 0 } || (op.ServerLatencyUs > _options.SlowShapeUs && !op.AwaitsByDesign);

    public void Complete() => _queue.CompleteAdding();
    public void Join() => _thread?.Join();

    private readonly List<Operation> _batch = new(BatchRows);
    private readonly List<OpUpdate> _updates = [];
    private readonly List<CallsiteRecord> _newCallsites = [];
    private readonly HashSet<string> _touched = [];
    private readonly List<TaskCompletionSource> _barriers = [];

    private void Loop()
    {
        var nextRetention = Environment.TickCount64 + 10_000;
        var deadline = Environment.TickCount64 + (long)BatchInterval.TotalMilliseconds;
        while (!_queue.IsCompleted)
        {
            var wait = (int)Math.Max(0, deadline - Environment.TickCount64);
            Volatile.Write(ref LastBeat, Environment.TickCount64);
            if (_queue.TryTake(out var item, wait))
            {
                Add(item);
                while (_batch.Count < BatchRows * 4 && _barriers.Count == 0 && _queue.TryTake(out var more)) Add(more);
            }
            if (_batch.Count >= BatchRows || _barriers.Count > 0 || Environment.TickCount64 >= deadline)
            {
                Write();
                deadline = Environment.TickCount64 + (long)BatchInterval.TotalMilliseconds;
            }
            if (Environment.TickCount64 >= nextRetention)
            {
                Retention();
                nextRetention = Environment.TickCount64 + 60_000;
            }
        }
        while (_queue.TryTake(out var rest)) Add(rest);
        Write();
    }

    private void Add(object item)
    {
        switch (item)
        {
            case Operation op: _batch.Add(op); Interlocked.Decrement(ref _ops); break;
            case OpUpdate u: _updates.Add(u); break;
            case CallsiteRecord c: _newCallsites.Add(c); break;
            case List<string> touched: _touched.UnionWith(touched); break;
            case TaskCompletionSource b: _barriers.Add(b); break;
        }
    }

    private void Write()
    {
        try
        {
            // appends first: every update in this batch names an op appended in this batch or before
            if (_batch.Count > 0)
            {
                _store.Append(_batch);
                Interlocked.Add(ref Written, _batch.Count);
            }
            if (_updates.Count > 0)
            {
                _store.Update(_updates);
                Interlocked.Add(ref Updated, _updates.Count);
                if (_live.Subscribers > 0)
                    foreach (var u in _updates)
                        if (u.Ctx != null && _store.Get(u.Id) is { } op) // late context: the op left memory when it was stored
                            _live.Publish(new LiveEvent("op.updated", Json.Write(w => Json.OpSummary(w, op)), op));
            }
            var now = _nowUs();
            if (_newCallsites.Count > 0) _callsites.Upsert(_newCallsites, now);
            if (_touched.Count > 0) _callsites.Touch(_touched, now);
            Volatile.Write(ref FailingSince, 0);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"storage: {e.GetType().Name}: {e.Message} ({_batch.Count} ops, {_updates.Count} updates lost)");
            LastError = $"{e.GetType().Name}: {e.Message}";
            if (Volatile.Read(ref FailingSince) == 0) Volatile.Write(ref FailingSince, Environment.TickCount64);
        }
        _batch.Clear();
        _updates.Clear();
        _newCallsites.Clear();
        _touched.Clear();
        foreach (var b in _barriers) b.TrySetResult();
        _barriers.Clear();
    }

    private void Retention()
    {
        try
        {
            var now = _nowUs();
            Interlocked.Add(ref Pruned, _store.Prune(now - (long)_options.OpRetention.TotalMicroseconds, _options.OpStoreBytes,
                () => Volatile.Write(ref LastBeat, Environment.TickCount64)));
            _aggregates.Rollup(now);
            _callsites.PruneCallsites(now - (long)TimeSpan.FromDays(30).TotalMicroseconds);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"retention: {e.GetType().Name}: {e.Message}");
        }
    }
}
