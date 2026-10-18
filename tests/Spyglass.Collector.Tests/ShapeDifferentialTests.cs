using MongoDB.Bson;
using Spyglass.Collector.Pipeline;
using Spyglass.Collector.Shapes;

namespace Spyglass.Collector.Tests;

/// <summary>
/// The streaming normalizer (straight from the BSON bytes) against the tree-building one it replaced, on random commands
/// that reach every 03 §4 rule and every proxy encoding: identical shape JSON, shapeHash, valueHash and inCardinality, in
/// every mode. Also the span layer under it: validation agrees with FastBson's decoder, canonical JSON with CanonicalJson,
/// and the stage-1 facts with their decoded-document versions.
/// </summary>
public sealed class ShapeDifferentialTests
{
    private static readonly string[] Fields = ["a", "b", "c", "a.b", "_id", "é", "Z", "$x"];

    private static BsonValue Leaf(Random r) => r.Next(12) switch
    {
        0 => "$redacted",
        1 => "?number",
        2 => "$field.path",
        3 => "abc…(+300)",
        4 => new BsonDocument("$binData", new BsonDocument { ["subtype"] = r.Next(2) == 0 ? 4 : 0, ["length"] = 16 }),
        5 => new BsonDocument("$binData", 1), // not a stub: the value is not a document
        _ => CanonicalTests.RandomValue(r, 3),
    };

    private static BsonArray Arr(Random r, Func<BsonValue> item, int max = 4)
    {
        var a = new BsonArray(Enumerable.Range(0, r.Next(max)).Select(_ => item()));
        if (r.Next(4) == 0) a.Add(new BsonDocument("$more", r.Next(3) switch { 0 => (BsonValue)r.Next(1, 2000), 1 => (BsonValue)(long)r.Next(1, 50), _ => 3.0 }));
        return a;
    }

    private static BsonDocument Doc(Random r, Func<string> name, Func<BsonValue> value, int max = 4)
    {
        var d = new BsonDocument(allowDuplicateNames: true);
        for (var i = r.Next(max); i > 0; i--) d.Add(name(), value()); // names may repeat: last one wins in normalized docs
        return d;
    }

    private static BsonValue Filter(Random r, int depth) => depth > 2 ? new BsonDocument("a", 1) : Doc(r, () => r.Next(8) switch
    {
        0 => "$and", 1 => "$or", 2 => "$nor",
        _ => Fields[r.Next(Fields.Length)],
    }, () => FilterValue(r, depth));

    private static BsonValue FilterValue(Random r, int depth) => r.Next(10) switch
    {
        0 => Arr(r, () => r.Next(5) == 0 ? 7 : Filter(r, depth + 1)),           // clauses (when the name is logical)
        1 or 2 => Operators(r, depth),
        3 => new BsonDocument { ["x"] = 1, ["$y"] = 2 },                           // exact-match document
        _ => Leaf(r),
    };

    private static BsonDocument Operators(Random r, int depth)
    {
        string[] ops = ["$in", "$nin", "$all", "$exists", "$type", "$regex", "$options", "$elemMatch", "$not", "$size", "$gt", "$lte", "$ne", "$foo"];
        return Doc(r, () => ops[r.Next(ops.Length)], () => r.Next(6) switch
        {
            0 => Arr(r, () => Leaf(r), 6),
            1 => depth > 2 ? 1 : r.Next(2) == 0 ? Operators(r, depth + 1) : Filter(r, depth + 1),
            2 => new BsonRegularExpression("^a", r.Next(2) == 0 ? "i" : "mi"),
            _ => Leaf(r),
        }, 3);
    }

    private static BsonValue Keys(Random r) => r.Next(5) == 0 ? (BsonValue)"idx_1" : Doc(r, () => Fields[r.Next(Fields.Length)], () => r.Next(3) switch { 0 => 1, 1 => -1, _ => "text" });

