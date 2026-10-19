using Spyglass.Proxy.Topology;
using Spyglass.Proxy.Wire;

namespace Spyglass.Proxy.Forwarding;

/// <summary>State shared by the two pumps of one proxied connection.</summary>
internal sealed class ConnectionContext
{
    public const int DefaultMaxFrame = 48_000_000;

    public ConnectionContext(long connSeq, Member member, ProxyServer server, string clientEndpoint)
    {
        ConnSeq = connSeq;
        Member = member;
        Server = server;
        ClientEndpoint = clientEndpoint;
    }

    public long ConnSeq { get; }
    public Member Member { get; }
    public ProxyServer Server { get; }
    public string ClientEndpoint { get; }
    public bool ClientTls { get; set; }

    private int _maxFrame = DefaultMaxFrame;
    public int MaxFrame => Volatile.Read(ref _maxFrame);

    private long _serverConnectionId = -1;
    /// <summary><c>connectionId</c> of the first hello reply on the upstream connection; null until then.</summary>
    public long? ServerConnectionId => Volatile.Read(ref _serverConnectionId) is var v and >= 0 ? v : null;

    public HelloReplyFacts? FirstHello { get; private set; }

    public long BytesFromClient;
    public long BytesFromServer;

    // ---- pending hello-family requests (written by the request pump, consumed by the reply pump) ----
    // Rare (handshake + heartbeats), so a lock is fine; the reply pump checks the volatile count first
    // and never takes the lock for ordinary replies.

    private readonly object _helloGate = new();
    private readonly int[] _pendingHello = new int[8];
    private int _pendingHelloCount;

    public void AddPendingHello(int requestId)
    {
        lock (_helloGate)
        {
            if (_pendingHelloCount == _pendingHello.Length)
            {
                // A client never has this many hellos outstanding; drop the oldest rather than grow.
                Array.Copy(_pendingHello, 1, _pendingHello, 0, _pendingHello.Length - 1);
                _pendingHelloCount--;
            }
            _pendingHello[_pendingHelloCount] = requestId;
            Volatile.Write(ref _pendingHelloCount, _pendingHelloCount + 1);
        }
    }

    public bool TakePendingHello(int responseTo)
    {
        if (Volatile.Read(ref _pendingHelloCount) == 0)
            return false;
        lock (_helloGate)
        {
            for (var i = 0; i < _pendingHelloCount; i++)
            {
                if (_pendingHello[i] != responseTo) continue;
                Array.Copy(_pendingHello, i + 1, _pendingHello, i, _pendingHelloCount - i - 1);
                Volatile.Write(ref _pendingHelloCount, _pendingHelloCount - 1);
                return true;
            }
        }
        return false;
    }

    public void OnHelloReply(HelloReplyFacts facts)
    {
        if (facts.ConnectionId is { } id)
            Interlocked.CompareExchange(ref _serverConnectionId, id, -1);
        if (facts.MaxMessageSizeBytes is > FrameHeader.Size and var max)
            Volatile.Write(ref _maxFrame, max);
        FirstHello ??= facts;
        Server.Topology.OnForwardedHello(Member, facts);
    }
}
