using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;
using Spyglass.Proxy.Forwarding;
using Spyglass.Proxy.Config;
using Spyglass.Proxy.Diagnostics;
using Spyglass.Proxy.Metrics;

namespace Spyglass.Proxy.Export;

/// <summary>A run of complete NDJSON lines, each <c>\n</c>-terminated. Pooled by the sender.</summary>
internal sealed class RecordChunk(int capacity)
{
    public readonly byte[] Buffer = new byte[capacity];
    public int Length;
    public int Records;
    /// <summary>Holds <c>conn</c> lifecycle records: survives a 429 pause (PLAN 1.2) and is charged to the reserved slice.</summary>
    public bool Reserved;
}

/// <summary>
/// One producer's (an observer worker's) open chunk. Records are copied in as they are serialized; the
/// chunk goes to the sender when full or at the sender's flush. The lock is taken by the producer per
/// record and by the sender once per flush, so it is uncontended in practice.
/// </summary>
internal sealed class RecordBuffer
{
    private readonly CollectorSender _sender;
    private readonly bool _reserved;
    private readonly RecordBuffer? _before;
    internal bool IsReserved => _reserved;
    private RecordChunk? _current;

    /// <param name="before">
    /// a buffer whose records must reach the collector ahead of this one's: the worker's <c>conn</c> buffer, sealed before
    /// any op chunk of the same worker so a connection's open record (its app, driver) never arrives after its ops
    /// </param>
    internal RecordBuffer(CollectorSender sender, bool reserved = false, RecordBuffer? before = null)
    {
        _sender = sender;
        _reserved = reserved;
        _before = before;
    }

    private void Seal(RecordChunk c)
    {
        _before?.Flush(); // lock order: an op buffer may take its conn buffer's lock, never the reverse
        _sender.Seal(c);
    }

    /// <summary>Never blocks on I/O; drops (and counts) the record when the buffer bound is reached or the collector asked to pause.</summary>
    public void Append(ReadOnlySpan<byte> line)
    {
        if (!_reserved && _sender.Paused)
        {
            _sender.Drop(1);
            return;
        }
        var need = line.Length + 1;
        lock (this)
        {
            var c = _current;
            if (c != null && c.Length + need > c.Buffer.Length)
            {
                Seal(c);
                c = _current = null;
            }
            if (c == null)
            {
                c = _current = _sender.Rent(need, _reserved);
                if (c == null)
                {
                    _sender.Drop(1);
                    return;
                }
            }
            line.CopyTo(c.Buffer.AsSpan(c.Length));
            c.Length += line.Length;
            c.Buffer[c.Length++] = (byte)'\n';
            if (++c.Records >= _sender.BatchMaxRecords)
            {
                Seal(c);
                _current = null;
            }
        }
    }

    /// <summary>Sender side: hands over the partially filled chunk.</summary>
    internal void Flush()
    {
        RecordChunk? c;
        lock (this)
        {
            c = _current;
            _current = null;
        }
        if (c != null)
        {
            if (c.Records > 0) Seal(c);
            else _sender.Release(c);
        }
    }
}

/// <summary>
/// Ships NDJSON wire records to the collector (01 §11, 02 §5). One sender loop; observer workers append
/// serialized lines to their <see cref="RecordBuffer"/>, which fills pooled chunks; a batch is a list of
/// chunk segments posted without concatenation. Bounded by <c>collector.bufferBytes</c> (charged per
/// chunk). Delivery is at most once; nothing here can block forwarding.
/// </summary>
internal sealed class CollectorSender : IAsyncDisposable
{
    /// <summary>Pooled chunk size; a record larger than this gets a chunk of its own, not pooled.</summary>
    public const int ChunkBytes = 64 * 1024;

    private readonly CollectorConfig _config;
    private readonly Uri _endpoint;
    private readonly string _src;
    private readonly HttpClient _http;
    private readonly ConcurrentQueue<RecordChunk> _ready = new();
    private readonly ConcurrentQueue<RecordChunk> _free = new();
    private readonly List<RecordBuffer> _buffers = [];
    private readonly RecordBuffer _own;
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _cts = new();
    private readonly Counter _dropped;
    private readonly int _chunkBytes;
    private long _chargedBytes, _reservedBytes;
    private readonly long _reservedBound;
    private int _readyRecords;
    private long _readyBytes;
    /// <summary>Conn chunks that outlived a 429 pause, oldest first; only the sender loop touches it. Sent ahead of anything sealed later.</summary>
    private List<RecordChunk> _held = [];
    private long _pausedUntil; // Environment.TickCount64
    private Task? _loop;

    public long SentRecords, SentBatches, DroppedRecords;
    /// <summary>Chunks created (not reused from the pool): flat in steady state.</summary>
    internal long ChunksAllocated;

