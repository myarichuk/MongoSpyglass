using System.Security.Cryptography;
using System.Text;
using MongoDB.Bson;

using Spyglass.Collector.Shapes;

namespace Spyglass.Collector.Tests;

public sealed record ReferenceShape(BsonDocument Shape, string ShapeHash, string? ValueHash, int InCardinality, string ShapeJson);

/// <summary>
/// The tree-building normalizer the streaming one replaced, kept as the oracle of a differential test.
/// 03 §4: the canonical shape of a command, computed only here, from the redacted command BSON the
/// proxy exported (01 §9). Understands the proxy's encodings of what it reduced: leaf tags already
/// applied (<c>values: shape</c>), truncated strings, <c>{$binData:{subtype,length}}</c>, and arrays
/// capped with a trailing <c>{$more:n}</c>. Kind-1 sequences arrive as arrays holding their first document.
/// </summary>
public static class ReferenceShapeNormalizer
{
    private enum Mode : byte { Shape, Value }

    private sealed class Ctx(Mode mode)
    {
        public readonly Mode Mode = mode;
        public int InCard;
    }

    private static readonly HashSet<string> LeafTags =
        ["?number", "?string", "?objectId", "?date", "?bool", "?null", "?binData", "?uuid", "?regex", "?redacted", "?timestamp", "?other", "?document", "?array"];


    /// <summary>01 §9.1: the proxy sends these as exactly <c>{$redacted: name}</c>; their shape is the command name.</summary>
    public static bool IsRedactedCommand(string command) => command is "authenticate" or "saslStart" or "saslContinue" or "getnonce" or
        "createUser" or "updateUser" or "copydbgetnonce" or "copydbsaslstart" or "copydb";

    /// <param name="valuesPreserved">false when the proxy ran <c>values: shape</c>: no valueHash (03 §4).</param>
    public static ReferenceShape Normalize(string command, string? db, string? coll, BsonDocument? cmd, bool cmdTruncated, bool valuesPreserved)
    {
        if (IsRedactedCommand(command)) cmd = null;
        var ctx = new Ctx(Mode.Shape);
        var shape = Build(ctx, command, db, coll, cmd);
        var canonical = CanonicalJson.Bytes(shape);
        var shapeHash = Hash(canonical);
        var shapeJson = Encoding.UTF8.GetString(canonical);
        string? valueHash = null;
        if (valuesPreserved && !cmdTruncated && cmd != null)
            valueHash = Hash(CanonicalJson.Bytes(Build(new Ctx(Mode.Value), command, db, coll, cmd)));
        return new ReferenceShape(shape, shapeHash, valueHash, ctx.InCard, shapeJson);
    }

    private static BsonDocument Build(Ctx c, string command, string? db, string? coll, BsonDocument? cmd)
    {
        var s = new BsonDocument { ["cmd"] = command, ["db"] = (BsonValue?)db ?? BsonNull.Value, ["coll"] = (BsonValue?)coll ?? BsonNull.Value };
        if (cmd == null) return s;
        switch (command)
        {
            case "find":
                if (Get(cmd, "filter") is BsonDocument f) s["filter"] = NormFilter(c, f);
                if (Get(cmd, "sort") is BsonDocument sort) s["sort"] = Ordered(sort);
                if (Get(cmd, "projection") is BsonDocument proj) s["projection"] = proj;
                foreach (var k in (string[])["limit", "skip", "batchSize"])
                    if (cmd.Contains(k)) s[k] = c.Mode == Mode.Shape ? "?" : cmd[k];
                if (Get(cmd, "hint") is { } hint) s["hint"] = hint is BsonDocument keys ? Ordered(keys) : hint; // a key pattern, or an index name
                if (Get(cmd, "collation") is BsonDocument col && col.Contains("locale")) s["collation"] = new BsonDocument("locale", col["locale"]);
                break;
            case "aggregate":
                if (Get(cmd, "pipeline") is BsonArray p) s["pipeline"] = NormPipeline(c, p);
                break;
            case "update":
                if (Get(cmd, "updates") is BsonArray ups)
                    s["updates"] = Statements(c, ups, u => NormUpdateStatement(c, u));
                break;
            case "delete":
                if (Get(cmd, "deletes") is BsonArray dels)
                    s["deletes"] = Statements(c, dels, d =>
                    {
                        var r = new BsonDocument { ["q"] = Get(d, "q") is BsonDocument q ? NormFilter(c, q) : new BsonDocument() };
                        if (d.Contains("limit")) r["limit"] = d["limit"];
                        return r;
                    });
                break;
            case "findAndModify" or "findandmodify":
                if (Get(cmd, "query") is BsonDocument fq) s["query"] = NormFilter(c, fq);
                if (Get(cmd, "sort") is BsonDocument fs) s["sort"] = Ordered(fs);
                if (Get(cmd, "update") is { } fu) s["update"] = NormUpdate(c, fu);
                foreach (var k in (string[])["remove", "new", "upsert"])
                    if (cmd.Contains(k)) s[k] = cmd[k];
                break;
            case "count":
                if (Get(cmd, "query") is BsonDocument cq) s["query"] = NormFilter(c, cq);
                break;
            case "distinct":
                if (Get(cmd, "query") is BsonDocument dq) s["query"] = NormFilter(c, dq);
                if (Get(cmd, "key") is { } key) s["key"] = key;
                break;
        }
        return s;
    }

