using System.IO.Compression;
using System.Text;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using Spyglass.Collector.Analysis;
using Spyglass.Collector.Ingest;
using Spyglass.Collector.Model;
using Spyglass.Collector.Pipeline;
using Spyglass.Collector.Storage;

namespace Spyglass.Collector;

/// <summary>Outcome of one ingest POST (02 §5).</summary>
public readonly record struct IngestResult(int Status, int Accepted, int Rejected, int Ignored, string? Error = null);

/// <summary>
/// The collector's pipeline (03 §1): ingest → partitions (stage 1) → analysis (stage 2) → storage, plus the
/// shared registries. Hosting (HTTP, auth, UI) lives in <see cref="Server"/>; this class has no HTTP in it.
/// </summary>
public sealed class Collector : IAsyncDisposable
{
    /// <summary>02 §5 batch limits.</summary>
    public const int MaxBatchRecords = 2000, MaxBatchBytes = 1024 * 1024;

    private readonly Partition[] _partitions;
    private readonly AnalysisStage _analysis;
    private readonly StorageWriter _writer;
    private long _charged;
    private readonly Timer _sweep;

    public CollectorOptions Options { get; }
    public ConnectionRegistry Connections { get; } = new();
    public ProducerRegistry Producers { get; } = new();
    public LiveHub Live { get; }
    public IOperationStore Ops { get; }
    public IAggregateStore Aggregates { get; }
    public IAlertStore AlertStore { get; }
    public Func<long> NowUs { get; }

    // self-telemetry (03 §8 /metrics)
    public long RecordsAccepted, RecordsRejected, RecordsIgnored, RedactionRejected, ClockSkewed, ContextRecords;
    public long Batches2xx, Batches400, Batches413, Batches429;