    public Func<IEnumerable<byte[]>>? Periodic60s { get; set; }
    public Func<byte[]>? Stats10s { get; set; }

    public CollectorSender(CollectorConfig config, string src, Counter dropped, HttpMessageHandler? handler = null)
    {
        _config = config;
        _endpoint = new Uri(config.Endpoint!);
        _src = src;
        _dropped = dropped;
        _chunkBytes = (int)Math.Min(ChunkBytes, Math.Max(1024, config.BatchMaxBytes));
        _reservedBound = Math.Max(_chunkBytes, config.BufferBytes / 16); // PLAN 1.2: conn records' own slice, on top of the op bound
        _http = new HttpClient(handler ?? CreateHandler(config))
        {
            Timeout = TimeSpan.FromSeconds(5),
        };
        _own = CreateBuffer();
    }

    private static SocketsHttpHandler CreateHandler(CollectorConfig config)
    {
        var h = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectTimeout = TimeSpan.FromSeconds(2),
        };
        // Custom trust for an https endpoint: same chain logic as upstream.tls.caFile (TlsTrust).
        if (config.CaFile != null || config.AllowInvalidCertificates)
        {
            var trust = config.CaFile != null ? TlsTrust.LoadCaFile(config.CaFile, "collector.caFile") : null;
            var insecure = config.AllowInvalidCertificates;
            h.SslOptions.RemoteCertificateValidationCallback = (_, cert, chain, errors) =>
                TlsTrust.Validate("collector TLS", "collector.caFile", cert, chain, errors, trust, insecure, allowInvalidHostnames: false, X509RevocationMode.NoCheck);
        }
        return h;
    }

    internal int BatchMaxRecords => _config.BatchMaxRecords;
    internal bool Paused => Environment.TickCount64 < Volatile.Read(ref _pausedUntil);
    internal long ChargedBytes => Volatile.Read(ref _chargedBytes) + Volatile.Read(ref _reservedBytes);
    internal long PausedForMs => Math.Max(0, Volatile.Read(ref _pausedUntil) - Environment.TickCount64);

    /// <summary>A producer's buffer; one per observer worker. <paramref name="reserved"/>: <c>conn</c> records, kept through a 429 pause.</summary>
    public RecordBuffer CreateBuffer(bool reserved = false, RecordBuffer? before = null)
    {
        var b = new RecordBuffer(this, reserved, before);
        lock (_buffers) _buffers.Add(b);
        return b;
    }

    /// <summary>Single-line convenience (periodic records, tests).</summary>
    public void Enqueue(ReadOnlySpan<byte> line) => _own.Append(line);

    public void Start() => _loop = Task.Run(LoopAsync);

    // ---- chunk pool -----------------------------------------------------------------------

    internal RecordChunk? Rent(int need, bool reserved = false)
    {
        var capacity = Math.Max(_chunkBytes, need);
        if (Interlocked.Add(ref reserved ? ref _reservedBytes : ref _chargedBytes, capacity) > (reserved ? _reservedBound : _config.BufferBytes))
        {
            Interlocked.Add(ref reserved ? ref _reservedBytes : ref _chargedBytes, -capacity);
            return null;
        }
        if (capacity == _chunkBytes && _free.TryDequeue(out var c))
        {
            c.Reserved = reserved;
            return c;
        }
        Interlocked.Increment(ref ChunksAllocated);
        return new RecordChunk(capacity) { Reserved = reserved };
    }

    internal void Release(RecordChunk c)
    {
        Interlocked.Add(ref c.Reserved ? ref _reservedBytes : ref _chargedBytes, -c.Buffer.Length);
        c.Length = c.Records = 0;
        if (c.Buffer.Length == _chunkBytes)
            _free.Enqueue(c); // the pool never exceeds bufferBytes / chunk: every pooled chunk was charged once
    }

    internal void Seal(RecordChunk c)
    {
        _ready.Enqueue(c);
        var before = Interlocked.Add(ref _readyRecords, c.Records) - c.Records;
        var beforeBytes = Interlocked.Add(ref _readyBytes, c.Length) - c.Length;
        // wake the loop when either batch limit is crossed (large records never reach the record limit: the buffer bound drops first)
        if ((before < _config.BatchMaxRecords && before + c.Records >= _config.BatchMaxRecords) ||
            (beforeBytes < _config.BatchMaxBytes && beforeBytes + c.Length >= _config.BatchMaxBytes))
            _signal.Release();
    }

    internal void Drop(long n)
    {
        Interlocked.Add(ref DroppedRecords, n);
        _dropped.Add(n);
    }

    // ---- loop ----------------------------------------------------------------------------

    private async Task LoopAsync()
    {
        var ct = _cts.Token;
        var nextStats = DateTime.UtcNow.AddSeconds(10);
        var nextPeriodic = DateTime.UtcNow; // proxy record at start
        while (!ct.IsCancellationRequested)
        {
            try { await _signal.WaitAsync(_config.FlushIntervalMs, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }

            try
            {
                var now = DateTime.UtcNow;
                if (now >= nextPeriodic && Periodic60s != null)
                {
                    nextPeriodic = now.AddSeconds(60);
                    foreach (var r in Periodic60s()) Enqueue(r);
                }
                if (now >= nextStats && Stats10s != null)
                {
                    nextStats = now.AddSeconds(10);
                    Enqueue(Stats10s());
                }

                await FlushAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception e)
            {
                // a custom handler, a record builder bug: export must outlive it
                Log.Warn($"collector sender: {e.GetType().Name}: {e.Message}");
            }
        }
        // best-effort final flush
        using var final = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        try { await FlushAsync(final.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception e) { Log.Warn($"collector sender: final flush: {e.GetType().Name}: {e.Message}"); }
        // whatever is left (still paused, flush cut short) is lost: count it, so delivered + dropped stays whole
        foreach (var c in _held) { Drop(c.Records); Release(c); }
        _held.Clear();
        while (_ready.TryDequeue(out var c))
        {
            Interlocked.Add(ref _readyRecords, -c.Records);
            Interlocked.Add(ref _readyBytes, -c.Length);
            Drop(c.Records);
            Release(c);
        }
    }

    private async Task FlushAsync(CancellationToken ct)
    {
        RecordBuffer[] buffers;
        lock (_buffers) buffers = _buffers.ToArray();
        // conn buffers first, then the op buffers: every connection's open record is sealed ahead of the ops that
        // came with it, and the batches stay full instead of alternating conn | op | conn | op (FP-7)
        foreach (var b in buffers)
            if (b.IsReserved) b.Flush();
        foreach (var b in buffers)
            if (!b.IsReserved) b.Flush();

        // Drain everything sealed so far: batches are size-capped already, and sending one per interval capped
        // export at batchMaxBytes per flushIntervalMs (records over ~bufferBytes/batchMaxRecords never reach the record limit).
        // Chunks held through an earlier pause go first (a connection's open record before the ops sealed since); the ones
        // that are held again, or newly held, become the next flush's carry.
        var carry = _held;
        var nextHeld = new List<RecordChunk>();
        var carryAt = 0;
        try
        {
            while (carryAt < carry.Count || !_ready.IsEmpty)
            {
                var batch = TakeBatch(carry, ref carryAt);
                if (batch.Chunks.Count == 0) return;
                try
                {
                    if (Paused)
                        DropForPause(batch);
                    else
                        await SendAsync(batch, splitAllowed: batch.ReservedRecords == 0, ct).ConfigureAwait(false);
                }
                finally
                {
                    foreach (var c in batch.Chunks)
                    {
                        if (batch.Retain && c.Reserved) nextHeld.Add(c);
                        else Release(c);
                    }
                }
            }
        }
        finally
        {
            // an exception leaves the rest of the carry where it was
            for (; carryAt < carry.Count; carryAt++) nextHeld.Add(carry[carryAt]);
            _held = nextHeld;
        }
    }

    /// <summary>A 429 pause: op records are dropped, conn records ride it out (PLAN 1.2: tiny and few, resent after the pause).</summary>
    private void DropForPause(Batch batch)
    {
        if (batch.ReservedRecords > 0) batch.Retain = true;
        Drop(batch.Records - batch.ReservedRecords);
    }

    private Batch TakeBatch(List<RecordChunk> carry, ref int carryAt)
    {
        var batch = new Batch();
        while (true)
        {
            RecordChunk? c;
            var fromCarry = carryAt < carry.Count;
            if (fromCarry) c = carry[carryAt];
            else if (!_ready.TryPeek(out c)) break;
            if (batch.Chunks.Count > 0 &&
                (batch.Records + c.Records > _config.BatchMaxRecords || batch.Bytes + c.Length > _config.BatchMaxBytes))
                break;
            if (fromCarry) carryAt++;
            else
            {
                _ready.TryDequeue(out _);
                Interlocked.Add(ref _readyRecords, -c.Records);
                Interlocked.Add(ref _readyBytes, -c.Length);
            }
            batch.Add(c);
        }
        return batch;
    }

    private async Task SendAsync(Batch batch, bool splitAllowed, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            HttpStatusCode? status = null;
            TimeSpan? retryAfter = null;
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, _endpoint) { Content = new SegmentsContent(batch.Segments, batch.Bytes) };
                if (_config.Token is { Length: > 0 } token)
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                req.Headers.Add("X-Spyglass-Producer", "proxy");
                req.Headers.Add("X-Spyglass-Src", _src);
                using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
                status = resp.StatusCode;
                var ra = resp.Headers.RetryAfter; // delta-seconds or an HTTP-date (RFC 9110 §10.2.3)
                retryAfter = ra?.Delta ?? (ra?.Date is { } date ? date - DateTimeOffset.UtcNow : null);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                Drop(batch.Records);
                return;
            }
            catch (Exception e)
            {
                // network errors, but also anything a handler throws: the batch is retried once, then dropped and counted
                if (attempt == 0) Log.Debug($"collector: {e.Message}; retrying once");
            }

            switch ((int?)status)
            {
                case >= 200 and < 300:
                    Interlocked.Add(ref SentRecords, batch.Records);
                    Interlocked.Increment(ref SentBatches);
                    return;
                case 400 or 401 or 403:
                    Log.Warn($"collector rejected batch: {(int)status!}");
                    Drop(batch.Records);
                    return;
                case 413 when splitAllowed && batch.Records > 1:
                    var (first, second) = batch.Split();
                    await SendAsync(first, splitAllowed: false, ct).ConfigureAwait(false);
                    if (Paused) // the first half drew a 429
                        Drop(second.Records);
                    else
                        await SendAsync(second, splitAllowed: false, ct).ConfigureAwait(false);
                    return;
                case 413:
                    Drop(batch.Records);
                    return;
                case 429:
                    var pause = retryAfter is { } wait ? TimeSpan.FromSeconds(Math.Clamp(wait.TotalSeconds, 0, 10)) : TimeSpan.FromSeconds(1);
                    Volatile.Write(ref _pausedUntil, Environment.TickCount64 + (long)pause.TotalMilliseconds);
                    DropForPause(batch);
                    return;
            }

            // 5xx or network error: one retry after 1 s, then drop
            if (attempt == 0)
            {
                try { await Task.Delay(1000, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { Drop(batch.Records); return; }
            }
        }
        Drop(batch.Records);
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_loop != null)
        {
            try { await _loop.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
            catch (TimeoutException) { }
        }
        _http.Dispose();
        _cts.Dispose();
    }

    // ---- batches -------------------------------------------------------------------------

    internal readonly record struct Segment(byte[] Buffer, int Offset, int Length, int Records);

    /// <summary>Segments of chunks; the chunks themselves are released by the flush that took them.</summary>
    internal sealed class Batch
    {
        public readonly List<RecordChunk> Chunks = [];
        public readonly List<Segment> Segments = [];
        public int Records, Bytes;
        /// <summary>Records in conn chunks (<see cref="RecordChunk.Reserved"/>): they come first in a batch and survive a 429 pause.</summary>
        public int ReservedRecords;
        /// <summary>The conn chunks are held for the next flush instead of released (429 pause).</summary>
        public bool Retain;

        public void Add(RecordChunk c)
        {
            if (c.Reserved) ReservedRecords += c.Records;
            Chunks.Add(c);
            Add(new Segment(c.Buffer, 0, c.Length, c.Records));
        }

        private void Add(Segment s)
        {
            Segments.Add(s);
            Records += s.Records;
            Bytes += s.Length;
        }

        /// <summary>Two halves by records (413). Within one segment, at the line boundary nearest its middle.</summary>
        public (Batch, Batch) Split()
        {
            Batch a = new(), b = new();
            if (Segments.Count > 1)
            {
                var half = Records / 2;
                var into = a;
                foreach (var s in Segments)
                {
                    if (into == a && a.Segments.Count > 0 && a.Records + s.Records > half) into = b; // order kept
                    into.Add(s);
                }
                return (a, b);
            }
            var only = Segments[0];
            var span = only.Buffer.AsSpan(only.Offset, only.Length);
            var mid = span[..(only.Length / 2)].LastIndexOf((byte)'\n') + 1;
            if (mid <= 0) mid = span.IndexOf((byte)'\n') + 1; // first line is longer than half
            var left = span[..mid].Count((byte)'\n');
            a.Add(new Segment(only.Buffer, only.Offset, mid, left));
            b.Add(new Segment(only.Buffer, only.Offset + mid, only.Length - mid, only.Records - left));
            return (a, b);
        }
    }

    /// <summary>The batch body: its segments written in order, no concatenation.</summary>
    private sealed class SegmentsContent : HttpContent
    {
        private readonly List<Segment> _segments;
        private readonly long _length;

        public SegmentsContent(List<Segment> segments, long length)
        {
            _segments = segments;
            _length = length;
            Headers.ContentType = new MediaTypeHeaderValue("application/x-ndjson");
        }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken ct)
        {
            foreach (var s in _segments)
                await stream.WriteAsync(s.Buffer.AsMemory(s.Offset, s.Length), ct).ConfigureAwait(false);
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override bool TryComputeLength(out long length)
        {
            length = _length;
            return true;
        }
    }
}