    private static BsonValue? Get(BsonDocument d, string name) => d.TryGetValue(name, out var v) ? v : null;

    /// <summary>Statements deduplicated by canonical form (03 §4.3); value mode keeps them all, in order.</summary>
    private static BsonArray Statements(Ctx c, BsonArray items, Func<BsonDocument, BsonDocument> norm)
    {
        var result = new BsonArray();
        var seen = new HashSet<string>();
        foreach (var item in Elements(items))
        {
            if (item is not BsonDocument d) continue;
            var n = norm(d);
            if (c.Mode == Mode.Value || seen.Add(Canonical(n))) result.Add(n);
        }
        return result;
    }

    private static BsonDocument NormUpdateStatement(Ctx c, BsonDocument u)
    {
        var r = new BsonDocument { ["q"] = Get(u, "q") is BsonDocument q ? NormFilter(c, q) : new BsonDocument() };
        if (Get(u, "u") is { } upd) r["u"] = NormUpdate(c, upd);
        if (u.Contains("upsert")) r["upsert"] = u["upsert"];
        if (u.Contains("multi")) r["multi"] = u["multi"];
        return r;
    }

    /// <summary>Operator update: operator → field → tag. Pipeline update: like an aggregate pipeline. Replacement: its leaves tagged.</summary>
    private static BsonValue NormUpdate(Ctx c, BsonValue u)
    {
        if (u is BsonArray pipeline) return NormPipeline(c, pipeline);
        if (u is not BsonDocument d) return Leaf(c, u);
        var r = new BsonDocument();
        foreach (var e in d)
        {
            if (e.Name.StartsWith('$') && e.Value is BsonDocument fields)
            {
                var f = new BsonDocument();
                foreach (var fe in fields) f[fe.Name] = c.Mode == Mode.Shape ? Leaf(c, fe.Value) : fe.Value;
                r[e.Name] = f;
            }
            else r[e.Name] = TagTree(c, e.Value); // replacement document
        }
        return r;
    }

    // ---- filters (03 §4.2) -----------------------------------------------------------------

