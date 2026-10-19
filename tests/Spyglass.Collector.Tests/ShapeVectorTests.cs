using System.Text.Json;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using Spyglass.Collector.Shapes;

namespace Spyglass.Collector.Tests;

/// <summary>
/// 06 §3 <c>shape/*.json</c>: commands → expected 03 §4 shape (canonical form), shapeHash, valueHash and
/// inCardinality; <c>grouping.json</c>: pairs that must share a shapeHash or must not.
/// <para>
/// <c>SPYGLASS_WRITE_SHAPE_VECTORS=1</c> fills in <c>expected</c> from the implementation instead. That is how
/// a case is added: write its input, generate, then read the result against 03 §4 before committing it.
/// </para>
/// </summary>
public sealed class ShapeVectorTests
{
    private static readonly bool Write = Environment.GetEnvironmentVariable("SPYGLASS_WRITE_SHAPE_VECTORS") == "1";

    public static TheoryData<string> Files => new(Vectors.Files("shape", "*.json").Where(f => f != "grouping.json"));

    private static ShapeResult Normalize(JsonNode c)
    {
        var cmd = c["cmdHex"] is { } hex
            ? MongoDB.Bson.Serialization.BsonSerializer.Deserialize<BsonDocument>(Convert.FromHexString(hex.GetValue<string>()))
            : BsonDocument.Parse(c["cmd"]!.ToJsonString());
        return ShapeNormalizer.Normalize(c["command"]!.GetValue<string>(), c["db"]?.GetValue<string>(), c["coll"]?.GetValue<string>(), cmd,
            c["cmdTruncated"]?.GetValue<bool>() ?? false, (c["values"]?.GetValue<string>() ?? "truncated") != "shape");
    }

    [Theory]
    [MemberData(nameof(Files))]
    public async Task Commands_normalize_to_the_expected_shape(string file)
    {
        var path = Path.Combine(Vectors.Root, "shape", file);
        var doc = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        foreach (var c in doc["cases"]!.AsArray())
        {
            var r = Normalize(c!);
            var name = c!["name"]!.GetValue<string>();
            if (Write)
            {
                c["expected"] = new JsonObject
                {
                    ["shape"] = JsonNode.Parse(r.ShapeJson), ["shapeHash"] = r.ShapeHash, ["valueHash"] = r.ValueHash, ["inCardinality"] = r.InCardinality,
                };
                continue;
            }
            var e = c["expected"] ?? throw new InvalidOperationException($"{file} {name}: no expected (generate with SPYGLASS_WRITE_SHAPE_VECTORS=1)");
            // the canonical form is compact with sorted keys: re-serializing the expected tree compactly gives its bytes
            Assert.True(e["shape"]!.ToJsonString() == r.ShapeJson, $"{file} {name}:\n expected {e["shape"]!.ToJsonString()}\n actual   {r.ShapeJson}");
            Assert.True(e["shapeHash"]!.GetValue<string>() == r.ShapeHash, $"{file} {name}: shapeHash");
            Assert.True(e["valueHash"]?.GetValue<string>() == r.ValueHash, $"{file} {name}: valueHash {r.ValueHash}");
            Assert.True(e["inCardinality"]!.GetValue<int>() == r.InCardinality, $"{file} {name}: inCardinality {r.InCardinality}");
        }
        if (Write)
            await File.WriteAllTextAsync(path, doc.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }) + "\n");
    }

    [Fact]
    public async Task Grouping_pairs()
    {
        var doc = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(Vectors.Root, "shape", "grouping.json")))!;
        foreach (var c in doc["cases"]!.AsArray())
        {
            var command = c!["command"]?.GetValue<string>() ?? "find";
            string Hash(string side) => Normalize(new JsonObject
            {
                ["command"] = command, ["db"] = "shop", ["coll"] = "orders", ["cmd"] = c[side]!.DeepClone(),
            }).ShapeHash;
            Assert.True((Hash("a") == Hash("b")) == c["same"]!.GetValue<bool>(), $"grouping {c["name"]}: same = {c["same"]}");
        }
    }
}
