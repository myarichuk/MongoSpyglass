using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;
using Spyglass.Collector.Shapes;

namespace Spyglass.Collector.Tests;

/// <summary>
/// The fast paths against the driver as the oracle: <see cref="FastBson"/> must decode exactly what
/// <c>BsonSerializer</c> decodes, and <see cref="CanonicalJson"/> must group exactly what the driver's
/// sorted relaxed Extended JSON groups (same bytes ⇔ same text).
/// </summary>
public sealed class CanonicalTests
{
    private static BsonValue RandomValue(Random r, int depth)
    {
        var kind = r.Next(depth > 3 ? 14 : 16);
        return kind switch
        {
            0 => new BsonInt32(r.Next(-5, 5)),
            1 => new BsonInt64(r.NextInt64(-3, 3) * (r.Next(2) == 0 ? 1 : 1L << 40)),
            2 => new BsonDouble(r.Next(4) switch { 0 => 1.0, 1 => -0.5, 2 => double.PositiveInfinity, _ => r.NextDouble() }),
            3 => new BsonString(r.Next(3) switch { 0 => "", 1 => "é\"\\\n", _ => "s" + r.Next(3) }),
            4 => (BsonBoolean)(r.Next(2) == 0),
            5 => BsonNull.Value,
            6 => new BsonObjectId(new ObjectId(r.Next(2) == 0 ? "5f1d7a9e8b3c2d1e0f4a5b6c" : "000000000000000000000001")),
            7 => new BsonDateTime(r.NextInt64(-1000, 1_800_000_000_000)),
            8 => new BsonBinaryData(Guid.NewGuid().ToByteArray(), r.Next(2) == 0 ? BsonBinarySubType.UuidStandard : BsonBinarySubType.Binary),
            9 => new BsonRegularExpression("^a" + r.Next(2), r.Next(2) == 0 ? "i" : ""),
            10 => new BsonTimestamp(r.Next(1, 100), r.Next(1, 10)),
            11 => new BsonDecimal128(Decimal128.Parse(r.Next(2) == 0 ? "1.10" : "1.1")),
            12 => r.Next(2) == 0 ? BsonMinKey.Value : BsonMaxKey.Value,
            13 => new BsonJavaScript("function(){return " + r.Next(2) + "}"),
            14 => RandomDoc(r, depth + 1),
            _ => new BsonArray(Enumerable.Range(0, r.Next(4)).Select(_ => RandomValue(r, depth + 1))),
        };
    }

    private static BsonDocument RandomDoc(Random r, int depth)
    {
        var d = new BsonDocument();
        foreach (var name in Enumerable.Range(0, r.Next(1, 5)).Select(_ => ((char)('a' + r.Next(5))).ToString() + (r.Next(3) == 0 ? "$" : "")).Distinct())
            d[name] = RandomValue(r, depth);
        return d;
    }

    private static string DriverCanonical(BsonValue v)
    {
        static BsonValue Sorted(BsonValue x) => x switch
        {
            BsonDocument d => new BsonDocument(d.Elements.OrderBy(e => e.Name, StringComparer.Ordinal).Select(e => new BsonElement(e.Name, Sorted(e.Value)))),
            BsonArray a => new BsonArray(a.Select(Sorted)),
            _ => x,
        };
        return Sorted(v).ToJson(new JsonWriterSettings { OutputMode = JsonOutputMode.RelaxedExtendedJson });
    }

    [Fact]
    public void Fast_decode_matches_the_driver()
    {
        var r = new Random(11);
        for (var i = 0; i < 5000; i++)
        {
            var doc = RandomDoc(r, 0);
            var bytes = doc.ToBson();
            Assert.Equal(BsonSerializer.Deserialize<BsonDocument>(bytes), FastBson.Decode(bytes));
        }
    }

    [Fact]
    public void Fast_decode_fails_closed_on_damage()
    {
        var r = new Random(12);
        for (var i = 0; i < 5000; i++)
        {
            var bytes = RandomDoc(r, 0).ToBson();
            if (r.Next(2) == 0) bytes = bytes[..r.Next(bytes.Length)];  // truncated
            else bytes[r.Next(bytes.Length)] ^= (byte)(1 << r.Next(8)); // one bit flipped
            try { FastBson.Decode(bytes); }
            catch (FormatException) { } // the only acceptable failure; a flip can also land on a value byte and decode
        }
    }

    [Fact]
    public void Canonical_groups_exactly_like_the_drivers_sorted_relaxed_ejson()
    {
        var r = new Random(13);
        var docs = Enumerable.Range(0, 400).Select(_ => RandomDoc(new Random(r.Next(60)), 0)).ToList(); // a small seed space: many duplicates
        var byText = docs.Select(DriverCanonical).ToList();
        var byBytes = docs.Select(d => Convert.ToBase64String(CanonicalJson.Bytes(d))).ToList();
        for (var i = 0; i < docs.Count; i++)
            for (var j = i + 1; j < docs.Count; j++)
                Assert.True((byText[i] == byText[j]) == (byBytes[i] == byBytes[j]), $"{byText[i]} vs {byText[j]}");
        Assert.True(byText.Distinct().Count() < docs.Count, "the sample must contain equal pairs");
        // key order never matters; arrays keep theirs
        Assert.Equal(CanonicalJson.String(new BsonDocument { ["b"] = 1, ["a"] = 2 }), CanonicalJson.String(new BsonDocument { ["a"] = 2, ["b"] = 1 }));
        Assert.NotEqual(CanonicalJson.String(new BsonArray { 1, 2 }), CanonicalJson.String(new BsonArray { 2, 1 }));
        Assert.NotEqual(CanonicalJson.String(new BsonInt32(1)), CanonicalJson.String(new BsonDouble(1)));
    }
}
