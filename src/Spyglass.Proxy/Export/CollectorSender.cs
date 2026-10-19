using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
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
}

/// <summary>
/// One producer's (an observer worker's) open chunk. Records are copied in as they are serialized; the
/// chunk goes to the sender when full or at the sender's flush. The lock is taken by the producer per
/// record and by the sender once per flush, so it is uncontended in practice.
/// </summary>
internal sealed class RecordBuffer
{
    private readonly CollectorSender _sender;
    private RecordChunk? _current;

    internal RecordBuffer(CollectorSender sender) => _sender = sender;

    /// <summary>Never blocks on I/O; drops (and counts) the record when the buffer bound is reached or the collector asked to pause.</summary>
    public void Append(ReadOnlySpan<byte> line)
    {
        if (_sender.Paused)
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
                _sender.Seal(c);
                c = _current = null;
            }
            if (c == null)
            {
                c = _current = _sender.Rent(need);
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
                _sender.Seal(c);
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
            if (c.Records > 0) _sender.Seal(c);
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
    private long _chargedBytes;
    private int _readyRecords;
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
        _http = new HttpClient(handler ?? new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectTimeout = TimeSpan.FromSeconds(2),
        })
        {
            Timeout = TimeSpan.FromSeconds(5),
        };
        _own = CreateBuffer();
    }

    internal int BatchMaxRecords => _config.BatchMaxRecords;
    internal bool Paused => Environment.TickCount64 < Volatile.Read(ref _pausedUntil);
    internal long ChargedBytes => Volatile.Read(ref _chargedBytes);

    /// <summary>A producer's buffer; one per observer worker.</summary>
    public RecordBuffer CreateBuffer()
    {
        var b = new RecordBuffer(this);
        lock (_buffers) _buffers.Add(b);
        return b;
    }

    /// <summary>Single-line convenience (periodic records, tests).</summary>
    public void Enqueue(ReadOnlySpan<byte> line) => _own.Append(line);

    public void Start() => _loop = Task.Run(LoopAsync);

    // ---- chunk pool -----------------------------------------------------------------------

    internal RecordChunk? Rent(int need)
    {
        var capacity = Math.Max(_chunkBytes, need);
        if (Interlocked.Add(ref _chargedBytes, capacity) > _config.BufferBytes)
        {
            Interlocked.Add(ref _chargedBytes, -capacity);
            return null;
        }
        if (capacity == _chunkBytes && _free.TryDequeue(out var c))
            return c;
        Interlocked.Increment(ref ChunksAllocated);
        return new RecordChunk(capacity);
    }

    internal void Release(RecordChunk c)
    {
        Interlocked.Add(ref _chargedBytes, -c.Buffer.Length);
        c.Length = c.Records = 0;
        if (c.Buffer.Length == _chunkBytes)
            _free.Enqueue(c); // the pool never exceeds bufferBytes / chunk: every pooled chunk was charged once
    }

    internal void Seal(RecordChunk c)
    {
        _ready.Enqueue(c);
        var before = Interlocked.Add(ref _readyRecords, c.Records) - c.Records;
        if (before < _config.BatchMaxRecords && before + c.Records >= _config.BatchMaxRecords)
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

            var now = DateTime.UtcNow;
            if (now >= nextPeriodic && Periodic60s != null)
            {
                foreach (var r in Periodic60s()) Enqueue(r);
                nextPeriodic = now.AddSeconds(60);
            }
            if (now >= nextStats && Stats10s != null)
            {
                Enqueue(Stats10s());
                nextStats = now.AddSeconds(10);
            }

            await FlushAsync(ct).ConfigureAwait(false);
        }
        // best-effort final flush
        using var final = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        try { await FlushAsync(final.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }

    private async Task FlushAsync(CancellationToken ct)
    {
        RecordBuffer[] buffers;
        lock (_buffers) buffers = _buffers.ToArray();
        foreach (var b in buffers)
            b.Flush();

        while (!_ready.IsEmpty)
        {
            var batch = TakeBatch();
            if (batch.Chunks.Count == 0) return;
            try
            {
                if (Paused)
                {
                    Drop(batch.Records);
                    continue;
                }
                await SendAsync(batch, splitAllowed: true, ct).ConfigureAwait(false);
            }
            finally
            {
                foreach (var c in batch.Chunks) Release(c);
            }
            if (Volatile.Read(ref _readyRecords) < _config.BatchMaxRecords && !ct.IsCancellationRequested)
                return; // wait for the next interval unless a full batch is ready
        }
    }

    private Batch TakeBatch()
    {
        var batch = new Batch();
        while (_ready.TryPeek(out var c))
        {
            if (batch.Chunks.Count > 0 &&
                (batch.Records + c.Records > _config.BatchMaxRecords || batch.Bytes + c.Length > _config.BatchMaxBytes))
                break;
            _ready.TryDequeue(out _);
            Interlocked.Add(ref _readyRecords, -c.Records);
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
                retryAfter = resp.Headers.RetryAfter?.Delta;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                Drop(batch.Records);
                return;
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
            {
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
                    await SendAsync(second, splitAllowed: false, ct).ConfigureAwait(false);
                    return;
                case 413:
                    Drop(batch.Records);
                    return;
                case 429:
                    var pause = retryAfter is { } ra ? TimeSpan.FromSeconds(Math.Min(ra.TotalSeconds, 10)) : TimeSpan.FromSeconds(1);
                    Volatile.Write(ref _pausedUntil, Environment.TickCount64 + (long)pause.TotalMilliseconds);
                    Drop(batch.Records);
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

        public void Add(RecordChunk c)
        {
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
