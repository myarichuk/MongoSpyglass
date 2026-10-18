using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using Spyglass.Proxy.Config;
using Spyglass.Proxy.Diagnostics;
using Spyglass.Proxy.Metrics;

namespace Spyglass.Proxy.Export;

/// <summary>
/// Ships NDJSON wire records to the collector (01 §11, 02 §5). One sender loop; the observer only enqueues
/// serialized lines into a byte-bounded queue. Delivery is at most once; nothing here can block forwarding.
/// </summary>
internal sealed class CollectorSender : IAsyncDisposable
{
    private readonly CollectorConfig _config;
    private readonly Uri _endpoint;
    private readonly string _src;
    private readonly HttpClient _http;
    private readonly ConcurrentQueue<byte[]> _queue = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _cts = new();
    private readonly Counter _dropped;
    private long _queuedBytes;
    private int _queuedRecords;
    private Task? _loop;
    private DateTime _pausedUntil = DateTime.MinValue;

    public long SentRecords, SentBatches, DroppedRecords;

    public Func<IEnumerable<byte[]>>? Periodic60s { get; set; }
    public Func<byte[]>? Stats10s { get; set; }

    public CollectorSender(CollectorConfig config, string src, Counter dropped, HttpMessageHandler? handler = null)
    {
        _config = config;
        _endpoint = new Uri(config.Endpoint!);
        _src = src;
        _dropped = dropped;
        _http = new HttpClient(handler ?? new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectTimeout = TimeSpan.FromSeconds(2),
        })
        {
            Timeout = TimeSpan.FromSeconds(5),
        };
    }

    public void Start() => _loop = Task.Run(LoopAsync);

    /// <summary>Called by observer workers. Never blocks.</summary>
    public void Enqueue(byte[] line)
    {
        if (Interlocked.Add(ref _queuedBytes, line.Length) > _config.BufferBytes || DateTime.UtcNow < _pausedUntil)
        {
            Interlocked.Add(ref _queuedBytes, -line.Length);
            Drop(1);
            return;
        }
        _queue.Enqueue(line);
        if (Interlocked.Increment(ref _queuedRecords) == _config.BatchMaxRecords)
            _signal.Release();
    }

    private void Drop(long n)
    {
        Interlocked.Add(ref DroppedRecords, n);
        _dropped.Add(n);
    }

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
        while (!_queue.IsEmpty)
        {
            var batch = TakeBatch();
            if (batch.Count == 0) return;
            if (DateTime.UtcNow < _pausedUntil)
            {
                Drop(batch.Count);
                continue;
            }
            await SendAsync(batch, splitAllowed: true, ct).ConfigureAwait(false);
            if (_queue.Count < _config.BatchMaxRecords && !ct.IsCancellationRequested)
                return; // wait for the next interval unless a full batch is ready
        }
    }

    private List<byte[]> TakeBatch()
    {
        var batch = new List<byte[]>();
        var bytes = 0L;
        while (batch.Count < _config.BatchMaxRecords && _queue.TryPeek(out var line))
        {
            if (bytes + line.Length + 1 > _config.BatchMaxBytes && batch.Count > 0) break;
            _queue.TryDequeue(out _);
            Interlocked.Add(ref _queuedBytes, -line.Length);
            Interlocked.Decrement(ref _queuedRecords);
            batch.Add(line);
            bytes += line.Length + 1;
        }
        return batch;
    }

    private async Task SendAsync(List<byte[]> batch, bool splitAllowed, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            HttpStatusCode? status = null;
            TimeSpan? retryAfter = null;
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, _endpoint) { Content = Body(batch) };
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
                Drop(batch.Count);
                return;
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
            {
                if (attempt == 0) Log.Debug($"collector: {e.Message}; retrying once");
            }

            switch ((int?)status)
            {
                case >= 200 and < 300:
                    Interlocked.Add(ref SentRecords, batch.Count);
                    Interlocked.Increment(ref SentBatches);
                    return;
                case 400 or 401 or 403:
                    Log.Warn($"collector rejected batch: {(int)status!}");
                    Drop(batch.Count);
                    return;
                case 413 when splitAllowed && batch.Count > 1:
                    var half = batch.Count / 2;
                    await SendAsync(batch.GetRange(0, half), splitAllowed: false, ct).ConfigureAwait(false);
                    await SendAsync(batch.GetRange(half, batch.Count - half), splitAllowed: false, ct).ConfigureAwait(false);
                    return;
                case 413:
                    Drop(batch.Count);
                    return;
                case 429:
                    var pause = retryAfter is { } ra ? TimeSpan.FromSeconds(Math.Min(ra.TotalSeconds, 10)) : TimeSpan.FromSeconds(1);
                    _pausedUntil = DateTime.UtcNow + pause;
                    Drop(batch.Count);
                    return;
            }

            // 5xx or network error: one retry after 1 s, then drop
            if (attempt == 0)
            {
                try { await Task.Delay(1000, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { Drop(batch.Count); return; }
            }
        }
        Drop(batch.Count);
    }

    private static ByteArrayContent Body(List<byte[]> batch)
    {
        var total = batch.Sum(b => b.Length + 1);
        var buf = new byte[total];
        var p = 0;
        foreach (var line in batch)
        {
            line.CopyTo(buf, p);
            p += line.Length;
            buf[p++] = (byte)'\n';
        }
        var content = new ByteArrayContent(buf);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/x-ndjson");
        return content;
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
}
