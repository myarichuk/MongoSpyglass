using Raven.Client.Documents;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Queries;
using Raven.Embedded;
using System.Collections.Concurrent;
using System.Threading.Channels;
using MongoSpyglass.Proxy;
using Microsoft.IO;
using MongoSpyglass.Service.Analyzers.Rete;

namespace MongoSpyglass.Service.Data;
// ... (omitting MongoSession, MongoOperation, MongoInsight for brevity if replace handles it)
// Actually I'll just provide the whole file or a large enough chunk.

public class MongoSession
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public DateTime StartTime { get; set; } = DateTime.Now;
    public bool IsActive { get; set; } = true;
}

public record MongoOperation
{
    public string? Id { get; set; }
    public string SessionId { get; set; } = string.Empty;
    public int RequestId { get; set; }
    public string OpCode { get; set; } = string.Empty;
    public string Collection { get; set; } = string.Empty;
    public string Command { get; set; } = string.Empty;
    public double? DurationMs { get; set; }
    public int SizeBytes { get; set; }
    public int DocumentCount { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.Now;
}

public class MongoInsight
{
    public string? Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Level { get; set; } = "Info";
    public string Category { get; set; } = string.Empty;
    public string Details { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public bool IsRead { get; set; } = false;
}

public class RavenStorageService(ILogger<RavenStorageService> logger) : IDisposable
{
    private IDocumentStore? _store;
    private string? _activeSessionId;
    private readonly Channel<(MongoOperation Op, byte[]? Bson)> _bulkChannel = Channel.CreateBounded<(MongoOperation, byte[]?)>(10000);
    private Task? _bulkWorkerTask;
    private readonly CancellationTokenSource _bulkCts = new();
    private static readonly RecyclableMemoryStreamManager _streamManager = new();

    public event Action<string>? OnSessionChanged;

    public void Initialize(bool isEmbedded = true, string? remoteUrl = null, string database = "MongoSpyglass", string? dataDir = null)
    {
        if (isEmbedded)
        {
            logger.LogInformation("Starting Embedded RavenDB server...");
            if (!string.IsNullOrEmpty(dataDir))
            {
                EmbeddedServer.Instance.StartServer(new ServerOptions { DataDirectory = dataDir });
            }
            else
            {
                EmbeddedServer.Instance.StartServer();
            }
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

        // Resume last active session if available
        using (var session = _store.OpenSession())
        {
            var lastSession = session.Query<MongoSession>()
                .OrderByDescending(x => x.StartTime)
                .FirstOrDefault();

            if (lastSession != null)
            {
                _activeSessionId = lastSession.Id;
                logger.LogInformation($"Resumed session: {lastSession.Name} ({lastSession.Id})");
            }
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
        OnSessionChanged?.Invoke(newSession.Id);
        return newSession;
    }

    public async Task<MongoSession?> SwitchSessionAsync(string sessionId)
    {
        if (_store == null) throw new InvalidOperationException("Store not initialized");

        using var session = _store.OpenAsyncSession();
        
        // Deactivate old sessions
        var activeSessions = await session.Query<MongoSession>().Where(x => x.IsActive).ToListAsync();
        foreach (var s in activeSessions)
        {
            s.IsActive = false;
        }

        var newActiveSession = await session.LoadAsync<MongoSession>(sessionId);
        if (newActiveSession != null)
        {
            newActiveSession.IsActive = true;
            _activeSessionId = newActiveSession.Id;
            OnSessionChanged?.Invoke(newActiveSession.Id);
        }
        
        await session.SaveChangesAsync();
        return newActiveSession;
    }

    public async Task<List<MongoInsight>> GetInsightsAsync()
    {
        if (_store == null) return new();
        using var session = _store.OpenAsyncSession();
        return await session.Query<MongoInsight>().OrderByDescending(x => x.Timestamp).ToListAsync();
    }

    public async Task StoreInsightAsync(MongoInsight insight)
    {
        if (_store == null) return;
        using var session = _store.OpenAsyncSession();
        await session.StoreAsync(insight);
        await session.SaveChangesAsync();
    }

    public async Task UpdateInsightAsync(MongoInsight insight)
    {
        if (_store == null || string.IsNullOrEmpty(insight.Id)) return;
        using var session = _store.OpenAsyncSession();
        await session.StoreAsync(insight, insight.Id);
        await session.SaveChangesAsync();
    }

    public async Task DeleteInsightAsync(string? id)
    {
        if (_store == null || id == null) return;
        using var session = _store.OpenAsyncSession();
        session.Delete(id);
        await session.SaveChangesAsync();
    }

    public async Task DeleteAllInsightsAsync()
    {
        if (_store == null) return;
        using var session = _store.OpenAsyncSession();
        var insights = await session.Query<MongoInsight>().ToListAsync();
        foreach (var insight in insights)
        {
            session.Delete(insight.Id);
        }
        await session.SaveChangesAsync();
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

    public async Task<List<(MongoOperation Op, byte[]? Bson)>> GetLatestOperationsAsync(string sessionId, int limit = 1000)
    {
        if (_store == null) return new();
        using var session = _store.OpenAsyncSession();
        var ops = await session.Query<MongoOperation>()
            .Where(x => x.SessionId == sessionId)
            .OrderByDescending(x => x.Timestamp)
            .Take(limit)
            .ToListAsync();

        var result = new List<(MongoOperation Op, byte[]? Bson)>();
        foreach (var op in ops)
        {
            byte[]? bson = null;
            try
            {
                using var attachment = await session.Advanced.Attachments.GetAsync(op.Id, "raw.bson");
                if (attachment != null)
                {
                    using var ms = _streamManager.GetStream();
                    await attachment.Stream.CopyToAsync(ms);
                    bson = ms.ToArray();
                }
            }
            catch { }
            result.Add((op, bson));
        }
        return result;
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
                if (!await _bulkChannel.Reader.WaitToReadAsync(ct)) break;

                if (_store == null)
                {
                    await Task.Delay(1000, ct);
                    continue;
                }

                // Batch items for bulk insert
                var items = new List<(MongoOperation Op, byte[]? Bson)>();
                for (int i = 0; i < 512 && _bulkChannel.Reader.TryRead(out var item); i++)
                {
                    items.Add(item);
                }

                if (items.Count == 0) continue;

                using (var bulk = _store.BulkInsert())
                {
                    foreach (var item in items)
                    {
                        item.Op.SessionId = _activeSessionId ?? "default";
                        await bulk.StoreAsync(item.Op);
                    }
                }

                // Attachments MUST be stored via standard session as BulkInsert doesn't support them
                // We do this in parallel to not block the next bulk batch
                _ = Task.Run(async () => {
                    var streams = new List<MemoryStream>();
                    try {
                        using var session = _store.OpenAsyncSession();
                        foreach (var item in items)
                        {
                            if (item.Bson != null && item.Bson.Length > 0 && item.Op.Id != null)
                            {
                                var ms = _streamManager.GetStream(item.Bson);
                                streams.Add(ms);
                                session.Advanced.Attachments.Store(item.Op.Id, "raw.bson", ms);
                            }
                        }
                        await session.SaveChangesAsync();
                    } catch (Exception ex) {
                        logger.LogError(ex, "Error storing attachments");
                    } finally {
                        foreach (var s in streams) s.Dispose();
                    }
                }, CancellationToken.None);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, $"Bulk insert error: {ex.Message}");
                await Task.Delay(1000, ct);
            }
        }
    }

