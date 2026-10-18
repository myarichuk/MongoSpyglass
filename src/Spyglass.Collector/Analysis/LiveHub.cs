using System.Threading.Channels;
using Spyglass.Collector.Model;

namespace Spyglass.Collector.Analysis;

/// <summary>A live event (03 §8 <c>/live</c>): its SSE name, the JSON payload, and the op it is about (for filters).</summary>
public sealed record LiveEvent(string Name, byte[] Payload, Operation? Op);

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

    public void Publish(LiveEvent e)
    {
        foreach (var s in _snapshot)
            if (s.Filter(e))
                s.Channel.Writer.TryWrite(e);
    }
}
