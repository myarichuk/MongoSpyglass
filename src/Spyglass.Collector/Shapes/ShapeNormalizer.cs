using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MongoDB.Bson;

namespace Spyglass.Collector.Shapes;

/// <param name="ShapeJson">null when the caller said it already knows the shape.</param>
public sealed record ShapeResult(string ShapeHash, string? ValueHash, int InCardinality, string? ShapeJson);

/// <summary>
/// 03 §4: the canonical shape of a command, computed only here, from the redacted command BSON the proxy exported
/// (01 §9). Understands the proxy's encodings of what it reduced: leaf tags already applied (<c>values: shape</c>),
/// truncated strings, <c>{$binData:{subtype,length}}</c>, and arrays capped with a trailing <c>{$more:n}</c>. Kind-1
/// sequences arrive as arrays holding their first document.
/// <para>
/// Written straight from the BSON bytes into the canonical JSON that is hashed: no document is decoded, no shape tree
/// built. Keys that come from the command are sorted before they are written; <c>$and</c>/<c>$or</c>/<c>$nor</c> clauses
/// and update/delete statements are rendered to scratch buffers, then sorted and deduplicated by their bytes. The
/// tree-building version this replaced is the test suite's oracle.
/// </para>
/// </summary>
public static class ShapeNormalizer
{
    private enum Mode : byte { Shape, Value }

    /// <summary>01 §9.1: the proxy sends these as exactly <c>{$redacted: name}</c>; their shape is the command name.</summary>
    public static bool IsRedactedCommand(string command) => command is "authenticate" or "saslStart" or "saslContinue" or "getnonce" or
        "createUser" or "updateUser" or "copydbgetnonce" or "copydbsaslstart" or "copydb";

    /// <summary>Shape hashes repeat (an application has hundreds of shapes, not millions): their hex strings are cached by value.</summary>
    [ThreadStatic] private static Dictionary<ulong, string>? t_hashes;
    [ThreadStatic] private static Writer? t_writer;

    /// <summary>Tests and tools: the same from a decoded document.</summary>
    public static ShapeResult Normalize(string command, string? db, string? coll, BsonDocument? cmd, bool cmdTruncated, bool valuesPreserved,
        HashSet<string>? knownShapes = null) =>
        Normalize(command, db, coll, cmd == null ? default : cmd.ToBson(), cmdTruncated, valuesPreserved, knownShapes);

    /// <param name="cmd">the command's BSON, validated (<see cref="BsonSpan.Validate"/>); empty when there is none.</param>
    /// <param name="valuesPreserved">false when the proxy ran <c>values: shape</c>: no valueHash (03 §4).</param>
    /// <param name="knownShapes">shape hashes the caller has the JSON of already (added to here): no JSON is built for them.</param>
    public static ShapeResult Normalize(string command, string? db, string? coll, ReadOnlySpan<byte> cmd, bool cmdTruncated, bool valuesPreserved,
        HashSet<string>? knownShapes = null)
    {
        if (IsRedactedCommand(command)) cmd = default;
        var w = t_writer ??= new Writer();
        var canonical = w.Run(Mode.Shape, command, db, coll, cmd);
        var inCard = w.InCard;
        var shapeHash = Hash(canonical, cache: true);
        var shapeJson = knownShapes == null || knownShapes.Add(shapeHash) ? Encoding.UTF8.GetString(canonical) : null;
        string? valueHash = null;
        if (valuesPreserved && !cmdTruncated && !cmd.IsEmpty)
            valueHash = Hash(w.Run(Mode.Value, command, db, coll, cmd), cache: false);
        return new ShapeResult(shapeHash, valueHash, inCard, shapeJson);
    }

    /// <summary>Compact relaxed Extended JSON with keys sorted at every level (03 §4).</summary>
    public static string Canonical(BsonValue v) => CanonicalJson.String(v);

    private static string Hash(ReadOnlySpan<byte> canonical, bool cache)
    {
        Span<byte> h = stackalloc byte[32];
        SHA256.HashData(canonical, h);
        if (!cache) return Convert.ToHexStringLower(h[..8]);
        var key = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(h);
        var hashes = t_hashes ??= [];
        if (hashes.TryGetValue(key, out var s)) return s;
        if (hashes.Count >= 65_536) hashes.Clear();
        return hashes[key] = Convert.ToHexStringLower(h[..8]);
    }

