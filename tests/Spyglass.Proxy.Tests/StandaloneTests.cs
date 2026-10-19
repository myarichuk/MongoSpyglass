using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using MongoDB.Bson;
using MongoDB.Driver;
using Spyglass.Proxy.Tests.Infrastructure;

namespace Spyglass.Proxy.Tests;

public sealed class StandaloneFixture : IAsyncLifetime
{
    public Deployment Mongo { get; private set; } = null!;
    public ProxyHost Proxy { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        MongodBinaries.SkipIfMissing();
        Mongo = await Deployment.StartStandaloneAsync();
        Proxy = await ProxyHost.StartAsync(Mongo.DirectUri, listeners: 1);
    }

    public async ValueTask DisposeAsync()
    {
        if (Proxy != null) await Proxy.DisposeAsync();
        if (Mongo != null) await Mongo.DisposeAsync();
    }
}

/// <summary>06 §2.1 forwarding, against a real standalone mongod.</summary>
public sealed class StandaloneTests(StandaloneFixture f) : IClassFixture<StandaloneFixture>
{
    private IMongoDatabase Db(string? uri = null, string name = "t") =>
        new MongoClient(uri ?? f.Proxy.Uri()).GetDatabase(name);

    [Fact]
    public async Task Crud_through_proxy_matches_direct()
    {
        var coll = Db(name: "crud").GetCollection<BsonDocument>("c" + Guid.NewGuid().ToString("N")[..6]);
        await coll.InsertManyAsync(Enumerable.Range(0, 500).Select(i => new BsonDocument { ["_id"] = i, ["v"] = i % 7, ["s"] = new string('x', i) }));
        await coll.UpdateManyAsync(Builders<BsonDocument>.Filter.Eq("v", 3), Builders<BsonDocument>.Update.Set("u", true));
        await coll.DeleteManyAsync(Builders<BsonDocument>.Filter.Eq("v", 6));

        var viaProxy = await coll.Find(FilterDefinition<BsonDocument>.Empty).Sort("{_id:1}").ToListAsync();
        var direct = await new MongoClient(f.Mongo.DirectUri).GetDatabase("crud").GetCollection<BsonDocument>(coll.CollectionNamespace.CollectionName)
            .Find(FilterDefinition<BsonDocument>.Empty).Sort("{_id:1}").ToListAsync();

        Assert.Equal(500 - 500 / 7 - (500 % 7 > 6 ? 1 : 0), viaProxy.Count);
        Assert.Equal(direct, viaProxy);

        var agg = await coll.Aggregate().Group("{_id: '$v', n: {$sum: 1}}").Sort("{_id: 1}").ToListAsync();
        Assert.Equal(6, agg.Count);
    }

    [Fact]
    public async Task Hello_reply_carries_spyglass_document()
    {
        var hello = await Db(name: "admin").RunCommandAsync<BsonDocument>(new BsonDocument("hello", 1));
        var sg = hello["$spyglass"].AsBsonDocument;
        Assert.Equal(0, sg["v"].AsInt32);
        Assert.Equal(1, sg["schema"].AsInt32);
        Assert.Equal(f.Proxy.Server.ProxyId.ToString(), sg["proxyId"].AsString);
        Assert.Equal($"localhost:{f.Proxy.BasePort}", sg["member"].AsString);
        Assert.False(sg.Contains("ingest")); // headless
        Assert.Equal("$spyglass", hello.Names.Last()); // R2: last top-level element
    }

    [Fact]
    public async Task Legacy_isMaster_is_rewritten_too()
    {
        var hello = await Db(name: "admin").RunCommandAsync<BsonDocument>(new BsonDocument("isMaster", 1));
        Assert.True(hello.Contains("$spyglass"));
        var lower = await Db(name: "admin").RunCommandAsync<BsonDocument>(new BsonDocument("ismaster", 1));
        Assert.True(lower.Contains("$spyglass"));
    }

