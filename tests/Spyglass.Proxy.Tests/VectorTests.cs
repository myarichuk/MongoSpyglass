using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using Spyglass.Proxy.Config;
using Spyglass.Proxy.Tests.Infrastructure;

namespace Spyglass.Proxy.Tests;

/// <summary>
/// 06 §3 conformance vectors (<c>docs/spec/vectors/ingest-v0</c>), proxy side: address normalization, and every
/// wire record a real workload produces uses only the fields, and JSON types, of its type's full example.
/// </summary>
public sealed class VectorTests(StandaloneFixture f) : IClassFixture<StandaloneFixture>
{
    private static string Root
    {
        get
        {
            for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            {
                var v = Path.Combine(d.FullName, "docs", "spec", "vectors", "ingest-v0");
                if (Directory.Exists(v)) return v;
            }
            throw new DirectoryNotFoundException("docs/spec/vectors/ingest-v0");
        }
    }

    [Fact]
    public void Member_addresses_normalize_per_02_3_2()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "normalize-address.json")));
        foreach (var c in doc.RootElement.GetProperty("cases").EnumerateArray())
        {
            var input = c[0].GetString()!;
            Assert.True(HostPort.TryParse(input, out var hp), input);
            Assert.Equal(c[1].GetString(), hp.ToString());
        }
    }

    /// <summary>Objects whose keys are data (counter names, sequence identifiers), not fields.</summary>
    private static readonly HashSet<string> Maps = ["seqDocs", "dropped"];

    /// <summary>02 §4.2 required fields per wire record type.</summary>
    private static readonly Dictionary<string, string[]> Required = new()
    {
        ["op"] = ["type", "ts", "src", "member", "requestId", "connSeq", "command", "status", "reqBytes", "t"],
        ["conn"] = ["type", "ts", "src", "event", "member", "connSeq"],
        ["stream"] = ["type", "ts", "src", "member", "requestId", "seq"],
        ["proxy"] = ["type", "ts", "src", "version", "schema", "topology", "members", "config"],
        ["stats"] = ["type", "ts", "src"],
    };

    [Fact]
    public async Task Emitted_wire_records_use_only_the_fields_and_types_of_the_vectors()
    {
        await using var collector = new FakeCollector();
        await using var proxy = await ProxyHost.StartAsync(f.Mongo.DirectUri, listeners: 1, configure: a =>
        {
            a["collector.endpoint"] = collector.Endpoint;
            a["collector.flushIntervalMs"] = "50";
        });
        var client = new MongoClient(proxy.Uri(extra: "appName=vectors"));
        var coll = client.GetDatabase("vec").GetCollection<BsonDocument>("c" + Guid.NewGuid().ToString("N")[..6]);
        await coll.InsertManyAsync(Enumerable.Range(0, 50).Select(i => new BsonDocument { ["_id"] = i, ["k"] = i % 5 }));
        await coll.Find(new BsonDocument("k", 1)).ToListAsync();
        await coll.Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).Limit(30).ToListAsync(); // first batch
        using (var cursor = await coll.FindAsync(new BsonDocument(), new FindOptions<BsonDocument> { BatchSize = 5 }))
            await cursor.MoveNextAsync(); // disposed early: killCursors
        await coll.Find(new BsonDocument(), new FindOptions { BatchSize = 7 }).Project("{_id:1}").ToListAsync(); // getMores
        try { await coll.InsertOneAsync(new BsonDocument("_id", 1)); } catch (MongoWriteException) { }                    // writeErrors
        try { await client.GetDatabase("vec").RunCommandAsync<BsonDocument>(new BsonDocument("nope", 1)); } catch (MongoCommandException) { } // ok:0
        await coll.WithWriteConcern(WriteConcern.Unacknowledged).InsertOneAsync(new BsonDocument("_id", 1000));            // unacknowledged
        await coll.UpdateManyAsync(new BsonDocument("k", 2), new BsonDocument("$set", new BsonDocument("x", 1)));
        await coll.DeleteManyAsync(new BsonDocument("k", 3));
        await coll.Aggregate().Match(new BsonDocument("k", 4)).Group(new BsonDocument { ["_id"] = "$k", ["n"] = new BsonDocument("$sum", 1) }).ToListAsync();
        MongoDB.Driver.ClusterRegistry.Instance.UnregisterAndDisposeCluster(client.Cluster); // conn close records (Dispose leaves the cluster alive)

        // stats come every 10 s (02 §4.2)
        var deadline = DateTime.UtcNow.AddSeconds(15);
        bool Done() => collector.All.Any(r => r.GetProperty("type").GetString() == "stats") &&
                       collector.All.Any(r => r.GetProperty("type").GetString() == "conn" && r.GetProperty("event").GetString() == "close");
        while (!Done() && DateTime.UtcNow < deadline) await Task.Delay(100);

        var records = collector.All.ToList();
        var byType = records.GroupBy(r => r.GetProperty("type").GetString()!).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var t in Required.Keys.Where(t => t != "stream")) // stream needs exhaust or streaming hello on the wire
            Assert.True(byType.ContainsKey(t), $"no {t} record in the workload");
        Assert.Contains(byType["conn"], r => r.GetProperty("event").GetString() == "close");

        var problems = new List<string>();
        foreach (var (type, list) in byType)
        {
            Assert.True(Required.ContainsKey(type), $"the proxy emitted a {type} record, which is not a wire record type");
            var schema = Schema(type);
            foreach (var r in list)
            {
                foreach (var req in Required[type])
                    if (!r.TryGetProperty(req, out _)) problems.Add($"{type}: required '{req}' missing in {r}");
                Conform(type, r, schema, problems);
            }
        }
        Assert.True(problems.Count == 0, string.Join("\n", problems.Distinct().Take(20)));
    }

    /// <summary>The union of a type's vector lines, as the reference shape.</summary>
    private static List<JsonElement> Schema(string type) =>
        File.ReadAllLines(Path.Combine(Root, "records", type + ".ndjson")).Where(l => l.Length > 0)
            .Select(l => JsonDocument.Parse(l).RootElement.Clone()).ToList();

    private static void Conform(string path, JsonElement actual, List<JsonElement> refs, List<string> problems)
    {
        if (actual.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in actual.EnumerateObject())
            {
                var children = refs.Where(r => r.ValueKind == JsonValueKind.Object).Select(r => r.TryGetProperty(p.Name, out var v) ? v : default)
                    .Where(v => v.ValueKind != JsonValueKind.Undefined).ToList();
                if (children.Count == 0) { problems.Add($"{path}.{p.Name}: not a field of the vector"); continue; }
                if (p.Value.ValueKind == JsonValueKind.Null) { problems.Add($"{path}.{p.Name}: null (absent means default, 02 §4)"); continue; }
                if (!children.Any(c => Kind(c) == Kind(p.Value)))
                {
                    problems.Add($"{path}.{p.Name}: {p.Value.ValueKind}, the vector has {children[0].ValueKind}");
                    continue;
                }
                if (Maps.Contains(p.Name)) continue;
                Conform($"{path}.{p.Name}", p.Value, children, problems);
            }
        }
        else if (actual.ValueKind == JsonValueKind.Array)
        {
            var items = refs.Where(r => r.ValueKind == JsonValueKind.Array).SelectMany(r => r.EnumerateArray()).ToList();
            foreach (var x in actual.EnumerateArray()) Conform(path + "[]", x, items, problems);
        }
    }

    private static string Kind(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.True or JsonValueKind.False => "bool",
        var k => k.ToString(),
    };
}
