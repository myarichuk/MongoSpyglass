using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using Spyglass.Proxy.Tests.Infrastructure;

namespace Spyglass.Proxy.Tests;

/// <summary>06 §2.7: compression=opaque and compression=observe (strip is covered in ReplicaSetTests).</summary>
public sealed class CompressionTests(StandaloneFixture f) : IClassFixture<StandaloneFixture>
{
    private static string S(JsonElement e, string p) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

    private async Task<List<JsonElement>> Run(string mode, string coll)
    {
        await using var proxy = await ProxyHost.StartAsync(f.Mongo.DirectUri, listeners: 1, configure: a => a["compression"] = mode);
        var client = new MongoClient(proxy.Uri(extra: "compressors=zlib"));
        var c = client.GetDatabase("comp").GetCollection<BsonDocument>(coll);
        await c.InsertManyAsync(Enumerable.Range(0, 50).Select(i => new BsonDocument { ["i"] = i, ["s"] = new string('z', 200) }));
        Assert.Equal(50, (await c.Find("{}").ToListAsync()).Count);
        return await proxy.WaitRecordsAsync(r => S(r, "type") == "op" && S(r, "opCode") == "OP_COMPRESSED", 2);
    }

    [Fact]
    public async Task Opaque_forwards_compressed_frames_with_sizes_only()
    {
        var ops = await Run("opaque", "opaque");
        Assert.All(ops, o =>
        {
            Assert.Equal("(compressed)", S(o, "command"));
            Assert.Equal("zlib", S(o, "compressor"));
            Assert.True(o.GetProperty("reqBytes").GetInt32() > 16);
            Assert.True(o.GetProperty("respBytes").GetInt32() > 16);
        });
    }

    [Fact]
    public async Task Observe_inflates_zlib_and_recovers_commands()
    {
        var ops = await Run("observe", "observe");
        var commands = ops.Select(o => S(o, "command")).ToHashSet();
        Assert.Contains("insert", commands);
        Assert.Contains("find", commands);
        Assert.All(ops.Where(o => S(o, "command") is "insert" or "find"), o =>
        {
            Assert.Equal("comp", S(o, "db"));
            Assert.Equal("observe", S(o, "coll"));
        });
    }
}