    public async Task DeleteSessionAsync(string sessionId)
    {
        if (_store == null) return;

        // Use DeleteByQuery to avoid loading changed entities
        var operation = await _store.Operations.SendAsync(new Raven.Client.Documents.Operations.DeleteByQueryOperation(new Raven.Client.Documents.Queries.IndexQuery {
            Query = $"from MongoOperations where SessionId = '{sessionId}'"
        }));
        await operation.WaitForCompletionAsync();

        using var session = _store.OpenAsyncSession();
        session.Delete(sessionId);
        await session.SaveChangesAsync();

        if (_activeSessionId == sessionId)
        {
            _activeSessionId = null;
            OnSessionChanged?.Invoke(string.Empty);
        }
    }

    public async Task<AppSettings?> GetSettingsAsync()
    {
        if (_store == null) return null;
        using var session = _store.OpenAsyncSession();
        return await session.LoadAsync<AppSettings>("AppSettings/Default");
    }

    public async Task SaveSettingsAsync(AppSettings settings)
    {
        if (_store == null) return;
        using var session = _store.OpenAsyncSession();
        await session.StoreAsync(settings, "AppSettings/Default");
        await session.SaveChangesAsync();
    }

    public string? ActiveSessionId => _activeSessionId;

    public async Task StoreCursorAsync(CursorFact cursor)
    {
        if (_store == null) return;
        using var session = _store.OpenAsyncSession();
        cursor.SessionId = _activeSessionId ?? "default";
        await session.StoreAsync(cursor, cursor.RavenId);
        await session.SaveChangesAsync();
        cursor.RavenId = session.Advanced.GetDocumentId(cursor);
    }

    public async Task<List<CursorFact>> GetActiveCursorsAsync(string sessionId)
    {
        if (_store == null) return new();
        using var session = _store.OpenAsyncSession();
        return await session.Query<CursorFact>()
            .Where(x => x.SessionId == sessionId && !x.IsClosed)
            .ToListAsync();
    }

    public async Task StoreCursorStatsAsync(CursorStatsFact stats)
    {
        if (_store == null) return;
        using var session = _store.OpenAsyncSession();
        stats.SessionId = _activeSessionId ?? "default";
        string statsId = $"CursorStats/{stats.SessionId}";
        await session.StoreAsync(stats, statsId);
        await session.SaveChangesAsync();
        stats.RavenId = statsId;
    }

    public async Task<CursorStatsFact?> GetLatestCursorStatsAsync(string sessionId)
    {
        if (_store == null) return null;
        using var session = _store.OpenAsyncSession();
        return await session.LoadAsync<CursorStatsFact>($"CursorStats/{sessionId}");
    }

    public void Dispose()
    {
        _bulkCts.Cancel();
        try { _bulkWorkerTask?.Wait(2000); } catch { }
        _bulkCts.Dispose();
        _store?.Dispose();
    }
}