    /// <summary>
    /// The JSON writer over a buffer, and scratch writers for what must be rendered before it can be ordered. One per
    /// thread, reused: nothing allocated per command once warm.
    /// </summary>
    private sealed class Writer
    {
        private readonly ArrayBufferWriter<byte> _out = new(512);
        private readonly Utf8JsonWriter _w;
        private readonly List<(ArrayBufferWriter<byte> Buffer, Utf8JsonWriter Writer)> _scratch = [];
        private int _scratchDepth;
        private Mode _mode;
        public int InCard;

        private static readonly JsonWriterOptions Options = new() { SkipValidation = true };

        public Writer() => _w = new Utf8JsonWriter(_out, Options);

        /// <summary>The canonical bytes, valid until the next call.</summary>
        public ReadOnlySpan<byte> Run(Mode mode, string command, string? db, string? coll, ReadOnlySpan<byte> cmd)
        {
            _mode = mode;
            InCard = 0;
            _scratchDepth = 0;
            _out.ResetWrittenCount();
            _w.Reset(_out);
            Command(_w, command, db, coll, cmd);
            _w.Flush();
            return _out.WrittenSpan;
        }

        private bool Shape => _mode == Mode.Shape;

        // ---- per command (03 §4.3); top-level keys written in their sorted order -----------------------------------

        private void Command(Utf8JsonWriter w, string command, string? db, string? coll, ReadOnlySpan<byte> cmd)
        {
            w.WriteStartObject();
            if (cmd.IsEmpty)
            {
                Common(w, command, db, coll);
                w.WriteEndObject();
                return;
            }
            switch (command)
            {
                case "find":
                {
                    // batchSize cmd coll collation db filter hint limit projection skip sort
                    Presence(w, cmd, "batchSize"u8);
                    w.WriteString("cmd"u8, command);
                    Nullable(w, "coll"u8, coll);
                    if (BsonSpan.TryGet(cmd, "collation"u8, out var col) && col.IsDocument && BsonSpan.TryGet(col.Value, "locale"u8, out var locale))
                    {
                        w.WriteStartObject("collation"u8);
                        w.WritePropertyName("locale"u8);
                        BsonSpan.WriteValue(w, locale.Type, locale.Value);
                        w.WriteEndObject();
                    }
                    Nullable(w, "db"u8, db);
                    if (BsonSpan.TryGet(cmd, "filter"u8, out var f) && f.IsDocument) { w.WritePropertyName("filter"u8); Filter(w, f.Value); }
                    if (BsonSpan.TryGet(cmd, "hint"u8, out var hint))
                    {
                        w.WritePropertyName("hint"u8);
                        if (hint.IsDocument) Ordered(w, hint.Value); // a key pattern, or an index name
                        else BsonSpan.WriteValue(w, hint.Type, hint.Value);
                    }
                    Presence(w, cmd, "limit"u8);
                    if (BsonSpan.TryGet(cmd, "projection"u8, out var proj) && proj.IsDocument) { w.WritePropertyName("projection"u8); BsonSpan.WriteDocument(w, proj.Value); }
                    Presence(w, cmd, "skip"u8);
                    if (BsonSpan.TryGet(cmd, "sort"u8, out var sort) && sort.IsDocument) { w.WritePropertyName("sort"u8); Ordered(w, sort.Value); }
                    break;
                }
                case "aggregate":
                    Common(w, command, db, coll);
                    if (BsonSpan.TryGet(cmd, "pipeline"u8, out var p) && p.IsArray) { w.WritePropertyName("pipeline"u8); Pipeline(w, p.Value); }
                    break;
                case "update":
                    Common(w, command, db, coll);
                    if (BsonSpan.TryGet(cmd, "updates"u8, out var ups) && ups.IsArray) { w.WritePropertyName("updates"u8); Statements(w, ups.Value, delete: false); }
                    break;
                case "delete":
                    // cmd coll db deletes: "deletes" sorts after "db"
                    Common(w, command, db, coll);
                    if (BsonSpan.TryGet(cmd, "deletes"u8, out var dels) && dels.IsArray) { w.WritePropertyName("deletes"u8); Statements(w, dels.Value, delete: true); }
                    break;
                case "findAndModify" or "findandmodify":
                {
                    // cmd coll db new query remove sort update upsert
                    Common(w, command, db, coll);
                    Raw(w, cmd, "new"u8);
                    if (BsonSpan.TryGet(cmd, "query"u8, out var q) && q.IsDocument) { w.WritePropertyName("query"u8); Filter(w, q.Value); }
                    Raw(w, cmd, "remove"u8);
                    if (BsonSpan.TryGet(cmd, "sort"u8, out var fs) && fs.IsDocument) { w.WritePropertyName("sort"u8); Ordered(w, fs.Value); }
                    if (BsonSpan.TryGet(cmd, "update"u8, out var u)) { w.WritePropertyName("update"u8); Update(w, u); }
                    Raw(w, cmd, "upsert"u8);
                    break;
                }
                case "count":
                    Common(w, command, db, coll);
                    if (BsonSpan.TryGet(cmd, "query"u8, out var cq) && cq.IsDocument) { w.WritePropertyName("query"u8); Filter(w, cq.Value); }
                    break;
                case "distinct":
                    // cmd coll db key query
                    Common(w, command, db, coll);
                    Raw(w, cmd, "key"u8);
                    if (BsonSpan.TryGet(cmd, "query"u8, out var dq) && dq.IsDocument) { w.WritePropertyName("query"u8); Filter(w, dq.Value); }
                    break;
                default:
                    Common(w, command, db, coll);
                    break;
            }
            w.WriteEndObject();
        }

