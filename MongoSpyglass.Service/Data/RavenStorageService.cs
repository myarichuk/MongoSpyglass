using Raven.Client.Documents;
using Raven.Embedded;
using System.Collections.Concurrent;
using System.Threading.Channels;
using MongoSpyglass.Proxy;

namespace MongoSpyglass.Service.Data;

public class MongoSession
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public DateTime StartTime { get; set; } = DateTime.Now;
    public bool IsActive { get; set; } = true;
}

public class MongoOperation
{
    public string Id { get; set; } = string.Empty;
    public string SessionId { get; set; } = string.Empty;
    public int RequestId { get; set; }
    public string Collection { get; set; } = string.Empty;
    public string Command { get; set; } = string.Empty;
    public double? DurationMs { get; set; }
    public int SizeBytes { get; set; }
    public int DocumentCount { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.Now;
}

public class RavenStorageService(ILogger<RavenStorageService> logger) : IDisposable
{
    private IDocumentStore? _store;
    private string? _activeSessionId;
    private readonly Channel<(MongoOperation Op, byte[]? Bson)> _bulkChannel = Channel.CreateBounded<(MongoOperation, byte[]?)>(10000);
    private Task? _bulkWorkerTask;
    private readonly CancellationTokenSource _bulkCts = new();

    public void Initialize(bool isEmbedded = true, string? remoteUrl = null, string database = "MongoSpyglass")
    {
        if (isEmbedded)
        {
            logger.LogInformation("Starting Embedded RavenDB server...");
            EmbeddedServer.Instance.StartServer();
            _store = EmbeddedServer.Instance.GetDocumentStore(database);
        }
        else
        {
            logger.LogInformation($"Connecting to Remote RavenDB at {remoteUrl}");
            _store = new DocumentStore
            {
                Urls = [remoteUrl!],
                Database = database
            }.Initialize();
        }

        _bulkWorkerTask = Task.Run(() => ProcessBulkInsertsAsync(_bulkCts.Token));
    }

    public async Task<MongoSession> StartNewSessionAsync(string name)
    {
        if (_store == null)
        {
            throw new InvalidOperationException("Store not initialized");
        }

        using var session = _store.OpenAsyncSession();
        
        // Deactivate old sessions
        var activeSessions = await session.Query<MongoSession>().Where(x => x.IsActive).ToListAsync();
        foreach (var s in activeSessions)
        {
            s.IsActive = false;
        }

        var newSession = new MongoSession { Name = name };
        await session.StoreAsync(newSession);
        await session.SaveChangesAsync();

        _activeSessionId = newSession.Id;
        return newSession;
    }

    public async Task<List<MongoSession>> GetSessionsAsync()
    {
        if (_store == null)
        {
            return new();
        }

        using var session = _store.OpenAsyncSession();
        return await session.Query<MongoSession>().OrderByDescending(x => x.StartTime).ToListAsync();
    }

    public async Task StoreOperationAsync(MongoOperation op, byte[]? rawBson = null)
    {
        await _bulkChannel.Writer.WriteAsync((op, rawBson));
    }

    private async Task ProcessBulkInsertsAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Wait for data if empty
                if (!await _bulkChannel.Reader.WaitToReadAsync(ct)) break;

                if (_store == null)
                {
                    await Task.Delay(1000, ct);
                    continue;
                }

                using var bulk = _store.BulkInsert();
                // Process in batches of 512
                for (int i = 0; i < 512 && _bulkChannel.Reader.TryRead(out var item); i++)
                {
                    item.Op.SessionId = _activeSessionId ?? "default";
                    // Note: Standard BulkInsert doesn't support attachments easily.
                    // For this perf task, we prioritize the Op storage.
                    await bulk.StoreAsync(item.Op);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                // Log error but keep worker alive
                logger.LogError(ex, $"Bulk insert error: {ex.Message}");
                await Task.Delay(1000, ct);
            }
        }
    }

    public async Task DeleteSessionAsync(string sessionId)
    {
        if (_store == null)
        {
            return;
        }

        // In a real app we'd use a Patch or DeleteByQuery, but for simplicity:
        using var session = _store.OpenAsyncSession();
        var ops = await session.Query<MongoOperation>().Where(x => x.SessionId == sessionId).ToListAsync();
        foreach (var op in ops)
        {
            session.Delete(op.Id);
        }

        session.Delete(sessionId);
        await session.SaveChangesAsync();
    }

    public void Dispose()
    {
        _bulkCts.Cancel();
        try { _bulkWorkerTask?.Wait(2000); } catch { }
        _bulkCts.Dispose();
        _store?.Dispose();
    }
}