    private static BsonValue Projection(Random r, int depth) => Doc(r, () => Fields[r.Next(Fields.Length)], () => r.Next(6) switch
    {
        0 => 1, 1 => 0, 2 => true, 3 => 1.0,
        4 => depth < 2 ? Projection(r, depth + 1) : "$a",
        _ => new BsonDocument("$gt", new BsonArray { "$total", r.Next(100) }),
    });

    private static BsonValue Pipeline(Random r, int depth) => Arr(r, () => r.Next(14) switch
    {
        0 => new BsonDocument("$match", r.Next(5) == 0 ? (BsonValue)5 : Filter(r, 1)),
        1 => new BsonDocument("$sort", Keys(r)),
        2 => new BsonDocument("$group", Doc(r, () => r.Next(3) == 0 ? "_id" : "n" + r.Next(3), () => r.Next(3) switch
        {
            0 => "$cust", 1 => new BsonDocument("$sum", r.Next(2) == 0 ? (BsonValue)1 : "$amount"), _ => new BsonDocument { ["y"] = new BsonDocument("$year", "$ts") },
        })),
        3 => new BsonDocument("$project", Projection(r, 0)),
        4 => new BsonDocument("$lookup", Doc(r, () => new[] { "from", "localField", "foreignField", "as", "let", "pipeline" }[r.Next(6)],
            () => r.Next(4) == 0 && depth < 1 ? Pipeline(r, depth + 1) : r.Next(3) == 0 ? new BsonDocument("o", "$_id") : "x" + r.Next(3), 6)),
        5 => new BsonDocument("$limit", r.Next(100)),
        6 => new BsonDocument("$skip", 3),
        7 => new BsonDocument("$unwind", "$items"),
        8 => new BsonDocument("$addFields", new BsonDocument("k", Leaf(r))),
        9 => new BsonDocument("$set", new BsonDocument("k", "$a")),
        10 => new BsonDocument("$frob", Arr(r, () => Leaf(r))),
        11 => new BsonDocument(),
        12 => 42,
        _ => new BsonDocument("$changeStream", new BsonDocument()),
    }, 5);

    private static BsonValue Update(Random r) => r.Next(5) switch
    {
        0 => Pipeline(r, 0),
        1 => Doc(r, () => "f" + r.Next(3), () => Leaf(r)),                         // replacement
        2 => 3,
        _ => Doc(r, () => new[] { "$set", "$inc", "$unset", "$push" }[r.Next(4)], () => r.Next(5) == 0 ? (BsonValue)1 : Doc(r, () => Fields[r.Next(Fields.Length)], () => Leaf(r))),
    };

    private static BsonValue Statements(Random r, bool delete)
    {
        var list = new List<BsonValue>();
        for (var i = r.Next(5); i > 0; i--)
        {
            var s = new BsonDocument { ["q"] = r.Next(6) == 0 ? (BsonValue)1 : Filter(r, 1) };
            if (delete) s["limit"] = r.Next(2);
            else
            {
                if (r.Next(4) != 0) s["u"] = Update(r);
                if (r.Next(3) == 0) s["upsert"] = true;
                if (r.Next(3) == 0) s["multi"] = r.Next(2) == 0;
            }
            list.Add(s);
            if (r.Next(3) == 0) list.Add(s.DeepClone()); // a duplicate statement: deduplicated in shape mode
            if (r.Next(8) == 0) list.Add("not a statement");
        }
        list = [.. list.OrderBy(_ => r.Next())];
        var a = new BsonArray(list);
        if (r.Next(5) == 0) a.Add(new BsonDocument("$more", 10));
        return a;
    }

