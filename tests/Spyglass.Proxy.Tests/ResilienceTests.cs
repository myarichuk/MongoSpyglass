using MongoDB.Bson;
using MongoDB.Driver;
using Spyglass.Proxy.Tests.Infrastructure;

namespace Spyglass.Proxy.Tests;

/// <summary>06 §2.1: transactions, change streams resumed after a proxy-side reset, retryable writes after a reset.</summary>
public sealed class ResilienceTests(ObservationFixture f) : IClassFixture<ObservationFixture>
{
    private MongoClient Client() => new(f.Proxy.Uri("rs0"));

    [Fact]
    public async Task Transactions_commit_and_abort()
    {
        var client = Client();
        var name = "txn" + Guid.NewGuid().ToString("N")[..6];
        var coll = client.GetDatabase("res").GetCollection<BsonDocument>(name);
        await coll.InsertOneAsync(new BsonDocument("_id", 0)); // create the collection outside the txn

        using (var s = await client.StartSessionAsync())
        {
            s.StartTransaction();
            await coll.InsertOneAsync(s, new BsonDocument("_id", 1));
            await coll.InsertOneAsync(s, new BsonDocument("_id", 2));
            await s.CommitTransactionAsync();
        }
        using (var s = await client.StartSessionAsync())
        {
            s.StartTransaction();
            await coll.InsertOneAsync(s, new BsonDocument("_id", 3));
            await s.AbortTransactionAsync();
        }
        Assert.Equal([0, 1, 2], (await coll.Find("{}").Sort("{_id:1}").ToListAsync()).Select(d => d["_id"].AsInt32));

        // txnNumber is observed on the ops inside the transaction
        var ops = await f.Proxy.WaitRecordsAsync(r => r.GetProperty("type").GetString() == "op"
            && r.GetProperty("command").GetString() == "commitTransaction", 1);
        Assert.Equal(System.Text.Json.JsonValueKind.Number, ops[0].GetProperty("txnNumber").ValueKind);
    }

    [Fact]
    public async Task Change_stream_resumes_after_proxy_side_reset()
    {
        var client = Client();
        var name = "cs" + Guid.NewGuid().ToString("N")[..6];
        var coll = client.GetDatabase("res").GetCollection<BsonDocument>(name);
        await coll.InsertOneAsync(new BsonDocument("_id", -1));

        using var cursor = await coll.WatchAsync(new ChangeStreamOptions { MaxAwaitTime = TimeSpan.FromMilliseconds(200) });
        var seen = new List<int>();
        var writer = new MongoClient(f.Mongo.DirectUri).GetDatabase("res").GetCollection<BsonDocument>(name);

        for (var i = 0; i < 6; i++)
        {
            await writer.InsertOneAsync(new BsonDocument("_id", i));
            if (i == 2)
                Assert.True(f.Proxy.Server.ResetConnections() > 0); // every proxied connection drops, the change stream's included
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (!seen.Contains(i))
            {
                Assert.True(DateTime.UtcNow < deadline, $"event {i} not seen; have {string.Join(',', seen)}");
                if (await cursor.MoveNextAsync())
                    seen.AddRange(cursor.Current.Select(c => c.FullDocument["_id"].AsInt32));
            }
        }
        Assert.Equal(Enumerable.Range(0, 6), seen.Distinct().Order());
    }

    [Fact]
    public async Task Retryable_write_after_reset()
    {
        var client = Client();
        var coll = client.GetDatabase("res").GetCollection<BsonDocument>("retry" + Guid.NewGuid().ToString("N")[..6]);
        await coll.InsertOneAsync(new BsonDocument("_id", 0));
        f.Proxy.Server.ResetConnections();
        // the next write hits a dead pooled connection; retryWrites (default) retries it once
        await coll.InsertOneAsync(new BsonDocument("_id", 1));
        f.Proxy.Server.ResetConnections();
        await coll.UpdateOneAsync("{_id: 1}", "{$set: {x: 1}}");
        Assert.Equal(2, await coll.CountDocumentsAsync("{}"));
    }
}
