using MongoSpyglass.Service.Data;
using Microsoft.Extensions.Logging.Abstractions;
using Raven.Client.Documents;
using Raven.Embedded;
using Xunit;

namespace MongoSpyglass.Proxy.Tests;

public class DataTests : IDisposable
{
    private readonly RavenStorageService _storageService;
    private readonly IDocumentStore _store;

    public DataTests()
    {
        _storageService = new RavenStorageService(NullLogger<RavenStorageService>.Instance);
        _storageService.Initialize(isEmbedded: true, database: "TestDB", dataDir: "TestData");
        _store = EmbeddedServer.Instance.GetDocumentStore("TestDB");
    }

    [Fact]
    public async Task DeleteSessionAsync_ShouldDeleteSessionAndOperations()
    {
        // Arrange
        var mongoSession = await _storageService.StartNewSessionAsync("TestSession");
        string sessionId = mongoSession.Id;

        await _storageService.StoreOperationAsync(new MongoOperation { SessionId = sessionId, OpCode = "OP_MSG" });
        await _storageService.StoreOperationAsync(new MongoOperation { SessionId = sessionId, OpCode = "OP_QUERY" });

        // Wait a bit for the bulk worker or manually process if needed.
        // But for simplicity in tests, let's just insert them directly via a session to be sure they are there.
        using (var session = _store.OpenAsyncSession())
        {
            var op1 = new MongoOperation { SessionId = sessionId, OpCode = "OP_MSG" };
            var op2 = new MongoOperation { SessionId = sessionId, OpCode = "OP_QUERY" };
            await session.StoreAsync(op1);
            await session.StoreAsync(op2);
            await session.SaveChangesAsync();
        }

        // Verify they exist
        using (var session = _store.OpenAsyncSession())
        {
            var ops = await session.Query<MongoOperation>().Where(x => x.SessionId == sessionId).ToListAsync();
            Assert.Equal(2, ops.Count);
            var s = await session.LoadAsync<MongoSession>(sessionId);
            Assert.NotNull(s);
        }

        // Act
        await _storageService.DeleteSessionAsync(sessionId);

        // Assert
        using (var session = _store.OpenAsyncSession())
        {
            var ops = await session.Query<MongoOperation>().Where(x => x.SessionId == sessionId).ToListAsync();
            Assert.Empty(ops);
            var s = await session.LoadAsync<MongoSession>(sessionId);
            Assert.Null(s);
        }
    }

    [Fact]
    public async Task DeleteAllInsightsAsync_ShouldDeleteAllInsights()
    {
        // Arrange
        using (var session = _store.OpenAsyncSession())
        {
            await session.StoreAsync(new MongoInsight { Title = "Insight 1" });
            await session.StoreAsync(new MongoInsight { Title = "Insight 2" });
            await session.SaveChangesAsync();
        }

        // Act
        await _storageService.DeleteAllInsightsAsync();

        // Assert
        using (var session = _store.OpenAsyncSession())
        {
            var insights = await session.Query<MongoInsight>().ToListAsync();
            Assert.Empty(insights);
        }
    }

    public void Dispose()
    {
        _storageService.Dispose();
    }
}
