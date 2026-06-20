using BenchmarkDotNet.Attributes;
using MongoSpyglass.Service.Data;
using Microsoft.Extensions.Logging.Abstractions;
using Raven.Client.Documents;
using Raven.Embedded;
using System;
using System.Threading.Tasks;

namespace Benchmarks;

[MemoryDiagnoser]
public class DeleteSessionBenchmark
{
    private RavenStorageService _storageService;
    private string _sessionId;

    [GlobalSetup]
    public void Setup()
    {
        _storageService = new RavenStorageService(NullLogger<RavenStorageService>.Instance);
        _storageService.Initialize(isEmbedded: true, database: "BenchmarkDB", dataDir: "BenchmarkData");
    }

    [IterationSetup]
    public void IterationSetup()
    {
        var session = _storageService.StartNewSessionAsync("TestSession").GetAwaiter().GetResult();
        _sessionId = session.Id;

        var store = EmbeddedServer.Instance.GetDocumentStore("BenchmarkDB");
        using var bulk = store.BulkInsert();
        for (int i = 0; i < 1000; i++)
        {
            bulk.Store(new MongoOperation
            {
                SessionId = _sessionId,
                RequestId = i,
                OpCode = "OP_MSG",
                Collection = "test.collection",
                Command = "{ \"find\": \"test\" }",
                Timestamp = DateTime.Now
            });
        }
    }

    [Benchmark]
    public async Task DeleteSession()
    {
        await _storageService.DeleteSessionAsync(_sessionId);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _storageService.Dispose();
    }
}
