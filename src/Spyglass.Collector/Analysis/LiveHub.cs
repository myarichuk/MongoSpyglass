using System.Threading.Channels;
using Spyglass.Collector.Model;

namespace Spyglass.Collector.Analysis;

/// <summary>
/// A live event (03 §8 <c>/live</c>): its SSE name, the op it is about (for filters), and the JSON payload. Filters see the
/// event before it has a payload: the JSON is rendered only if some subscriber takes it.
/// </summary>
public sealed class LiveEvent(string name, Operation? op)
{
    public string Name { get; } = name;
    public Operation? Op { get; } = op;
    public byte[] Payload { get; internal set; } = null!;
}

/// <summary>
/// Fan-out to SSE subscribers. Each subscriber has a bounded queue that drops its oldest events when the
/// browser is slow: the analysis thread never waits on a client.
/// </summary>
public sealed class LiveHub
{
    public sealed class Subscription : IDisposable
    {
        private readonly LiveHub _hub;
        internal readonly Channel<LiveEvent> Channel;
        internal readonly Func<LiveEvent, bool> Filter;
        public long Dropped;

        internal Subscription(LiveHub hub, int capacity, Func<LiveEvent, bool> filter)
        {
            _hub = hub;
            Filter = filter;
            Channel = System.Threading.Channels.Channel.CreateBounded<LiveEvent>(new BoundedChannelOptions(capacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
            }, _ => Interlocked.Increment(ref Dropped));
        }

        public ChannelReader<LiveEvent> Reader => Channel.Reader;

        public void Dispose()
        {
            lock (_hub._subs)
            {
                _hub._subs.Remove(this);
                _hub._snapshot = [.. _hub._subs];
            }
            Channel.Writer.TryComplete();
        }
    }

    private readonly List<Subscription> _subs = [];
    private volatile Subscription[] _snapshot = [];
    private readonly int _capacity;

    public LiveHub(int capacity) => _capacity = capacity;

    public int Subscribers => _snapshot.Length;

    public Subscription Subscribe(Func<LiveEvent, bool> filter)
    {
        var s = new Subscription(this, _capacity, filter);
        lock (_subs)
        {
            _subs.Add(s);
            _snapshot = [.. _subs];
        }
        return s;
    }

    /// <summary>
    /// Publishes to every subscriber whose filter takes the event. <paramref name="render"/> runs at most once, on the
    /// calling thread (which owns the op), and only if a subscriber takes it: an open UI tab filtered to one collection
    /// must not cost a JSON serialization per op.
    /// </summary>
    public void Publish<TState>(string name, Operation? op, TState state, Func<TState, byte[]> render)
    {
        var subs = _snapshot;
        if (subs.Length == 0) return;
        var e = new LiveEvent(name, op);
        foreach (var s in subs)
        {
            if (!s.Filter(e)) continue;
            e.Payload ??= render(state);
            s.Channel.Writer.TryWrite(e);
        }
    }

    public void Publish(string name, Operation op) => Publish(name, op, op, static o => Api.Json.Write(w => Api.Json.OpSummary(w, o)));
}
