using System.Buffers;

namespace MongoSpyglass.Proxy.Profiling;

public class CorrelationRingBuffer : IDisposable
{
    private readonly OperationMetrics[] _buffer;
    private readonly int _mask;

    public CorrelationRingBuffer(int capacity = 1024)
    {
        // Capacity must be a power of 2 for the mask trick
        if ((capacity & (capacity - 1)) != 0)
        {
            capacity = (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)capacity);
        }

        _buffer = ArrayPool<OperationMetrics>.Shared.Rent(capacity);
        // Clear stale data from prior tenant of the pooled buffer
        Array.Clear(_buffer, 0, _buffer.Length);
        _mask = capacity - 1;
    }

    public void RecordRequest(int requestId, OperationMetrics metrics)
    {
        _buffer[requestId & _mask] = metrics;
    }

    public bool TryGetRequest(int responseTo, out OperationMetrics metrics)
    {
        metrics = _buffer[responseTo & _mask];
        // Basic validation that this is actually the right request
        return metrics.RequestId == responseTo;
    }

    public void Dispose()
    {
        ArrayPool<OperationMetrics>.Shared.Return(_buffer);
    }
}