    private static BsonDocument NormFilter(Ctx c, BsonDocument filter)
    {
        var r = new BsonDocument();
        foreach (var e in filter)
        {
            if (e.Name is "$and" or "$or" or "$nor" && e.Value is BsonArray clauses)
            {
                var items = new List<BsonDocument>();
                foreach (var clause in Elements(clauses))
                    if (clause is BsonDocument cd) items.Add(NormFilter(c, cd));
                if (c.Mode == Mode.Shape)
                {
                    // sorted by canonical serialization, deduplicated
                    items = items.Select(i => (Key: Canonical(i), Doc: i)).DistinctBy(x => x.Key)
                        .OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => x.Doc).ToList();
                }
                r[e.Name] = new BsonArray(items);
            }
            else if (e.Value is BsonDocument d && d.ElementCount > 0 && d.GetElement(0).Name.StartsWith('$') && !IsBinDataStub(d))
                r[e.Name] = NormOperators(c, d);
            else
                r[e.Name] = Leaf(c, e.Value);
        }
        return r;
    }

    private static BsonDocument NormOperators(Ctx c, BsonDocument ops)
    {
        var r = new BsonDocument();
        foreach (var e in ops)
        {
            switch (e.Name)
            {
                case "$in" or "$nin" or "$all":
                    if (e.Value is BsonArray arr)
                        c.InCard = Math.Max(c.InCard, Length(arr));
                    r[e.Name] = c.Mode == Mode.Shape ? new BsonArray { "?" } : e.Value;
                    break;
                case "$exists" or "$type":
                    r[e.Name] = e.Value; // semantically significant
                    break;
                case "$regex" or "$options":
                    r[e.Name] = c.Mode == Mode.Shape ? "?regex" : e.Value;
                    break;
                case "$elemMatch":
                    r[e.Name] = e.Value is BsonDocument em
                        ? em.ElementCount > 0 && em.GetElement(0).Name.StartsWith('$') ? NormOperators(c, em) : NormFilter(c, em)
                        : Leaf(c, e.Value);
                    break;
                case "$not":
                    r[e.Name] = e.Value is BsonDocument nd ? NormOperators(c, nd) : Leaf(c, e.Value);
                    break;
                case "$size":
                    r[e.Name] = c.Mode == Mode.Shape ? "?number" : e.Value;
                    break;
                default:
                    r[e.Name] = Leaf(c, e.Value);
                    break;
            }
        }
        return r;
    }

    // ---- pipelines ---------------------------------------------------------------------------

    private static BsonArray NormPipeline(Ctx c, BsonArray pipeline)
    {
        var r = new BsonArray();
        foreach (var st in Elements(pipeline))
        {
            if (st is not BsonDocument stage || stage.ElementCount == 0) continue;
            var (name, body) = (stage.GetElement(0).Name, stage.GetElement(0).Value);
            BsonValue norm = name switch
            {
                "$match" when body is BsonDocument m => NormFilter(c, m),
                "$limit" or "$skip" => c.Mode == Mode.Shape ? "?" : body,
                "$sort" when body is BsonDocument sd => Ordered(sd),
                "$lookup" when body is BsonDocument l => NormLookup(c, l),
                "$group" when body is BsonDocument g => NormGroup(c, g),
                "$project" when body is BsonDocument pd => NormProjection(c, pd),
                "$addFields" or "$set" or "$unset" => TagTree(c, body),
                _ => TagTree(c, body),
            };
            r.Add(new BsonDocument(name, norm));
        }
        return r;
    }

    /// <summary>
    /// Key order that matters (sort keys, index key patterns): the canonical form sorts document keys, so these
    /// are written as <c>[[key, value], …]</c> to keep it.
    /// </summary>
    private static BsonArray Ordered(BsonDocument d)
    {
        var r = new BsonArray(d.ElementCount);
        foreach (var e in d) r.Add(new BsonArray { e.Name, e.Value });
        return r;
    }

    /// <summary><c>$project</c>: inclusion flags are structure (kept, like a find projection); expressions have their leaves tagged.</summary>
    private static BsonDocument NormProjection(Ctx c, BsonDocument p)
    {
        var r = new BsonDocument();
        foreach (var e in p)
            r[e.Name] = e.Value switch
            {
                BsonBoolean or BsonInt32 or BsonInt64 or BsonDouble => e.Value,
                BsonDocument sub when sub.ElementCount > 0 && !sub.GetElement(0).Name.StartsWith('$') => NormProjection(c, sub),
                _ => TagTree(c, e.Value),
            };
        return r;
    }

    private static BsonDocument NormLookup(Ctx c, BsonDocument l)
    {
        var r = new BsonDocument();
        foreach (var k in (string[])["from", "localField", "foreignField", "as"])
            if (l.Contains(k)) r[k] = l[k];
        if (Get(l, "pipeline") is BsonArray p) r["pipeline"] = NormPipeline(c, p);
        if (Get(l, "let") is { } let) r["let"] = TagTree(c, let);
        return r;
    }

    /// <summary><c>$group</c>: <c>_id</c> structure kept with leaves tagged; accumulator operators kept.</summary>
    private static BsonDocument NormGroup(Ctx c, BsonDocument g)
    {
        var r = new BsonDocument();
        foreach (var e in g)
            r[e.Name] = TagTree(c, e.Value);
        return r;
    }

    /// <summary>Structure kept (keys, operator names, field-path strings), other leaves tagged.</summary>
    private static BsonValue TagTree(Ctx c, BsonValue v)
    {
        if (c.Mode == Mode.Value) return v;
        switch (v)
        {
            case BsonDocument d when IsBinDataStub(d):
                return Leaf(c, d);
            case BsonDocument d:
            {
                var r = new BsonDocument();
                foreach (var e in d) r[e.Name] = TagTree(c, e.Value);
                return r;
            }
            case BsonArray a:
            {
                var r = new BsonArray();
                foreach (var x in Elements(a)) r.Add(TagTree(c, x));
                return r;
            }
            case BsonString s when s.Value.StartsWith('$') && !s.Value.StartsWith("$redacted", StringComparison.Ordinal):
                return s; // field path / variable reference: structure, not a value
            default:
                return Leaf(c, v);
        }
    }

    // ---- leaves (03 §4.1) --------------------------------------------------------------------

    private static BsonValue Leaf(Ctx c, BsonValue v)
    {
        if (c.Mode == Mode.Value) return v;
        return v.BsonType switch
        {
            BsonType.Int32 or BsonType.Int64 or BsonType.Double or BsonType.Decimal128 => "?number",
            BsonType.String => StringTag(v.AsString),
            BsonType.ObjectId => "?objectId",
            BsonType.DateTime => "?date",
            BsonType.Boolean => "?bool",
            BsonType.Null or BsonType.Undefined => "?null",
            BsonType.Binary => v.AsBsonBinaryData.SubType == BsonBinarySubType.UuidStandard ? "?uuid" : "?binData",
            BsonType.RegularExpression => "?regex",
            BsonType.Timestamp => "?timestamp",
            BsonType.Document when IsBinDataStub(v.AsBsonDocument) => v["$binData"].AsBsonDocument.TryGetValue("subtype", out var st) && st.IsNumeric && st.ToInt32() == 4 ? "?uuid" : "?binData",
            BsonType.Document => "?document",
            BsonType.Array => "?array",
            _ => "?other",
        };
    }

    private static string StringTag(string s) =>
        s == "$redacted" ? "?redacted" : LeafTags.Contains(s) ? s : "?string"; // already shaped by the proxy

    /// <summary>01 §9.2 truncated mode reduces binaries to <c>{$binData:{subtype,length}}</c>.</summary>
    private static bool IsBinDataStub(BsonDocument d) =>
        d.ElementCount == 1 && d.GetElement(0).Name == "$binData" && d.GetElement(0).Value is BsonDocument;

    /// <summary>Array elements without the proxy's trailing <c>{$more:n}</c> marker.</summary>
    private static IEnumerable<BsonValue> Elements(BsonArray a) =>
        a.Count > 0 && IsMore(a[^1]) ? a.Take(a.Count - 1) : a;

    /// <summary>Array length including the elements the proxy cut (01 §9.2: 20 kept, then <c>{$more:n}</c>).</summary>
    private static int Length(BsonArray a) =>
        a.Count > 0 && IsMore(a[^1]) ? a.Count - 1 + a[^1]["$more"].ToInt32() : a.Count;

    private static bool IsMore(BsonValue v) =>
        v is BsonDocument d && d.ElementCount == 1 && d.GetElement(0).Name == "$more" && d.GetElement(0).Value.IsNumeric;

    // ---- canonical form ------------------------------------------------------------------------

    /// <summary>Compact relaxed Extended JSON with keys sorted at every level (03 §4).</summary>
    public static string Canonical(BsonValue v) => CanonicalJson.String(v);

    private static string Hash(ReadOnlySpan<byte> canonical)
    {
        Span<byte> h = stackalloc byte[32];
        SHA256.HashData(canonical, h);
        return Convert.ToHexStringLower(h[..8]);
    }
}
