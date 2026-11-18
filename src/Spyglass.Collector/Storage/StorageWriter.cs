using Spyglass.Collector.Analysis;
using Spyglass.Collector.Api;
using Spyglass.Collector.Model;
using Spyglass.Collector.Pipeline;

namespace Spyglass.Collector.Storage;

/// <summary>
/// 03 §7 write path: one thread, transactions of ≥ 500 rows or every 250 ms. Under backpressure the raw
/// operations are sampled (every slow, errored or alerted op kept, plus 1/<see cref="SampleEvery"/> of the
/// rest); aggregates never pass through here, so they are never what gets dropped. Retention runs here too.
/// Appends, updates and callsites share one FIFO: an update can never overtake the append of its op, and each
/// write is one transaction: a failure keeps the whole batch and retries with backoff, <see cref="MaxAttempts"/>
/// times, then drops and counts it (a store that stays down must not grow the backlog without bound).
/// Memory: ops are bounded by <see cref="Capacity"/> (sampled past it, never-sampled ops included up to
/// <see cref="HardCapacity"/>), updates by <see cref="UpdateCapacity"/>, callsites by <see cref="CallsiteCapacity"/>;
/// everything over a bound is counted, never queued.
/// </summary>
internal sealed class StorageWriter
{
    public const int BatchRows = 500, Capacity = 200_000, SampleEvery = 10;
    /// <summary>Not even a slow or failed op is queued past this: 03 §7 keeps them under sampling, not under an unbounded backlog.</summary>
    public const int HardCapacity = Capacity * 2, UpdateCapacity = 200_000, CallsiteCapacity = 50_000;
    public const int MaxAttempts = 6;
    private static readonly TimeSpan BatchInterval = TimeSpan.FromMilliseconds(250);

    private readonly IOperationStore _store;
    private readonly IAggregateStore _aggregates;
    private readonly ICallsiteStore _callsites;
    private readonly CollectorOptions _options;
    private readonly LiveHub _live;
    /// <summary>An <see cref="Operation"/>, <see cref="OpUpdate"/>, <see cref="CallsiteRecord"/>, callsite touches, or a barrier.</summary>
    private readonly WorkQueue<object> _queue = new();
    private readonly Func<long> _nowUs;
    private Thread? _thread;
    private long _seq;
    private int _ops, _pendingUpdates, _pendingCallsites;
    private int _attempts;
    private long _retryAt;

    public long Written, Sampled, Pruned, Updated;
    /// <summary>Lost: ops (over <see cref="HardCapacity"/>, or a write that failed <see cref="MaxAttempts"/> times), updates, callsite records.</summary>
    public long Overflowed, WriteDropped, UpdatesDropped, CallsitesDropped, Retries;
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
        if (backlog >= HardCapacity) // a stalled store: even what matters is not queued without bound
        {
            Interlocked.Increment(ref Overflowed);
            return;
        }
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
    public void Enqueue(OpUpdate update)
    {
        if (Interlocked.Increment(ref _pendingUpdates) > UpdateCapacity)
        {
            Interlocked.Decrement(ref _pendingUpdates);
            Interlocked.Increment(ref UpdatesDropped);
            return;
        }
        _queue.Add(update);
    }

    public void Enqueue(CallsiteRecord callsite)
    {
        if (Interlocked.Increment(ref _pendingCallsites) > CallsiteCapacity)
        {
            Interlocked.Decrement(ref _pendingCallsites);
            Interlocked.Increment(ref CallsitesDropped);
            return;
        }
        _queue.Add(callsite);
    }

    public void Touch(List<string> callsites) => _queue.Add(callsites);

