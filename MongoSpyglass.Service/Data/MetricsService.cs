using System;
using System.Diagnostics.Metrics;
using MongoSpyglass.Proxy.Profiling;
using MongoSpyglass.Service.Analyzers;

namespace MongoSpyglass.Service.Data;

public class MetricsService
{
    private static readonly Meter Meter = new("MongoSpyglass", "1.0.0");

    private readonly Counter<long> _operationCounter;
    private readonly Histogram<double> _latencyHistogram;
    private readonly ObservableGauge<int> _activeCursorsGauge;
    private readonly ObservableGauge<int> _leakedCursorsGauge;
    private readonly ObservableGauge<long> _droppedFactsGauge;
    private readonly ObservableGauge<long> _droppedMessagesGauge;

    private readonly IAnalyzerPlugin _analyzer;
    private readonly CorrelationRingBuffer _correlationBuffer;
    private long _lastMessageCount = 0;
    private long _droppedMessageCount = 0;

    public MetricsService(IAnalyzerPlugin analyzer, CorrelationRingBuffer correlationBuffer)
    {
        _analyzer = analyzer;
        _correlationBuffer = correlationBuffer;

        // Counter: total operations
        _operationCounter = Meter.CreateCounter<long>(
            "mongodb.operations.total",
            unit: "{operation}",
            description: "Total MongoDB operations"
        );

        // Histogram: request latency
        _latencyHistogram = Meter.CreateHistogram<double>(
            "mongodb.request.duration_ms",
            unit: "ms",
            description: "MongoDB request duration in milliseconds"
        );

        // Observable gauges (sampled on collection)
        _activeCursorsGauge = Meter.CreateObservableGauge(
            "mongodb.cursors.active",
            GetActiveCursorCount,
            description: "Number of active MongoDB cursors"
        );

        _leakedCursorsGauge = Meter.CreateObservableGauge(
            "mongodb.cursors.leaked",
            GetLeakedCursorCount,
            description: "Number of leaked (orphaned) MongoDB cursors"
        );

        _droppedFactsGauge = Meter.CreateObservableGauge(
            "mongospyglass.facts.dropped",
            GetDroppedFactCount,
            description: "Number of dropped RETE facts due to buffer overflow"
        );

        _droppedMessagesGauge = Meter.CreateObservableGauge(
            "mongospyglass.messages.dropped",
            GetDroppedMessageCount,
            description: "Number of dropped traffic messages due to buffer overflow"
        );
    }

    public void RecordOperation(double durationMs)
    {
        _operationCounter.Add(1);
        _latencyHistogram.Record(durationMs);
    }

    public void SetDroppedMessageCount(long count)
    {
        _droppedMessageCount = count;
    }

    private int GetActiveCursorCount()
    {
        return (_analyzer as ReteAnalyzerEngine)?.ActiveCursorsCount ?? 0;
    }

    private int GetLeakedCursorCount()
    {
        // This would require querying the session for orphaned cursors
        // For now, return 0 (can be enhanced with direct session access)
        return 0;
    }

    private long GetDroppedFactCount()
    {
        return (_analyzer as ReteAnalyzerEngine)?.DroppedFactCount ?? 0;
    }

    private long GetDroppedMessageCount()
    {
        return _droppedMessageCount;
    }
}