    [Fact]
    public async Task Large_document_and_large_batch()
    {
        var coll = Db(name: "large").GetCollection<BsonDocument>("c" + Guid.NewGuid().ToString("N")[..6]);
        var big = new BsonDocument { ["_id"] = 1, ["blob"] = new BsonBinaryData(new byte[16 * 1024 * 1024 - 1024]) };
        await coll.InsertOneAsync(big);
        var back = await coll.Find("{_id: 1}").FirstAsync();
        Assert.Equal(big["blob"].AsByteArray.Length, back["blob"].AsByteArray.Length);

        // ~48 MB of replies in 16 MB batches
        await coll.InsertManyAsync(Enumerable.Range(2, 3).Select(i => new BsonDocument { ["_id"] = i, ["blob"] = new BsonBinaryData(new byte[15 * 1024 * 1024]) }));
        var total = 0L;
        await foreach (var d in coll.Find(FilterDefinition<BsonDocument>.Empty).ToAsyncEnumerable())
            total += d["blob"].AsByteArray.Length;
        Assert.True(total > 45L * 1024 * 1024);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(int.MaxValue)]
    public async Task Invalid_frame_fails_closed_without_affecting_others(int length)
    {
        var other = new MongoClient(f.Proxy.Uri());
        await other.GetDatabase("admin").RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1));

        using var raw = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await raw.ConnectAsync(IPAddress.Loopback, f.Proxy.BasePort);
        var header = new byte[16];
        BinaryPrimitives.WriteInt32LittleEndian(header, length);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12), 2013);
        await raw.SendAsync(header);
        var buf = new byte[16];
        var read = await raw.ReceiveAsync(buf).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, read); // proxy closed the connection

        await other.GetDatabase("admin").RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1));
        Assert.Contains("spyglass_proxy_invalid_frames_total", await f.Proxy.HttpGetAsync("/metrics"));
    }

    [Fact]
    public async Task Byte_at_a_time_client_still_works()
    {
        // Exercises header hold-back and frame tracking across arbitrary chunk boundaries.
        using var raw = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        await raw.ConnectAsync(IPAddress.Loopback, f.Proxy.BasePort);
        var frame = OpMsg(7, new BsonDocument { ["ping"] = 1, ["$db"] = "admin" });
        foreach (var b in frame)
        {
            await raw.SendAsync(new[] { b });
            await Task.Yield();
        }
        var reply = await ReadFrameAsync(raw);
        Assert.Equal(7, BinaryPrimitives.ReadInt32LittleEndian(reply.AsSpan(8)));
        var doc = MongoDB.Bson.Serialization.BsonSerializer.Deserialize<BsonDocument>(reply.AsSpan(21).ToArray());
        Assert.Equal(1.0, doc["ok"].ToDouble());
    }

    [Fact]
    public async Task Pipelined_frames_in_one_write_all_answered()
    {
        using var raw = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await raw.ConnectAsync(IPAddress.Loopback, f.Proxy.BasePort);
        var frames = Enumerable.Range(100, 20).SelectMany(i => OpMsg(i, new BsonDocument { ["ping"] = 1, ["$db"] = "admin" })).ToArray();
        await raw.SendAsync(frames);
        var ids = new List<int>();
        for (var i = 0; i < 20; i++)
            ids.Add(BinaryPrimitives.ReadInt32LittleEndian((await ReadFrameAsync(raw)).AsSpan(8)));
        Assert.Equal(Enumerable.Range(100, 20), ids);
    }

    [Fact]
    public async Task Health_endpoints()
    {
        Assert.Equal("ok\n", await f.Proxy.HttpGetAsync("/healthz"));
        var ready = await f.Proxy.HttpGetAsync("/readyz");
        Assert.StartsWith("ready", ready);
        var metrics = await f.Proxy.HttpGetAsync("/metrics");
        Assert.Contains("spyglass_proxy_frames_total{listener=", metrics);
        Assert.Contains("spyglass_proxy_forward_added_latency_seconds_bucket{le=\"5.0000000000000004E-06\"}", metrics);
    }

    internal static byte[] OpMsg(int requestId, BsonDocument body)
    {
        var doc = body.ToBson();
        var frame = new byte[16 + 4 + 1 + doc.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, frame.Length);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4), requestId);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(12), 2013);
        doc.CopyTo(frame, 21);
        return frame;
    }

    internal static async Task<byte[]> ReadFrameAsync(Socket s)
    {
        var header = new byte[16];
        await ReceiveExactly(s, header);
        var len = BinaryPrimitives.ReadInt32LittleEndian(header);
        var frame = new byte[len];
        header.CopyTo(frame, 0);
        await ReceiveExactly(s, frame.AsMemory(16));
        return frame;
    }

    private static async Task ReceiveExactly(Socket s, Memory<byte> buf)
    {
        var got = 0;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (got < buf.Length)
        {
            var n = await s.ReceiveAsync(buf[got..], SocketFlags.None, cts.Token);
            if (n == 0) throw new IOException("closed");
            got += n;
        }
    }
}
