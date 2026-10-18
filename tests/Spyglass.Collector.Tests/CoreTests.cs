using System.Text;
using MongoDB.Bson;
using Spyglass.Collector.Analysis;
using Spyglass.Collector.Ingest;
using Spyglass.Collector.Model;
using Spyglass.Collector.Shapes;

namespace Spyglass.Collector.Tests;

public sealed class ParserTests
{
    private static (ParseResult, Model.Record?, string?) P(string line)
    {
        var r = RecordParser.Parse(Encoding.UTF8.GetBytes(line), out var rec, out var err);
        return (r, rec, err);
    }

    [Fact]
    public void Trimmed_op_record_reads_with_defaults_and_derivations()
    {
        // 02 §4.2: absent = default; ack, serverLatency_us, proxyId derived
        var (res, rec, err) = P("""{"type":"op","ts":1700000000000000,"src":"p1","member":"h:1","serverConnectionId":5,"requestId":9,"connSeq":3,"command":"find","db":"d","coll":"c","status":"ok","reqBytes":10,"respBytes":20,"t":{"reqLast_us":3,"respFirst_us":812,"respLast_us":845},"reply":{"ok":1,"cursorId":"9223372036854775807","nReturned":17},"lsid":"ab"}""");
        Assert.True(res == ParseResult.Ok, err);
        var op = Assert.IsType<OpRecord>(rec);
        Assert.Equal("p1", op.ProxyId);
        Assert.Equal("OP_MSG", op.OpCode);
        Assert.True(op.Ack);
        Assert.False(op.Stream);
        Assert.Equal(809, op.ServerLatencyUs);
        Assert.Equal(long.MaxValue, op.Reply!.CursorId); // decimal string, beyond 2^53
        Assert.Equal(0, op.Reply.WriteErrors);
        Assert.True(op.Reply.Parsed);
        Assert.Null(op.Extra);
    }

    [Fact]
    public void Unacknowledged_op_has_no_reply_and_ack_false()
    {
        var (_, rec, _) = P("""{"type":"op","ts":1,"src":"p","member":"h:1","requestId":1,"connSeq":1,"command":"insert","status":"unacknowledged","reqBytes":10,"t":{"reqLast_us":0}}""");
        var op = Assert.IsType<OpRecord>(rec);
        Assert.False(op.Ack);
        Assert.False(op.HasReply);
        Assert.Null(op.ServerLatencyUs);
        Assert.Null(op.ServerConnectionId);
    }

    [Fact]
    public void Unknown_fields_are_kept_verbatim_unknown_types_ignored()
    {
        var (res, rec, _) = P("""{"type":"conn","ts":1,"src":"p","event":"open","member":"h:1","connSeq":2,"future":{"x":[1,2]},"z":"q"}""");
        Assert.Equal(ParseResult.Ok, res);
        Assert.Equal("""{"future":{"x":[1,2]},"z":"q"}""", rec!.Extra);
        Assert.Equal(ParseResult.Ignored, P("""{"type":"somethingNew","ts":1,"src":"p"}""").Item1);
    }

    [Theory]
    [InlineData("""{"type":"op","ts":1,"src":"p","member":"h","requestId":1,"connSeq":1,"command":"find","reqBytes":1}""", "status")] // required missing
    [InlineData("""{"type":"op","src":"p","member":"h","requestId":1,"connSeq":1,"command":"find","status":"ok","reqBytes":1}""", "ts")]
    [InlineData("""{"type":"conn","ts":1,"src":"p","member":"h","connSeq":"one","event":"open"}""", "integer")] // wrong type
    [InlineData("""{"type":"op","ts":1,"src":"p","member":"h","requestId":1,"connSeq":1,"command":"find","status":"ok","reqBytes":1,"reply":{"cursorId":5}}""", "decimal string")]
    public void Invalid_records_are_rejected(string line, string why)
    {
        var (res, _, err) = P(line);
        Assert.Equal(ParseResult.Rejected, res);
        Assert.Contains(why, err);
    }

    [Fact]
    public void Not_json_is_invalid()
    {
        Assert.Equal(ParseResult.Invalid, P("not json").Item1);
        Assert.Equal(ParseResult.Invalid, P("[1,2]").Item1);
    }
}

