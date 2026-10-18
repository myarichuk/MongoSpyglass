using Spyglass.Proxy.Wire;

namespace Spyglass.Proxy.Forwarding;

/// <summary>
/// Observation hooks called by a pump for every frame (01 §3.2, §4). Implementations MUST be O(1)
/// apart from copying at most their capture limit, MUST NOT block, and MUST NOT allocate in steady state.
/// Exceptions are not allowed to escape: a tap fault must never affect forwarding.
/// </summary>
internal interface IFrameTap
{
    void Begin(in FrameHeader header, long tFirst, RequestKind kind);
    /// <summary>Body bytes (after the 16-byte header), in order; called zero or more times per frame.</summary>
    void Body(ReadOnlySpan<byte> chunk);
    void End(long tLast);
    /// <summary>The pump stopped; any frame in progress is abandoned.</summary>
    void Abort();
}

internal sealed class NullTap : IFrameTap
{
    public static readonly NullTap Instance = new();
    public void Begin(in FrameHeader header, long tFirst, RequestKind kind) { }
    public void Body(ReadOnlySpan<byte> chunk) { }
    public void End(long tLast) { }
    public void Abort() { }
}