        private static void Common(Utf8JsonWriter w, string command, string? db, string? coll)
        {
            w.WriteString("cmd"u8, command);
            Nullable(w, "coll"u8, coll);
            Nullable(w, "db"u8, db);
        }

        private static void Nullable(Utf8JsonWriter w, ReadOnlySpan<byte> name, string? v)
        {
            if (v == null) w.WriteNull(name);
            else w.WriteString(name, v);
        }

        /// <summary>limit/skip/batchSize: presence as "?" (shape), the value (value mode).</summary>
        private void Presence(Utf8JsonWriter w, ReadOnlySpan<byte> cmd, ReadOnlySpan<byte> name)
        {
            if (!BsonSpan.TryGet(cmd, name, out var e)) return;
            w.WritePropertyName(name);
            if (Shape) w.WriteStringValue("?"u8);
            else BsonSpan.WriteValue(w, e.Type, e.Value);
        }

        private static void Raw(Utf8JsonWriter w, ReadOnlySpan<byte> doc, ReadOnlySpan<byte> name)
        {
            if (!BsonSpan.TryGet(doc, name, out var e)) return;
            w.WritePropertyName(name);
            BsonSpan.WriteValue(w, e.Type, e.Value);
        }

        /// <summary>Key order that matters (sort keys, index key patterns): <c>[[key, value], …]</c>, since the canonical form sorts keys.</summary>
        private static void Ordered(Utf8JsonWriter w, ReadOnlySpan<byte> doc)
        {
            w.WriteStartArray();
            foreach (var e in new BsonElements(doc))
            {
                w.WriteStartArray();
                BsonSpan.WriteString(w, e.Name);
                BsonSpan.WriteValue(w, e.Type, e.Value);
                w.WriteEndArray();
            }
            w.WriteEndArray();
        }

        // ---- documents the normalizer builds from the command's keys -------------------------------------------------

        private enum Members : byte { Filter, Operators, TagTree, Projection, UpdateFields, UpdateDoc }

