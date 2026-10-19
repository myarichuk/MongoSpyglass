using System.IO.Compression;
using System.Text;
using MongoDB.Bson;
using Spyglass.Collector.Analysis;
using Spyglass.Collector.Ingest;
using Spyglass.Collector.Model;
using Spyglass.Collector.Pipeline;
using Spyglass.Collector.Storage;

namespace Spyglass.Collector;

/// <summary>One health check: <paramref name="Ok"/> false fails <see cref="HealthReport.Live"/> if <paramref name="Liveness"/>, else only readiness.</summary>
public readonly record struct HealthCheck(string Name, bool Ok, bool Liveness, string Detail);

/// <summary>
/// <c>/healthz</c> (live: every stage is turning and storage is writing; a restart may help otherwise) and
/// <c>/readyz</c> (ready: also not shedding ingest with 429s).
/// </summary>
public sealed record HealthReport(bool Live, bool Ready, IReadOnlyList<HealthCheck> Checks);

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
    public JoinCounters Join { get; } = new();
    /// <summary>When an exporter last sent anything (µs): ops wait for context only while one is reporting (02 §6.2).</summary>
    private long _lastContextUs = long.MinValue / 2;
    private static readonly long ExporterIdleUs = 600_000_000;
    public long Batches2xx, Batches400, Batches413, Batches429;

    public ICallsiteStore Callsites { get; }

    public Collector(CollectorOptions options, IOperationStore ops, IAggregateStore aggregates, IAlertStore alerts, Func<long>? nowUs = null,
        ICallsiteStore? callsites = null)
    {
        Options = options;
        Ops = ops;
        Aggregates = aggregates;
        AlertStore = alerts;
        Callsites = callsites ?? ops as ICallsiteStore ?? new MemoryStore();
        NowUs = nowUs ?? (() => (DateTime.UtcNow - DateTime.UnixEpoch).Ticks / 10);
        Live = new LiveHub(options.LiveBuffer);
        _writer = new StorageWriter(ops, aggregates, Callsites, options, NowUs, Live);
        _analysis = new AnalysisStage(options, _writer, aggregates, alerts, ops, Live, Producers, Release, NowUs);
        _partitions = new Partition[options.Workers];
        for (var i = 0; i < _partitions.Length; i++)
            _partitions[i] = new Partition(options, Connections, Producers, Join, _analysis.Post, Release, NowUs, ExportersActive);
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

    private void Release(long bytes) => Interlocked.Add(ref _charged, -bytes);

    private bool ExportersActive(long nowUs) => nowUs - Volatile.Read(ref _lastContextUs) < ExporterIdleUs;

    /// <summary>Handles one POSTed batch. Never blocks on the pipeline (03 §1: answer within 50 ms).</summary>
    public IngestResult Ingest(ReadOnlySpan<byte> body, string? contentEncoding, string? producerHeader)
    {
        if (contentEncoding is not { } enc || !enc.Contains("gzip", StringComparison.OrdinalIgnoreCase))
            return IngestPlain(body, producerHeader);
        var pool = System.Buffers.ArrayPool<byte>.Shared;
        byte[] compressed = pool.Rent(body.Length), inflated = pool.Rent(MaxBatchBytes + 1); // pooled: Ingest copies what it keeps
        try
        {
            body.CopyTo(compressed);
            int n;
            try { n = Gunzip(compressed.AsSpan(0, body.Length), inflated); }
            catch (InvalidDataException) { Interlocked.Increment(ref Batches400); return new IngestResult(400, 0, 0, 0, "bad gzip"); }
            if (n > MaxBatchBytes) { Interlocked.Increment(ref Batches413); return new IngestResult(413, 0, 0, 0, "batch over 1 MiB uncompressed"); }
            return IngestPlain(inflated.AsSpan(0, n), producerHeader);
        }
        finally
        {
            pool.Return(compressed);
            pool.Return(inflated);
        }
    }

    private IngestResult IngestPlain(ReadOnlySpan<byte> body, string? producerHeader)
    {
        if (body.Length > MaxBatchBytes) { Interlocked.Increment(ref Batches413); return new IngestResult(413, 0, 0, 0, "batch over 1 MiB"); }
        if (body.Count((byte)'\n') + (body.Length > 0 && body[^1] != (byte)'\n' ? 1 : 0) > MaxBatchRecords) { Interlocked.Increment(ref Batches413); return new IngestResult(413, 0, 0, 0, "batch over 2000 records"); }
        if (Volatile.Read(ref _charged) > Options.QueueBytes * 9 / 10) { Interlocked.Increment(ref Batches429); return new IngestResult(429, 0, 0, 0, "ingest queue above 90%"); }

        var now = NowUs();
        var maxTs = now + (long)Options.MaxFutureSkew.TotalMicroseconds;
        int accepted = 0, rejected = 0, ignored = 0, syntax = 0;
        var perPartition = new List<Record>?[_partitions.Length]; // one hand-off per partition per POST
        // Shared counters are written once per POST, not per record: the ingest thread writing a line per record that
        // the pipeline threads read per record made both 3-5x slower (measured: stage 1 at 20 µs a record beside
        // ingest, 6.5 µs alone)
        long charged = 0;
        ProducerInfo? producer = null;
        string? producerSrc = null;
        var producerRecords = 0;
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
            if (!ReferenceEquals(r.Src, producerSrc) && r.Src != producerSrc) // a batch is almost always one src
            {
                if (producer != null) Interlocked.Add(ref producer.Records, producerRecords);
                producer = Producers.Touch(r.Src, producerHeader, now);
                producerSrc = r.Src;
                producerRecords = 0;
            }
            producerRecords++;
            if (r.Ts > maxTs) // 03 §2: clamp, count per src
            {
                r.Ts = now;
                Interlocked.Increment(ref producer!.ClockSkewed);
                Interlocked.Increment(ref ClockSkewed);
            }
            if (r is OpRecord op && !RedactionHolds(op))
            {
                rejected++;
                Interlocked.Increment(ref producer!.Rejected);
                Interlocked.Increment(ref RedactionRejected);
                continue;
            }
            charged += Route(r, perPartition, now);
            accepted++;
        }
        if (producer != null) Interlocked.Add(ref producer.Records, producerRecords);
        if (charged > 0) Interlocked.Add(ref _charged, charged); // before the records are visible to the stages that release them
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

    /// <returns>the bytes the record is charged against the ingest queue (0 if it is not queued).</returns>
    private long Route(Record r, List<Record>?[] perPartition, long now)
    {
        switch (r)
        {
            case ConnRecord c: Connections.Apply(c); return 0;
            case ProxyRecord p: Producers.Apply(p); return 0;
            case StatsRecord s: Producers.Apply(s); return 0;
            case CmdRecord c:
            {
                ContextSeen(now);
                var p = Producers.Get(c.Src)!;
                Interlocked.Increment(ref p.Cmds);
                if (c.Key is not { } key) // 02 §3.3, §6.5: counted, never joinable
                {
                    Interlocked.Increment(ref Join.OrphanNoKey);
                    Interlocked.Increment(ref p.Orphans);
                    return 0;
                }
                (perPartition[PartitionFor(key.ProxyId, key.Member, key.ServerConnectionId)] ??= []).Add(c);
                return c.Size;
            }
            case HelloRecord h:
                ContextSeen(now);
                Producers.Apply(h);
                return 0;
            case CallsiteRecord cs:
                ContextSeen(now);
                _writer.Enqueue(cs);
                return 0;
            case ContextRecord: Interlocked.Increment(ref ContextRecords); return 0; // uow, pool: M3 (03 §5.3)
            case OpRecord op:
                (perPartition[PartitionFor(op.Src, op.Member, op.ServerConnectionId ?? -op.ConnSeq)] ??= []).Add(op);
                return op.Size;
            case StreamRecord s:
                (perPartition[PartitionFor(s.Src, s.Member, s.ServerConnectionId ?? -1)] ??= []).Add(s);
                return s.Size;
            default: return 0;
        }
    }

    /// <summary>Partitions read this per op: it is written at most once a second, not per exporter record.</summary>
    private void ContextSeen(long now)
    {
        if (now - Volatile.Read(ref _lastContextUs) > 1_000_000) Volatile.Write(ref _lastContextUs, now);
    }

    private int PartitionFor(string proxyId, string member, long sid) =>
        (int)((uint)HashCode.Combine(proxyId, member, sid) % (uint)_partitions.Length);

    /// <summary>03 §9 defence in depth: an auth command's body must be exactly the proxy's <c>{$redacted: name}</c>.</summary>
    private static bool RedactionHolds(OpRecord op)
    {
        if (!Shapes.ShapeNormalizer.IsRedactedCommand(op.Command)) return true;
        if (op.Cmd == null) return true;
        try
        {
            var d = Shapes.FastBson.Decode(op.Cmd);
            return d.ElementCount == 1 && d.GetElement(0).Name == "$redacted";
        }
        catch (Exception) { return false; }
    }

    /// <summary>Inflates into <paramref name="into"/> (MaxBatchBytes + 1 long); a result over MaxBatchBytes means too large.</summary>
    private static unsafe int Gunzip(ReadOnlySpan<byte> body, byte[] into)
    {
        fixed (byte* p = body)
        {
            using var gz = new GZipStream(new UnmanagedMemoryStream(p, body.Length), CompressionMode.Decompress);
            var n = 0;
            int read;
            while (n <= MaxBatchBytes && (read = gz.Read(into, n, MaxBatchBytes + 1 - n)) > 0) n += read;
            return n;
        }
    }

    /// <summary>
    /// Waits until everything ingested so far has gone through every stage, storage included, with join expiry and
    /// the analysis housekeeping run at the current clock. Each stage is FIFO, so a barrier per stage is exact.
    /// </summary>
    public async Task DrainAsync(TimeSpan timeout)
    {
        var all = Task.WhenAll(_partitions.Select(p => p.Barrier()));
        if (await Task.WhenAny(all, Task.Delay(timeout)).ConfigureAwait(false) != all) return;
        await _analysis.Invoke(() => { _analysis.Housekeeping(NowUs()); return 0; }).ConfigureAwait(false);
        await _writer.Barrier().ConfigureAwait(false);
    }

    public HealthReport Health()
    {
        var now = Environment.TickCount64;
        var checks = new List<HealthCheck>();
        string Age(long beat) => $"last turn {(now - Volatile.Read(ref beat)) / 1000.0:F1} s ago";
        void Stage(string name, bool alive, long beat, TimeSpan stall) =>
            checks.Add(new HealthCheck(name, alive && now - Volatile.Read(ref beat) < stall.TotalMilliseconds, true, alive ? Age(beat) : "thread not running"));

        for (var i = 0; i < _partitions.Length; i++) Stage($"partition-{i}", _partitions[i].Alive, _partitions[i].LastBeat, Options.HealthStageStall);
        Stage("analysis", _analysis.Alive, _analysis.LastBeat, Options.HealthStageStall);
        Stage("storage-writer", _writer.Alive, _writer.LastBeat, Options.HealthWriterStall);
        var failing = Volatile.Read(ref _writer.FailingSince);
        checks.Add(new HealthCheck("storage", failing == 0 || now - failing < Options.HealthStorageFailing.TotalMilliseconds, true,
            failing == 0 ? "writes succeeding" : $"writes failing for {(now - failing) / 1000.0:F0} s: {_writer.LastError}"));
        var queued = QueuedBytes;
        checks.Add(new HealthCheck("ingest-queue", queued <= Options.QueueBytes * 9 / 10, false,
            $"{queued * 100.0 / Options.QueueBytes:F1} % of {Options.QueueBytes / 1048576} MiB (429 above 90 %)"));
        checks.Add(new HealthCheck("storage-backlog", true, false,
            $"{_writer.Backlog} ops" + (_writer.Backlog > StorageWriter.Capacity / 2 ? ", sampling (03 §7)" : "")));
        var live = checks.All(c => c.Ok || !c.Liveness);
        return new HealthReport(live, live && checks.All(c => c.Ok), checks);
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
        var j = Join;
        M("spyglass_collector_join_total", "counter", "cmd records by 02 §6 outcome.",
            ("result=\"joined\"", j.Joined), ("result=\"joined_late\"", j.JoinedLate), ("result=\"conflict\"", j.Conflicts),
            ("result=\"orphan_no_key\"", j.OrphanNoKey), ("result=\"orphan_expired\"", j.OrphanExpired),
            ("result=\"late_discarded\"", j.LateDiscarded), ("result=\"already_joined\"", j.AlreadyJoined), ("result=\"duplicate\"", j.Duplicates));
        M("spyglass_collector_join_conflicts_total", "counter", "02 §6.3 join conflicts: same key, different command or db.", ("", j.Conflicts));
        M("spyglass_collector_join_waited_wire_only_total", "counter", "Ops stored without context after waiting for it.", ("", j.WaitedWireOnly));
        M("spyglass_collector_join_evicted_total", "counter", "Ops finalized early because the waiting set was full.", ("", j.EvictedOps));
        M("spyglass_collector_join_waiting", "gauge", "Records waiting for their counterpart.",
            ("record=\"op\"", Volatile.Read(ref j.PendingOps)), ("record=\"cmd\"", Volatile.Read(ref j.PendingCmds)));
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
        foreach (var p in _partitions) p.JoinThread();
        _analysis.Complete();
        _analysis.Join();
        _writer.Complete();
        _writer.Join(); // the stores belong to whoever created them
    }
}