    internal static (string Name, BsonDocument Cmd) RandomCommand(Random r)
    {
        var kind = r.Next(13);
        var name = new[] { "find", "aggregate", "update", "delete", "findAndModify", "count", "distinct", "insert", "getMore", "killCursors", "ping", "saslStart", "find" }[kind];
        var d = new BsonDocument(allowDuplicateNames: true) { [name] = name is "getMore" ? (BsonValue)(long)r.Next() : "orders" };
        void Maybe(string k, Func<BsonValue> v) { if (r.Next(3) != 0) d.Add(k, v()); }
        switch (name)
        {
            case "find":
                Maybe("filter", () => r.Next(8) == 0 ? (BsonValue)"x" : Filter(r, 0));
                Maybe("sort", () => Keys(r));
                Maybe("projection", () => Projection(r, 0));
                Maybe("limit", () => r.Next(3) switch { 0 => 0, 1 => (long)r.Next(100), _ => "10" });
                Maybe("skip", () => 5);
                Maybe("batchSize", () => 101);
                Maybe("hint", () => Keys(r));
                Maybe("collation", () => r.Next(3) switch { 0 => new BsonDocument { ["locale"] = "fr", ["strength"] = 2 }, 1 => new BsonDocument("strength", 1), _ => "fr" });
                Maybe("tailable", () => r.Next(2) == 0);
                Maybe("awaitData", () => r.Next(3));
                if (r.Next(10) == 0) d.Add("filter", Filter(r, 0)); // a repeated top-level name: the first one counts
                break;
            case "aggregate":
                Maybe("pipeline", () => r.Next(10) == 0 ? (BsonValue)"p" : Pipeline(r, 0));
                break;
            case "update": Maybe("updates", () => Statements(r, delete: false)); break;
            case "delete": Maybe("deletes", () => Statements(r, delete: true)); break;
            case "findAndModify":
                Maybe("query", () => Filter(r, 0));
                Maybe("sort", () => Keys(r));
                Maybe("update", () => Update(r));
                Maybe("new", () => true);
                Maybe("upsert", () => r.Next(2) == 0);
                Maybe("remove", () => true);
                break;
            case "count": Maybe("query", () => Filter(r, 0)); Maybe("limit", () => r.Next(2)); break;
            case "distinct": Maybe("key", () => r.Next(2) == 0 ? (BsonValue)"customer" : 5); Maybe("query", () => Filter(r, 0)); break;
            case "insert": d.Add("documents", Arr(r, () => CanonicalTests.RandomDoc(r, 1))); break;
            case "killCursors": d.Add("cursors", Arr(r, () => r.Next(3) == 0 ? (BsonValue)"x" : (long)r.Next())); break;
            case "saslStart": d = new BsonDocument("$redacted", "saslStart"); break;
        }
        d.Add("$db", "shop");
        if (r.Next(2) == 0) d.Add("lsid", new BsonDocument("id", "5b8516c3a99c21e7"));
        return (name, d);
    }

    [Fact]
    public void Streaming_normalizer_matches_the_tree_one()
    {
        var r = new Random(20261018);
        for (var i = 0; i < 20_000; i++)
        {
            var (name, cmd) = RandomCommand(r);
            var bytes = cmd.ToBson();
            Assert.True(BsonSpan.Validate(bytes));
            var coll = r.Next(10) == 0 ? null : "orders";
            var (truncated, values) = (r.Next(6) == 0, r.Next(5) != 0);
            ReferenceShape expected;
            try { expected = ReferenceShapeNormalizer.Normalize(name, "shop", coll, FastBson.Decode(bytes), truncated, values); }
            catch (OverflowException) { continue; } // a $more count out of int range: the tree version threw too
            var actual = ShapeNormalizer.Normalize(name, "shop", coll, bytes, truncated, values);
            Assert.True(expected.ShapeJson == actual.ShapeJson, $"#{i} {cmd.ToJson()}\n tree      {expected.ShapeJson}\n streaming {actual.ShapeJson}");
            Assert.Equal(expected.ShapeHash, actual.ShapeHash);
            Assert.True(expected.ValueHash == actual.ValueHash, $"#{i} valueHash differs: {cmd.ToJson()}");
            Assert.True(expected.InCardinality == actual.InCardinality, $"#{i} inCardinality {expected.InCardinality} vs {actual.InCardinality}: {cmd.ToJson()}");
        }
    }