        /// <summary>
        /// An object of the document's members in sorted name order, each value normalized by <paramref name="kind"/>. A name
        /// that repeats keeps its last value (what assigning into a BsonDocument does).
        /// </summary>
        private void Object(Utf8JsonWriter w, ReadOnlySpan<byte> doc, Members kind)
        {
            var count = BsonSpan.Count(doc);
            BsonSpan.Entry[]? rented = null;
            var entries = count <= 32 ? stackalloc BsonSpan.Entry[count] : (rented = ArrayPool<BsonSpan.Entry>.Shared.Rent(count));
            try
            {
                var n = BsonSpan.Sorted(doc, entries);
                w.WriteStartObject();
                for (var i = 0; i < n; i++)
                {
                    var x = entries[i];
                    var name = doc.Slice(x.NameStart, x.NameLength);
                    if (i + 1 < n && BsonSpan.NamesEqual(name, doc.Slice(entries[i + 1].NameStart, entries[i + 1].NameLength)))
                    {
                        // last wins, but the shadowed one is still normalized (into a discarded buffer): what it counts
                        // toward inCardinality counts, as when a BsonDocument member is assigned twice
                        var (_, discard) = RentScratch();
                        try { Member(discard, kind, new BsonEl(x.Type, name, doc.Slice(x.ValueStart, x.ValueLength))); }
                        finally { discard.Flush(); ReturnScratch(); }
                        continue;
                    }
                    BsonSpan.WritePropertyName(w, name);
                    Member(w, kind, new BsonEl(x.Type, name, doc.Slice(x.ValueStart, x.ValueLength)));
                }
                w.WriteEndObject();
            }
            finally
            {
                if (rented != null) ArrayPool<BsonSpan.Entry>.Shared.Return(rented);
            }
        }

        private void Member(Utf8JsonWriter w, Members kind, in BsonEl e)
        {
            switch (kind)
            {
                case Members.Filter: FilterMember(w, e); return;
                case Members.Operators: Operator(w, e); return;
                case Members.TagTree: TagTree(w, e.Type, e.Value); return;
                case Members.Projection:
                    if (e.Type is BsonSpan.Boolean or BsonSpan.Int32 or BsonSpan.Int64 or BsonSpan.Double) BsonSpan.WriteValue(w, e.Type, e.Value);
                    else if (e.IsDocument && BsonSpan.First(e.Value, out var f) && !f.NameStartsWithDollar) Object(w, e.Value, Members.Projection);
                    else TagTree(w, e.Type, e.Value);
                    return;
                case Members.UpdateFields:
                    if (Shape) Leaf(w, e.Type, e.Value);
                    else BsonSpan.WriteValue(w, e.Type, e.Value);
                    return;
                case Members.UpdateDoc:
                    if (e.NameStartsWithDollar && e.IsDocument) Object(w, e.Value, Members.UpdateFields);
                    else TagTree(w, e.Type, e.Value); // replacement document
                    return;
            }
        }

        // ---- filters (03 §4.2) -----------------------------------------------------------------------------------------

        private void Filter(Utf8JsonWriter w, ReadOnlySpan<byte> doc) => Object(w, doc, Members.Filter);

        private void FilterMember(Utf8JsonWriter w, in BsonEl e)
        {
            if ((e.NameIs("$and"u8) || e.NameIs("$or"u8) || e.NameIs("$nor"u8)) && e.IsArray)
            {
                Clauses(w, e.Value);
                return;
            }
            if (e.IsDocument && BsonSpan.First(e.Value, out var first) && first.NameStartsWithDollar && !IsBinDataStub(e.Value))
                Object(w, e.Value, Members.Operators);
            else
                Leaf(w, e.Type, e.Value);
        }

        /// <summary><c>$and</c>/<c>$or</c>/<c>$nor</c>: each clause normalized; in shape mode sorted by canonical form and deduplicated.</summary>
        private void Clauses(Utf8JsonWriter w, ReadOnlySpan<byte> array)
        {
            if (!Shape)
            {
                w.WriteStartArray();
                foreach (var c in Items(array))
                    if (c.IsDocument) Filter(w, c.Value);
                w.WriteEndArray();
                return;
            }
            var (buf, sw) = RentScratch();
            try
            {
                var list = new RangeList(stackalloc (int, int)[32]);
                foreach (var c in Items(array))
                {
                    if (!c.IsDocument) continue;
                    var start = Mark(buf, sw);
                    Filter(sw, c.Value);
                    sw.Flush();
                    list.Add((start, buf.WrittenCount - start));
                }
                var written = buf.WrittenSpan;
                var items = list.Span;
                // sorted by bytes (the canonical form is ASCII: the writer escapes the rest), then deduplicated
                var order = items.Length <= 64 ? stackalloc int[items.Length] : new int[items.Length];
                SortByBytes(written, items, order);
                w.WriteStartArray();
                for (var i = 0; i < order.Length; i++)
                {
                    var s = written.Slice(items[order[i]].Start, items[order[i]].Length);
                    if (i > 0 && s.SequenceEqual(written.Slice(items[order[i - 1]].Start, items[order[i - 1]].Length))) continue;
                    w.WriteRawValue(s, skipInputValidation: true);
                }
                w.WriteEndArray();
                list.Dispose();
            }
            finally
            {
                ReturnScratch();
            }
        }