public sealed class ShapeTests
{
    private static ShapeResult S(BsonDocument cmd, bool values = true, bool truncated = false)
    {
        var name = cmd.GetElement(0).Name;
        var coll = cmd[0].IsString ? cmd[0].AsString : null;
        return ShapeNormalizer.Normalize(name, "shop", coll, cmd, truncated, values);
    }

    private static BsonDocument Find(BsonDocument filter, BsonDocument? extra = null)
    {
        var d = new BsonDocument { ["find"] = "orders", ["filter"] = filter, ["$db"] = "shop" };
        if (extra != null) d.AddRange(extra);
        return d;
    }

    [Fact]
    public void Values_do_not_change_the_shape_but_change_the_value_hash()
    {
        var a = S(Find(new BsonDocument("customerId", 1)));
        var b = S(Find(new BsonDocument("customerId", 2)));
        Assert.Equal(a.ShapeHash, b.ShapeHash);
        Assert.NotEqual(a.ValueHash, b.ValueHash);
        Assert.Equal(16, a.ShapeHash.Length);
        Assert.Equal("""{"cmd":"find","coll":"orders","db":"shop","filter":{"customerId":"?number"}}""", a.ShapeJson);
    }

    [Fact]
    public void Key_order_and_or_clause_order_do_not_matter()
    {
        var a = S(Find(new BsonDocument { ["a"] = 1, ["b"] = "x" }));
        var b = S(Find(new BsonDocument { ["b"] = "y", ["a"] = 2 }));
        Assert.Equal(a.ShapeHash, b.ShapeHash);
        var c = S(Find(new BsonDocument("$or", new BsonArray { new BsonDocument("a", 1), new BsonDocument("b", 2), new BsonDocument("a", 3) })));
        var d = S(Find(new BsonDocument("$or", new BsonArray { new BsonDocument("b", 5), new BsonDocument("a", 9) })));
        Assert.Equal(c.ShapeHash, d.ShapeHash); // sorted and deduplicated
    }

    [Fact]
    public void Operators_are_normalized_per_spec()
    {
        var r = S(Find(new BsonDocument
        {
            ["status"] = new BsonDocument("$in", new BsonArray(Enumerable.Range(0, 5))),
            ["deleted"] = new BsonDocument("$exists", false),
            ["kind"] = new BsonDocument("$type", "string"),
            ["name"] = new BsonDocument { ["$regex"] = "^a", ["$options"] = "i" },
            ["tags"] = new BsonDocument("$size", 3),
            ["qty"] = new BsonDocument { ["$gt"] = 1, ["$lte"] = 9 },
        }));
        var f = r.Shape["filter"].AsBsonDocument;
        Assert.Equal(new BsonArray { "?" }, f["status"]["$in"]);
        Assert.Equal(false, f["deleted"]["$exists"].AsBoolean);
        Assert.Equal("string", f["kind"]["$type"].AsString);
        Assert.Equal("?regex", f["name"]["$regex"].AsString);
        Assert.Equal("?number", f["tags"]["$size"].AsString);
        Assert.Equal("?number", f["qty"]["$gt"].AsString);
        Assert.Equal(5, r.InCardinality);
    }

    [Fact]
    public void Proxy_encodings_are_understood()
    {
        // capped array (01 §9.2: 20 kept + {$more:n}) counts the whole $in; truncated strings and binData stubs are leaves
        var arr = new BsonArray(Enumerable.Range(0, 20)) { new BsonDocument("$more", 1480) };
        var r = S(Find(new BsonDocument
        {
            ["id"] = new BsonDocument("$in", arr),
            ["note"] = "abcdef…(+300)",
            ["blob"] = new BsonDocument("$binData", new BsonDocument { ["subtype"] = 4, ["length"] = 16 }),
            ["pw"] = "$redacted",
        }));
        Assert.Equal(1500, r.InCardinality);
        var f = r.Shape["filter"].AsBsonDocument;
        Assert.Equal("?string", f["note"].AsString);
        Assert.Equal("?uuid", f["blob"].AsString);
        Assert.Equal("?redacted", f["pw"].AsString);

        // values: shape — the proxy already tagged the leaves; the shape is the same, no valueHash
        var shaped = S(Find(new BsonDocument("customerId", "?number")), values: false);
        Assert.Equal(S(Find(new BsonDocument("customerId", 42))).ShapeHash, shaped.ShapeHash);
        Assert.Null(shaped.ValueHash);
        Assert.Null(S(Find(new BsonDocument("customerId", 42)), truncated: true).ValueHash); // 03 §4: not when truncated
    }