    /// <summary>Completes once everything enqueued before it is written.</summary>
    public Task Barrier()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Add(tcs);
        return tcs.Task;
    }

    private bool Keep(Operation op) =>
        op.Failed || op.Alerts is { Count: > 0 } || (op.ServerLatencyUs > _options.SlowShapeUs && !op.AwaitsByDesign);

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
            var now = Environment.TickCount64;
            var wait = (int)Math.Max(0, deadline - now);
            Volatile.Write(ref LastBeat, now);
            if (_retryAt > now && _batch.Count >= BatchRows * 4)
                Thread.Sleep(Math.Clamp(wait, 1, 250)); // retrying a full batch: take nothing more until it is written or dropped
            else if (_queue.TryTake(out var item, wait))
            {
                Add(item);
                while (_batch.Count < BatchRows * 4 && _barriers.Count == 0 && _queue.TryTake(out var more)) Add(more);
            }
            if (_batch.Count >= BatchRows || _barriers.Count > 0 || Environment.TickCount64 >= deadline)
            {
                Write(final: false);
                deadline = Environment.TickCount64 + (long)BatchInterval.TotalMilliseconds;
            }
            if (Environment.TickCount64 >= nextRetention)
            {
                Retention();
                nextRetention = Environment.TickCount64 + 60_000;
            }
        }
        while (_queue.TryTake(out var rest)) Add(rest);
        Write(final: true);
    }

    private void Add(object item)
    {
        switch (item)
        {
            case Operation op: _batch.Add(op); break; // still counted in Backlog until written or dropped
            case OpUpdate u: _updates.Add(u); break;
            case CallsiteRecord c: _newCallsites.Add(c); break;
            case List<string> touched: _touched.UnionWith(touched); break;
            case TaskCompletionSource b: _barriers.Add(b); break;
        }
    }

    /// <summary>One transaction for the batch. On failure it is kept and retried after a backoff (<paramref name="final"/>: at shutdown, at once).</summary>
    private void Write(bool final)
    {
        if (_batch.Count == 0 && _updates.Count == 0 && _newCallsites.Count == 0 && _touched.Count == 0)
        {
            ReleaseBarriers();
            return;
        }
        if (!final && Environment.TickCount64 < _retryAt) return; // backing off
        var committed = false;
        while (true)
        {
            try
            {
                var now = _nowUs();
                // appends first: every update in this batch names an op appended in this batch or before
                _store.Transact(() =>
                {
                    if (_batch.Count > 0) _store.Append(_batch);
                    if (_updates.Count > 0) _store.Update(_updates);
                    if (_newCallsites.Count > 0) _callsites.Upsert(_newCallsites, now);
                    if (_touched.Count > 0) _callsites.Touch(_touched, now);
                });
                Interlocked.Add(ref Written, _batch.Count);
                Interlocked.Add(ref Updated, _updates.Count);
                Volatile.Write(ref FailingSince, 0);
                _attempts = 0;
                committed = true;
                break;
            }
            catch (Exception e)
            {
                LastError = $"{e.GetType().Name}: {e.Message}";
                if (Volatile.Read(ref FailingSince) == 0) Volatile.Write(ref FailingSince, Environment.TickCount64);
                if (++_attempts < MaxAttempts)
                {
                    Interlocked.Increment(ref Retries);
                    var backoff = 250 << Math.Min(_attempts - 1, 4); // 250 ms … 4 s
                    Console.Error.WriteLine($"storage: {LastError}; retry {_attempts}/{MaxAttempts - 1} in {backoff} ms ({_batch.Count} ops, {_updates.Count} updates, {_newCallsites.Count} callsites)");
                    if (!final) { _retryAt = Environment.TickCount64 + backoff; return; }
                    Thread.Sleep(Math.Min(backoff, 500));
                    continue;
                }
                Console.Error.WriteLine($"storage: {LastError}; dropped {_batch.Count} ops, {_updates.Count} updates, {_newCallsites.Count} callsites after {_attempts} attempts");
                Interlocked.Add(ref WriteDropped, _batch.Count);
                Interlocked.Add(ref UpdatesDropped, _updates.Count);
                Interlocked.Add(ref CallsitesDropped, _newCallsites.Count);
                _attempts = 0;
                _retryAt = 0;
                break;
            }
        }
        // after the commit and outside its try: a throwing read or subscriber must not make a stored batch look failed (FC-10)
        if (committed && _updates.Count > 0 && _live.Subscribers > 0)
            foreach (var u in _updates)
                try
                {
                    if (u.Ctx != null && _store.Get(u.Id) is { } op) // late context: the op left memory when it was stored
                        _live.Publish("op.updated", op);
                }
                catch (Exception e) { Console.Error.WriteLine($"storage: publishing op.updated failed: {e.GetType().Name}: {e.Message}"); break; }
        Interlocked.Add(ref _ops, -_batch.Count);
        Interlocked.Add(ref _pendingUpdates, -_updates.Count);
        Interlocked.Add(ref _pendingCallsites, -_newCallsites.Count);
        _batch.Clear();
        _updates.Clear();
        _newCallsites.Clear();
        _touched.Clear();
        _retryAt = 0;
        ReleaseBarriers();
    }

    private void ReleaseBarriers()
    {
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