        private void Operator(Utf8JsonWriter w, in BsonEl e)
        {
            var name = e.Name;
            if (name.SequenceEqual("$in"u8) || name.SequenceEqual("$nin"u8) || name.SequenceEqual("$all"u8))
            {
                if (e.IsArray) InCard = Math.Max(InCard, Length(e.Value));
                if (Shape) { w.WriteStartArray(); w.WriteStringValue("?"u8); w.WriteEndArray(); }
                else BsonSpan.WriteValue(w, e.Type, e.Value);
            }
            else if (name.SequenceEqual("$exists"u8) || name.SequenceEqual("$type"u8))
                BsonSpan.WriteValue(w, e.Type, e.Value); // semantically significant
            else if (name.SequenceEqual("$regex"u8) || name.SequenceEqual("$options"u8))
            {
                if (Shape) w.WriteStringValue("?regex"u8);
                else BsonSpan.WriteValue(w, e.Type, e.Value);
            }
            else if (name.SequenceEqual("$elemMatch"u8))
            {
                if (!e.IsDocument) Leaf(w, e.Type, e.Value);
                else if (BsonSpan.First(e.Value, out var f) && f.NameStartsWithDollar) Object(w, e.Value, Members.Operators);
                else Filter(w, e.Value);
            }
            else if (name.SequenceEqual("$not"u8))
            {
                if (e.IsDocument) Object(w, e.Value, Members.Operators);
                else Leaf(w, e.Type, e.Value);
            }
            else if (name.SequenceEqual("$size"u8))
            {
                if (Shape) w.WriteStringValue("?number"u8);
                else BsonSpan.WriteValue(w, e.Type, e.Value);
            }
            else Leaf(w, e.Type, e.Value);
        }

        // ---- updates and statements --------------------------------------------------------------------------------------

        /// <summary>Operator update: operator → field → tag. Pipeline update: like an aggregate pipeline. Replacement: its leaves tagged.</summary>
        private void Update(Utf8JsonWriter w, in BsonEl u)
        {
            if (u.IsArray) Pipeline(w, u.Value);
            else if (!u.IsDocument) Leaf(w, u.Type, u.Value);
            else Object(w, u.Value, Members.UpdateDoc);
        }

        /// <summary>Statements in order; in shape mode deduplicated by canonical form, the first of each kept in place (03 §4.3).</summary>
        private void Statements(Utf8JsonWriter w, ReadOnlySpan<byte> array, bool delete)
        {
            if (!Shape)
            {
                w.WriteStartArray();
                foreach (var s in Items(array))
                    if (s.IsDocument) Statement(w, s.Value, delete);
                w.WriteEndArray();
                return;
            }
            var (buf, sw) = RentScratch();
            try
            {
                var list = new RangeList(stackalloc (int, int)[32]);
                foreach (var s in Items(array))
                {
                    if (!s.IsDocument) continue;
                    var start = Mark(buf, sw);
                    Statement(sw, s.Value, delete);
                    sw.Flush();
                    list.Add((start, buf.WrittenCount - start));
                }
                var written = buf.WrittenSpan;
                var items = list.Span;
                // the first occurrence of each: indices sorted by bytes (stable), the later ones of an equal run marked
                var order = items.Length <= 64 ? stackalloc int[items.Length] : new int[items.Length];
                SortByBytes(written, items, order);
                var dup = items.Length <= 256 ? stackalloc bool[items.Length] : new bool[items.Length];
                for (var i = 1; i < order.Length; i++)
                    if (written.Slice(items[order[i]].Start, items[order[i]].Length).SequenceEqual(written.Slice(items[order[i - 1]].Start, items[order[i - 1]].Length)))
                        dup[order[i]] = true;
                w.WriteStartArray();
                for (var i = 0; i < items.Length; i++)
                    if (!dup[i]) w.WriteRawValue(written.Slice(items[i].Start, items[i].Length), skipInputValidation: true);
                w.WriteEndArray();
                list.Dispose();
            }
            finally
            {
                ReturnScratch();
            }
        }