    [Fact]
    public void Find_options_and_aggregate_pipeline()
    {
        var a = S(Find(new BsonDocument("a", 1), new BsonDocument { ["sort"] = new BsonDocument("t", -1), ["limit"] = 10, ["projection"] = new BsonDocument("a", 1) }));
        var b = S(Find(new BsonDocument("a", 2), new BsonDocument { ["sort"] = new BsonDocument("t", -1), ["limit"] = 50, ["projection"] = new BsonDocument("a", 1) }));
        Assert.Equal(a.ShapeHash, b.ShapeHash);                        // limit presence only
        var c = S(Find(new BsonDocument("a", 2), new BsonDocument { ["sort"] = new BsonDocument("t", 1), ["limit"] = 50, ["projection"] = new BsonDocument("a", 1) }));
        Assert.NotEqual(a.ShapeHash, c.ShapeHash);                     // sort direction kept

        var agg = new BsonDocument
        {
            ["aggregate"] = "orders",
            ["pipeline"] = new BsonArray
            {
                new BsonDocument("$match", new BsonDocument("status", "A")),
                new BsonDocument("$group", new BsonDocument { ["_id"] = "$cust", ["total"] = new BsonDocument("$sum", "$amount") }),
                new BsonDocument("$limit", 5),
            },
            ["cursor"] = new BsonDocument(),
        };
        var s = S(agg).Shape["pipeline"].AsBsonArray;
        Assert.Equal("?string", s[0]["$match"]["status"].AsString);
        Assert.Equal("$cust", s[1]["$group"]["_id"].AsString);        // field paths are structure
        Assert.Equal("$amount", s[1]["$group"]["total"]["$sum"].AsString);
        Assert.Equal("?", s[2]["$limit"].AsString);
    }

    [Fact]
    public void Update_statements_are_deduplicated_and_operators_kept()
    {
        var cmd = new BsonDocument
        {
            ["update"] = "orders",
            ["updates"] = new BsonArray
            {
                new BsonDocument { ["q"] = new BsonDocument("_id", 1), ["u"] = new BsonDocument("$set", new BsonDocument("s", "x")) },
                new BsonDocument { ["q"] = new BsonDocument("_id", 2), ["u"] = new BsonDocument("$set", new BsonDocument("s", "y")) },
            },
        };
        var ups = S(cmd).Shape["updates"].AsBsonArray;
        Assert.Single(ups);
        Assert.Equal("?string", ups[0]["u"]["$set"]["s"].AsString);
    }
}

public sealed class SketchTests
{
    [Fact]
    public void Quantiles_within_one_percent_and_survive_serialization()
    {
        var s = new Sketch();
        var rnd = new Random(1);
        var values = Enumerable.Range(0, 20_000).Select(_ => Math.Exp(rnd.NextDouble() * 14)).ToArray(); // 1 µs .. ~1.2 s
        foreach (var v in values) s.Add(v);
        Array.Sort(values);
        foreach (var q in new[] { 0.5, 0.9, 0.95, 0.99 })
        {
            var exact = values[(int)Math.Ceiling(q * values.Length) - 1];
            Assert.InRange(s.Quantile(q), exact * 0.99, exact * 1.01);
        }
        var round = Sketch.Deserialize(s.Serialize());
        Assert.Equal(s.Quantile(0.95), round.Quantile(0.95));
        var merged = new Sketch();
        merged.Merge(s);
        merged.Merge(round);
        Assert.Equal(2 * s.Count, merged.Count);
        Assert.Equal(s.Quantile(0.5), merged.Quantile(0.5));
    }
}
