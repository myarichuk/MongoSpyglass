using Raven.Client.Documents;
using Raven.Embedded;
using System.Collections.Concurrent;
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

public class RavenStorageService : IDisposable
{
    private IDocumentStore? _store;
    private readonly ILogger<RavenStorageService> _logger;
    private string? _activeSessionId;

    public RavenStorageService(ILogger<RavenStorageService> logger)
    {
        _logger = logger;
    }

    public void Initialize(bool isEmbedded = true, string? remoteUrl = null, string database = "MongoSpyglass")
    {
        if (isEmbedded)
        {
            _logger.LogInformation("Starting Embedded RavenDB server...");
            EmbeddedServer.Instance.StartServer();
            _store = EmbeddedServer.Instance.GetDocumentStore(database);
        }
        else
        {
            _logger.LogInformation($"Connecting to Remote RavenDB at {remoteUrl}");
            _store = new DocumentStore
            {
                Urls = new[] { remoteUrl! },
                Database = database
            }.Initialize();
        }
    }

    public async Task<MongoSession> StartNewSessionAsync(string name)
    {
        if (_store == null) throw new InvalidOperationException("Store not initialized");

        using var session = _store.OpenAsyncSession();
        
        // Deactivate old sessions
        var activeSessions = await session.Query<MongoSession>().Where(x => x.IsActive).ToListAsync();
        foreach (var s in activeSessions) s.IsActive = false;

        var newSession = new MongoSession { Name = name };
        await session.StoreAsync(newSession);
        await session.SaveChangesAsync();

        _activeSessionId = newSession.Id;
        return newSession;
    }

    public async Task<List<MongoSession>> GetSessionsAsync()
    {
        if (_store == null) return new();
        using var session = _store.OpenAsyncSession();
        return await session.Query<MongoSession>().OrderByDescending(x => x.StartTime).ToListAsync();
    }

    public async Task StoreOperationAsync(MongoOperation op, byte[]? rawBson = null)
    {
        if (_store == null || string.IsNullOrEmpty(_activeSessionId)) return;

        using var session = _store.OpenAsyncSession();
        op.SessionId = _activeSessionId;
        await session.StoreAsync(op);

        if (rawBson != null)
        {
            using var ms = new MemoryStream(rawBson);
            session.Advanced.Attachments.Store(op.Id, "raw.bson", ms, "application/octet-stream");
        }

        await session.SaveChangesAsync();
    }

    public async Task DeleteSessionAsync(string sessionId)
    {
        if (_store == null) return;
        
        // In a real app we'd use a Patch or DeleteByQuery, but for simplicity:
        using var session = _store.OpenAsyncSession();
        var ops = await session.Query<MongoOperation>().Where(x => x.SessionId == sessionId).ToListAsync();
        foreach (var op in ops) session.Delete(op.Id);
        
        session.Delete(sessionId);
        await session.SaveChangesAsync();
    }

    public void Dispose()
    {
        _store?.Dispose();
    }
}