        private void Statement(Utf8JsonWriter w, ReadOnlySpan<byte> s, bool delete)
        {
            w.WriteStartObject();
            if (delete)
            {
                // limit q
                Raw(w, s, "limit"u8);
                w.WritePropertyName("q"u8);
                if (BsonSpan.TryGet(s, "q"u8, out var q) && q.IsDocument) Filter(w, q.Value);
                else { w.WriteStartObject(); w.WriteEndObject(); }
            }
            else
            {
                // multi q u upsert
                Raw(w, s, "multi"u8);
                w.WritePropertyName("q"u8);
                if (BsonSpan.TryGet(s, "q"u8, out var q) && q.IsDocument) Filter(w, q.Value);
                else { w.WriteStartObject(); w.WriteEndObject(); }
                if (BsonSpan.TryGet(s, "u"u8, out var u)) { w.WritePropertyName("u"u8); Update(w, u); }
                Raw(w, s, "upsert"u8);
            }
            w.WriteEndObject();
        }

        // ---- pipelines -----------------------------------------------------------------------------------------------------

        private void Pipeline(Utf8JsonWriter w, ReadOnlySpan<byte> array)
        {
            w.WriteStartArray();
            foreach (var st in Items(array))
            {
                if (!st.IsDocument || !BsonSpan.First(st.Value, out var stage)) continue;
                w.WriteStartObject();
                BsonSpan.WritePropertyName(w, stage.Name);
                var name = stage.Name;
                if (name.SequenceEqual("$match"u8) && stage.IsDocument) Filter(w, stage.Value);
                else if (name.SequenceEqual("$limit"u8) || name.SequenceEqual("$skip"u8))
                {
                    if (Shape) w.WriteStringValue("?"u8);
                    else BsonSpan.WriteValue(w, stage.Type, stage.Value);
                }
                else if (name.SequenceEqual("$sort"u8) && stage.IsDocument) Ordered(w, stage.Value);
                else if (name.SequenceEqual("$lookup"u8) && stage.IsDocument) Lookup(w, stage.Value);
                else if (name.SequenceEqual("$group"u8) && stage.IsDocument) Object(w, stage.Value, Members.TagTree);
                else if (name.SequenceEqual("$project"u8) && stage.IsDocument) Object(w, stage.Value, Members.Projection);
                else TagTree(w, stage.Type, stage.Value);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }

        private void Lookup(Utf8JsonWriter w, ReadOnlySpan<byte> l)
        {
            // as foreignField from let localField pipeline
            w.WriteStartObject();
            Raw(w, l, "as"u8);
            Raw(w, l, "foreignField"u8);
            Raw(w, l, "from"u8);
            if (BsonSpan.TryGet(l, "let"u8, out var let)) { w.WritePropertyName("let"u8); TagTree(w, let.Type, let.Value); }
            Raw(w, l, "localField"u8);
            if (BsonSpan.TryGet(l, "pipeline"u8, out var p) && p.IsArray) { w.WritePropertyName("pipeline"u8); Pipeline(w, p.Value); }
            w.WriteEndObject();
        }

        /// <summary>Structure kept (keys, operator names, field-path strings), other leaves tagged; in value mode, the value itself.</summary>
        private void TagTree(Utf8JsonWriter w, byte type, ReadOnlySpan<byte> v)
        {
            if (!Shape) { BsonSpan.WriteValue(w, type, v); return; }
            switch (type)
            {
                case BsonSpan.Document when IsBinDataStub(v):
                    Leaf(w, type, v);
                    return;
                case BsonSpan.Document:
                    Object(w, v, Members.TagTree);
                    return;
                case BsonSpan.Array:
                    w.WriteStartArray();
                    foreach (var x in Items(v)) TagTree(w, x.Type, x.Value);
                    w.WriteEndArray();
                    return;
                case BsonSpan.String or BsonSpan.Symbol or BsonSpan.DbPointer:
                {
                    var s = StringOf(type, v);
                    if (s.Length > 0 && s[0] == (byte)'$' && !s.StartsWith("$redacted"u8)) { BsonSpan.WriteString(w, s); return; } // a field path: structure
                    Leaf(w, type, v);
                    return;
                }
                default:
                    Leaf(w, type, v);
                    return;
            }
        }

        // ---- leaves (03 §4.1) --------------------------------------------------------------------------------------------

        private void Leaf(Utf8JsonWriter w, byte type, ReadOnlySpan<byte> v)
        {
            if (!Shape) { BsonSpan.WriteValue(w, type, v); return; }
            w.WriteStringValue(type switch
            {
                BsonSpan.Int32 or BsonSpan.Int64 or BsonSpan.Double or BsonSpan.Decimal => "?number"u8,
                BsonSpan.String or BsonSpan.Symbol or BsonSpan.DbPointer => StringTag(StringOf(type, v)),
                BsonSpan.ObjectId => "?objectId"u8,
                BsonSpan.DateTime => "?date"u8,
                BsonSpan.Boolean => "?bool"u8,
                BsonSpan.Null or BsonSpan.Undefined => "?null"u8,
                BsonSpan.Binary => v[4] == 4 ? "?uuid"u8 : "?binData"u8,
                BsonSpan.Regex => "?regex"u8,
                BsonSpan.Timestamp => "?timestamp"u8,
                BsonSpan.Document when IsBinDataStub(v) => StubTag(v),
                BsonSpan.Document => "?document"u8,
                BsonSpan.Array => "?array"u8,
                _ => "?other"u8,
            });
        }

        private static ReadOnlySpan<byte> StubTag(ReadOnlySpan<byte> stub)
        {
            BsonSpan.First(stub, out var inner);
            return BsonSpan.TryGet(inner.Value, "subtype"u8, out var st) && st.IsNumeric && BsonSpan.ToInt32(st) == 4 ? "?uuid"u8 : "?binData"u8;
        }

        private static ReadOnlySpan<byte> StringOf(byte type, ReadOnlySpan<byte> v) =>
            v.Slice(4, System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(v) - 1); // a DBPointer's namespace is its string

        /// <summary>Tags the proxy applied already (values: shape) pass through; "$redacted" is ?redacted.</summary>
        private static ReadOnlySpan<byte> StringTag(ReadOnlySpan<byte> s)
        {
            if (s.SequenceEqual("$redacted"u8)) return "?redacted"u8;
            if (s.Length > 1 && s[0] == (byte)'?')
                foreach (var tag in LeafTags)
                    if (s.SequenceEqual(tag)) return s;
            return "?string"u8;
        }

        private static readonly byte[][] LeafTags =
            [.. ((string[])["?number", "?string", "?objectId", "?date", "?bool", "?null", "?binData", "?uuid", "?regex", "?redacted", "?timestamp", "?other", "?document", "?array"]).Select(Encoding.ASCII.GetBytes)];

        /// <summary>01 §9.2 truncated mode reduces binaries to <c>{$binData:{subtype,length}}</c>.</summary>
        private static bool IsBinDataStub(ReadOnlySpan<byte> doc)
        {
            var e = new BsonElements(doc);
            if (!e.MoveNext()) return false;
            var first = e.Current;
            return !e.MoveNext() && first.NameIs("$binData"u8) && first.IsDocument;
        }

        // ---- arrays the proxy capped (01 §9.2: 20 kept, then {$more: n}) -----------------------------------------------

        /// <summary>Array elements without the proxy's trailing <c>{$more:n}</c> marker.</summary>
        private static ItemEnumerator Items(ReadOnlySpan<byte> array) => new(array);

        private ref struct ItemEnumerator(ReadOnlySpan<byte> array)
        {
            private BsonElements _e = new(array);
            private BsonEl _next;
            private bool _hasNext, _started;

            public readonly ItemEnumerator GetEnumerator() => this;
            public BsonEl Current { get; private set; }

            public bool MoveNext()
            {
                if (!_started)
                {
                    _started = true;
                    _hasNext = _e.MoveNext();
                    if (_hasNext) _next = _e.Current;
                }
                if (!_hasNext) return false;
                var cur = _next;
                _hasNext = _e.MoveNext();
                if (_hasNext) _next = _e.Current;
                else if (IsMore(cur)) return false; // the last one, and a marker
                Current = cur;
                return true;
            }
        }

        private static bool IsMore(in BsonEl v)
        {
            if (!v.IsDocument) return false;
            var e = new BsonElements(v.Value);
            if (!e.MoveNext()) return false;
            var only = e.Current;
            return !e.MoveNext() && only.NameIs("$more"u8) && only.IsNumeric;
        }

        /// <summary>Array length including the elements the proxy cut.</summary>
        private static int Length(ReadOnlySpan<byte> array)
        {
            var n = 0;
            BsonEl last = default;
            foreach (var e in new BsonElements(array)) { n++; last = e; }
            if (n > 0 && IsMore(last))
            {
                BsonSpan.First(last.Value, out var more);
                return n - 1 + BsonSpan.ToInt32(more);
            }
            return n;
        }

        /// <summary>
        /// <paramref name="order"/> := the indices of <paramref name="items"/>, sorted by their bytes, equal ones in index order.
        /// A bottom-up merge sort: a bulk write's thousands of statements must not cost n².
        /// </summary>
        private static void SortByBytes(ReadOnlySpan<byte> written, ReadOnlySpan<(int Start, int Length)> items, Span<int> order)
        {
            for (var i = 0; i < order.Length; i++) order[i] = i;
            if (order.Length < 2) return;
            var rented = ArrayPool<int>.Shared.Rent(order.Length);
            var tmp = rented.AsSpan(0, order.Length);
            try
            {
                var src = order;
                var dst = tmp;
                for (var width = 1; width < order.Length; width *= 2)
                {
                    for (var lo = 0; lo < order.Length; lo += 2 * width)
                    {
                        int mid = Math.Min(lo + width, order.Length), hi = Math.Min(lo + 2 * width, order.Length);
                        int a = lo, b = mid, k = lo;
                        while (a < mid && b < hi)
                        {
                            var c = written.Slice(items[src[a]].Start, items[src[a]].Length).SequenceCompareTo(written.Slice(items[src[b]].Start, items[src[b]].Length));
                            dst[k++] = c <= 0 ? src[a++] : src[b++]; // ties from the left half: stable
                        }
                        while (a < mid) dst[k++] = src[a++];
                        while (b < hi) dst[k++] = src[b++];
                    }
                    var t = src;
                    src = dst;
                    dst = t;
                }
                if (src != order) src.CopyTo(order);
            }
            finally
            {
                ArrayPool<int>.Shared.Return(rented);
            }
        }

        // ---- scratch buffers for what must be rendered before it is ordered ----------------------------------------------

        private (ArrayBufferWriter<byte>, Utf8JsonWriter) RentScratch()
        {
            if (_scratchDepth == _scratch.Count)
            {
                var b = new ArrayBufferWriter<byte>(256);
                _scratch.Add((b, new Utf8JsonWriter(b, Options)));
            }
            var s = _scratch[_scratchDepth++];
            s.Buffer.ResetWrittenCount();
            s.Writer.Reset(s.Buffer);
            return s;
        }

        private void ReturnScratch() => _scratchDepth--;

        /// <summary>Starts a new rendering at the end of the scratch buffer (a reset writer adds no separator).</summary>
        private static int Mark(ArrayBufferWriter<byte> buf, Utf8JsonWriter w)
        {
            w.Flush();
            w.Reset(buf);
            return buf.WrittenCount;
        }
    }

    /// <summary>A growable list of ranges on the stack, spilling to the pool.</summary>
    private ref struct RangeList(Span<(int Start, int Length)> initial)
    {
        private Span<(int Start, int Length)> _items = initial;
        private (int, int)[]? _rented;
        private int _count;

        public readonly Span<(int Start, int Length)> Span => _items[.._count];

        public void Add((int, int) r)
        {
            if (_count == _items.Length)
            {
                var bigger = ArrayPool<(int, int)>.Shared.Rent(_items.Length * 2);
                _items.CopyTo(bigger);
                if (_rented != null) ArrayPool<(int, int)>.Shared.Return(_rented);
                _items = _rented = bigger;
            }
            _items[_count++] = r;
        }

        public void Dispose()
        {
            if (_rented != null) ArrayPool<(int, int)>.Shared.Return(_rented);
            _rented = null;
        }
    }
}