    [Fact]
    public void Canonical_json_from_bytes_matches_the_tree_writer()
    {
        var r = new Random(7);
        var buf = new System.Buffers.ArrayBufferWriter<byte>();
        using var w = new System.Text.Json.Utf8JsonWriter(buf, new System.Text.Json.JsonWriterOptions { SkipValidation = true });
        for (var i = 0; i < 5000; i++)
        {
            var doc = new BsonDocument(allowDuplicateNames: true);
            doc.AddRange(CanonicalTests.RandomDoc(r, 0).Elements);
            if (r.Next(4) == 0) doc.Add(doc.GetElement(0).Name, 1); // repeated names: kept, in order
            var bytes = doc.ToBson();
            buf.ResetWrittenCount();
            w.Reset(buf);
            BsonSpan.WriteDocument(w, bytes);
            w.Flush();
            Assert.Equal(CanonicalJson.String(FastBson.Decode(bytes)), System.Text.Encoding.UTF8.GetString(buf.WrittenSpan));
        }
    }

    [Fact]
    public void Validation_accepts_exactly_what_the_decoder_decodes()
    {
        var r = new Random(12);
        for (var i = 0; i < 20_000; i++)
        {
            var bytes = (r.Next(2) == 0 ? CanonicalTests.RandomDoc(r, 0) : RandomCommand(r).Cmd).ToBson();
            if (r.Next(2) == 0) bytes = bytes[..r.Next(bytes.Length)];
            else bytes[r.Next(bytes.Length)] ^= (byte)(1 << r.Next(8));
            bool decodes;
            try { FastBson.Decode(bytes); decodes = true; }
            catch (FormatException) { decodes = false; }
            Assert.True(decodes == BsonSpan.Validate(bytes), $"#{i}: decoder {decodes}, validation {!decodes}");
        }
    }

    [Fact]
    public void Stage_one_facts_match_their_document_versions()
    {
        var r = new Random(3);
        for (var i = 0; i < 20_000; i++)
        {
            var (name, cmd) = RandomCommand(r);
            var bytes = cmd.ToBson();
            Assert.Equal(AwaitsByDesign(name, cmd), Partition.AwaitsByDesign(name, bytes));
            Assert.Equal(IsUnbounded(name, cmd), Partition.IsUnbounded(name, bytes));
        }
    }

    // the decoded-document versions the span ones replaced
    private static bool AwaitsByDesign(string command, BsonDocument cmd)
    {
        if (command is "hello" or "isMaster" or "ismaster") return true;
        if (cmd.TryGetValue("tailable", out var t) && t.ToBoolean() || cmd.TryGetValue("awaitData", out var a) && a.ToBoolean()) return true;
        return command == "aggregate" && cmd.TryGetValue("pipeline", out var p) && p is BsonArray { Count: > 0 } stages &&
               stages[0] is BsonDocument { ElementCount: > 0 } first && first.GetElement(0).Name == "$changeStream";
    }

    private static bool IsUnbounded(string command, BsonDocument cmd)
    {
        static bool Empty(BsonDocument c, string f) => !c.TryGetValue(f, out var v) || v is BsonDocument { ElementCount: 0 };
        static bool NoLimit(BsonDocument c) => !c.TryGetValue("limit", out var l) || (l.IsNumeric && l.ToInt64() == 0);
        return command switch
        {
            "find" => Empty(cmd, "filter") && NoLimit(cmd),
            "count" => Empty(cmd, "query") && NoLimit(cmd),
            "aggregate" => cmd.TryGetValue("pipeline", out var p) && p is BsonArray stages &&
                           (stages.Count == 0 || stages[0] is not BsonDocument { ElementCount: > 0 } first ||
                            first.GetElement(0).Name is not ("$match" or "$limit" or "$collStats" or "$indexStats" or "$currentOp")),
            _ => false,
        };
    }
}