    public Collector(CollectorOptions options, IOperationStore ops, IAggregateStore aggregates, IAlertStore alerts, Func<long>? nowUs = null)
    {
        Options = options;
        Ops = ops;
        Aggregates = aggregates;
        AlertStore = alerts;
        NowUs = nowUs ?? (() => (DateTime.UtcNow - DateTime.UnixEpoch).Ticks / 10);
        Live = new LiveHub(options.LiveBuffer);
        _writer = new StorageWriter(ops, aggregates, options, NowUs);
        _analysis = new AnalysisStage(options, _writer, aggregates, alerts, ops, Live, Release, NowUs);
        _partitions = new Partition[options.Workers];
        for (var i = 0; i < _partitions.Length; i++)
            _partitions[i] = new Partition(options, Connections, Producers, _analysis.Post, Release);
        _sweep = new Timer(_ => Connections.Sweep(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    internal AnalysisStage Analysis => _analysis;
    internal StorageWriter Writer => _writer;
    internal IReadOnlyList<Partition> Partitions => _partitions;
    public long QueuedBytes => Volatile.Read(ref _charged);

    public void Start()
    {
        _writer.Start();
        _analysis.Start();
        for (var i = 0; i < _partitions.Length; i++) _partitions[i].Start(i);
    }

    private void Release(Record r) => Interlocked.Add(ref _charged, -r.Size);

    /// <summary>Handles one POSTed batch. Never blocks on the pipeline (03 §1: answer within 50 ms).</summary>
    public IngestResult Ingest(ReadOnlySpan<byte> body, string? contentEncoding, string? producerHeader)
    {
        byte[]? inflated = null;
        if (contentEncoding is { } enc && enc.Contains("gzip", StringComparison.OrdinalIgnoreCase))
        {
            try { inflated = Gunzip(body); }
            catch (InvalidDataException) { Interlocked.Increment(ref Batches400); return new IngestResult(400, 0, 0, 0, "bad gzip"); }
            if (inflated == null) { Interlocked.Increment(ref Batches413); return new IngestResult(413, 0, 0, 0, "batch over 1 MiB uncompressed"); }
            body = inflated;
        }
        if (body.Length > MaxBatchBytes) { Interlocked.Increment(ref Batches413); return new IngestResult(413, 0, 0, 0, "batch over 1 MiB"); }
        if (body.Count((byte)'\n') + (body.Length > 0 && body[^1] != (byte)'\n' ? 1 : 0) > MaxBatchRecords) { Interlocked.Increment(ref Batches413); return new IngestResult(413, 0, 0, 0, "batch over 2000 records"); }
        if (Volatile.Read(ref _charged) > Options.QueueBytes * 9 / 10) { Interlocked.Increment(ref Batches429); return new IngestResult(429, 0, 0, 0, "ingest queue above 90%"); }

        var now = NowUs();
        var maxTs = now + (long)Options.MaxFutureSkew.TotalMicroseconds;
        int accepted = 0, rejected = 0, ignored = 0, syntax = 0;
        var perPartition = new List<Record>?[_partitions.Length]; // one hand-off per partition per POST
        foreach (var range in body.Split((byte)'\n'))
        {
            var line = body[range].TrimEnd((byte)'\r');
            if (line.IsEmpty) continue;
            var result = RecordParser.Parse(line, out var record, out var error);
            if (result == ParseResult.Ignored) { ignored++; continue; }
            if (result is ParseResult.Rejected or ParseResult.Invalid)
            {
                rejected++;
                if (result == ParseResult.Invalid) syntax++;
                continue;
            }
            var r = record!;
            var producer = Producers.Touch(r.Src, producerHeader, now);
            Interlocked.Increment(ref producer.Records);
            if (r.Ts > maxTs) // 03 §2: clamp, count per src
            {
                r.Ts = now;
                Interlocked.Increment(ref producer.ClockSkewed);
                Interlocked.Increment(ref ClockSkewed);
            }
            if (r is OpRecord op && !RedactionHolds(op))
            {
                rejected++;
                Interlocked.Increment(ref producer.Rejected);
                Interlocked.Increment(ref RedactionRejected);
                continue;
            }
            Route(r, perPartition);
            accepted++;
        }
        for (var i = 0; i < perPartition.Length; i++)
            if (perPartition[i] is { } batch) _partitions[i].Post(batch);
        Interlocked.Add(ref RecordsAccepted, accepted);
        Interlocked.Add(ref RecordsRejected, rejected);
        Interlocked.Add(ref RecordsIgnored, ignored);
        if (accepted == 0 && rejected > 0 && syntax == rejected)
        {
            Interlocked.Increment(ref Batches400);
            return new IngestResult(400, 0, rejected, ignored, "no line is a JSON object");
        }
        Interlocked.Increment(ref Batches2xx);
        return new IngestResult(204, accepted, rejected, ignored);
    }

    private void Route(Record r, List<Record>?[] perPartition)
    {
        switch (r)
        {
            case ConnRecord c: Connections.Apply(c); return;
            case ProxyRecord p: Producers.Apply(p); return;
            case StatsRecord s: Producers.Apply(s); return;
            case ContextRecord: Interlocked.Increment(ref ContextRecords); return; // M2: the join
            case OpRecord op:
                Charge(op);
                (perPartition[PartitionFor(op.Src, op.Member, op.ServerConnectionId ?? -op.ConnSeq)] ??= []).Add(op);
                return;
            case StreamRecord s:
                Charge(s);
                (perPartition[PartitionFor(s.Src, s.Member, s.ServerConnectionId ?? -1)] ??= []).Add(s);
                return;
        }
    }

    private void Charge(Record r) => Interlocked.Add(ref _charged, r.Size);

    private int PartitionFor(string proxyId, string member, long sid) =>
        (int)((uint)HashCode.Combine(proxyId, member, sid) % (uint)_partitions.Length);

    /// <summary>03 §9 defence in depth: an auth command's body must be exactly the proxy's <c>{$redacted: name}</c>.</summary>
    private static bool RedactionHolds(OpRecord op)
    {
        if (op.Command is not ("authenticate" or "saslStart" or "saslContinue" or "getnonce" or "createUser" or "updateUser" or "copydbgetnonce" or "copydbsaslstart" or "copydb"))
            return true;
        if (op.Cmd == null) return true;
        try
        {
            var d = BsonSerializer.Deserialize<BsonDocument>(op.Cmd);
            return d.ElementCount == 1 && d.GetElement(0).Name == "$redacted";
        }
        catch (Exception) { return false; }
    }

    private static byte[]? Gunzip(ReadOnlySpan<byte> body)
    {
        using var gz = new GZipStream(new MemoryStream(body.ToArray()), CompressionMode.Decompress);
        var buf = new byte[MaxBatchBytes + 1];
        var n = 0;
        int read;
        while (n < buf.Length && (read = gz.Read(buf, n, buf.Length - n)) > 0) n += read;
        return n > MaxBatchBytes ? null : buf.AsSpan(0, n).ToArray();
    }

    /// <summary>Waits until everything ingested so far has been analysed (tests, shutdown).</summary>
    public async Task DrainAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (QueuedBytes > 0 && DateTime.UtcNow < deadline)
            await Task.Delay(5).ConfigureAwait(false);
        _analysis.Tick();
        await Task.Delay(20).ConfigureAwait(false);
    }

    public string RenderMetrics()
    {
        var sb = new StringBuilder();
        void M(string name, string type, string help, params (string Labels, double Value)[] series)
        {
            sb.Append("# HELP ").Append(name).Append(' ').Append(help).Append('\n');
            sb.Append("# TYPE ").Append(name).Append(' ').Append(type).Append('\n');
            foreach (var (l, v) in series)
                sb.Append(name).Append(l.Length > 0 ? "{" + l + "}" : "").Append(' ').Append(v.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
        }
        M("spyglass_collector_ingest_records_total", "counter", "Ingested records by result.",
            ("result=\"accepted\"", RecordsAccepted), ("result=\"rejected\"", RecordsRejected), ("result=\"ignored\"", RecordsIgnored),
            ("result=\"redaction_rejected\"", RedactionRejected), ("result=\"context_deferred\"", ContextRecords));
        M("spyglass_collector_ingest_batches_total", "counter", "Ingest POSTs by response.",
            ("status=\"204\"", Batches2xx), ("status=\"400\"", Batches400), ("status=\"413\"", Batches413), ("status=\"429\"", Batches429));
        M("spyglass_collector_ingest_queue_bytes", "gauge", "Records ingested and not yet analysed (03 §1 bound).", ("", QueuedBytes));
        M("spyglass_collector_clock_skew_total", "counter", "Records whose ts was more than 5 min ahead and was clamped.", ("", ClockSkewed));
        M("spyglass_collector_duplicates_total", "counter", "Records suppressed as duplicates (02 §6.4).", ("", _partitions.Sum(p => p.Duplicates)));
        M("spyglass_collector_decode_failures_total", "counter", "op records whose cmd was not decodable BSON.", ("", _partitions.Sum(p => p.DecodeFailures)));
        M("spyglass_collector_join_conflicts_total", "counter", "02 §6.3 join conflicts (M2).", ("", 0));
        M("spyglass_collector_ops_analysed_total", "counter", "Operations through the analysis stage.", ("", _analysis.Processed));
        M("spyglass_collector_ops_stored_total", "counter", "Operations written to the store.", ("", _writer.Written));
        M("spyglass_collector_ops_sampled_out_total", "counter", "Operations not stored under write backpressure (03 §7).", ("", _writer.Sampled));
        M("spyglass_collector_ops_pruned_total", "counter", "Operations removed by retention.", ("", _writer.Pruned));
        M("spyglass_collector_storage_backlog", "gauge", "Operations waiting to be written.", ("", _writer.Backlog));
        M("spyglass_collector_cursors_open", "gauge", "Server cursors tracked as open (03 §5.4).", ("", _analysis.OpenCursors.Length));
        M("spyglass_collector_alerts_open", "gauge", "Open alerts.", ("", _analysis.Alerts.Snapshot().Count(a => a.Open)));
        M("spyglass_collector_live_subscribers", "gauge", "SSE subscribers.", ("", Live.Subscribers));
        return sb.ToString();
    }

    public async ValueTask DisposeAsync()
    {
        await _sweep.DisposeAsync().ConfigureAwait(false);
        foreach (var p in _partitions) p.Complete();
        foreach (var p in _partitions) p.Join();
        _analysis.Complete();
        _analysis.Join();
        _writer.Complete();
        _writer.Join(); // the stores belong to whoever created them
    }
}
