using System.Buffers.Binary;
using System.Text;
using MongoDB.Bson;
using Spyglass.Proxy.Observation;
using Spyglass.Proxy.Wire;

namespace Spyglass.Proxy.Tests;

/// <summary>01 §10: the streaming scanners must give identical facts for any chunking.</summary>
public sealed class ScannerTests
{
    private static byte[] MsgBody(BsonDocument doc, uint flags = 0, bool checksum = false)
    {
        var d = doc.ToBson();
        var body = new byte[4 + 1 + d.Length + (checksum ? 4 : 0)];
        BinaryPrimitives.WriteUInt32LittleEndian(body, flags | (checksum ? 1u : 0));
        d.CopyTo(body, 5);
        return body;
    }

    private static byte[] ReplyBody(BsonDocument doc)
    {
        var d = doc.ToBson();
        var body = new byte[20 + d.Length];
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(16), 1);
        d.CopyTo(body, 20);
        return body;
    }

    public static TheoryData<string> Replies() => new(
        "find", "getMore", "insertErrors", "update", "error", "killCursors", "oddTypes", "opReply", "deep");

    private static (int OpCode, byte[] Body) Reply(string name) => name switch
    {
        "find" => (OpCodes.Msg, MsgBody(new BsonDocument
        {
            ["cursor"] = new BsonDocument
            {
                ["firstBatch"] = new BsonArray(Enumerable.Range(0, 37).Select(i => new BsonDocument { ["_id"] = i, ["s"] = new string('x', i * 7) })),
                ["id"] = 1234567890123L,
                ["ns"] = "db.users",
            },
            ["ok"] = 1.0,
            ["$clusterTime"] = new BsonDocument { ["clusterTime"] = new BsonTimestamp(1, 2) },
        })),
        "getMore" => (OpCodes.Msg, MsgBody(new BsonDocument
        {
            ["cursor"] = new BsonDocument { ["nextBatch"] = new BsonArray(Enumerable.Range(0, 5).Select(i => new BsonDocument("_id", i))), ["id"] = 0L, ["ns"] = "db.users" },
            ["ok"] = 1,
        }, checksum: true)),
        "insertErrors" => (OpCodes.Msg, MsgBody(new BsonDocument
        {
            ["n"] = 3,
            ["writeErrors"] = new BsonArray { new BsonDocument { ["index"] = 1, ["code"] = 11000, ["errmsg"] = "dup" }, new BsonDocument { ["index"] = 4, ["code"] = 11000 } },
            ["writeConcernError"] = new BsonDocument { ["code"] = 64 },
            ["ok"] = 1.0,
        })),
        "update" => (OpCodes.Msg, MsgBody(new BsonDocument
        {
            ["n"] = 7L, ["nModified"] = 5, ["upserted"] = new BsonArray { new BsonDocument { ["index"] = 0, ["_id"] = ObjectId.GenerateNewId() } }, ["ok"] = 1,
        })),
        "error" => (OpCodes.Msg, MsgBody(new BsonDocument
        {
            ["ok"] = 0.0,
            ["errmsg"] = new string('e', 400) + "é",
            ["code"] = 43,
            ["codeName"] = "CursorNotFound",
        })),
        "killCursors" => (OpCodes.Msg, MsgBody(new BsonDocument
        {
            ["cursorsKilled"] = new BsonArray(Enumerable.Range(1, 20).Select(i => (BsonValue)(long)i)),
            ["cursorsNotFound"] = new BsonArray { 99L },
            ["cursorsAlive"] = new BsonArray(),
            ["ok"] = 1,
        })),
        "oddTypes" => (OpCodes.Msg, MsgBody(new BsonDocument
        {
            ["re"] = new BsonRegularExpression("a.*b", "i"),
            ["js"] = new BsonJavaScript("function(){}"),
            ["jsws"] = new BsonJavaScriptWithScope("x", new BsonDocument("a", 1)),
            ["bin"] = new BsonBinaryData(new byte[300]),
            ["dec"] = new BsonDecimal128(1.5m),
            ["min"] = BsonMinKey.Value, ["max"] = BsonMaxKey.Value, ["undef"] = BsonUndefined.Value, ["nul"] = BsonNull.Value,
            ["sym"] = BsonSymbolTable.Lookup("sym"),
            ["ok"] = 1,
        })),
        "opReply" => (OpCodes.Reply, ReplyBody(new BsonDocument { ["ismaster"] = true, ["maxWireVersion"] = 25, ["ok"] = 1.0 })),
        "deep" => (OpCodes.Msg, MsgBody(new BsonDocument
        {
            ["cursor"] = new BsonDocument
            {
                ["firstBatch"] = new BsonArray { new BsonDocument("a", new BsonDocument("b", new BsonDocument("c", new BsonArray { 1, 2, new BsonDocument("d", 1) }))) },
                ["id"] = 5L,
            },
            ["ok"] = 1,
        })),
        _ => throw new ArgumentException(name)
    };

    private static ReplyFacts Scan(int opCode, byte[] body, IEnumerable<int> cuts)
    {
        var facts = new ReplyFacts();
        var s = new ReplyScanner();
        s.Begin(opCode, facts);
        var prev = 0;
        foreach (var c in cuts.Append(body.Length))
        {
            s.Feed(body.AsSpan(prev, c - prev));
            prev = c;
        }
        return facts;
    }

    private static string Describe(ReplyFacts f) =>
        $"parsed={f.Parsed} ok={f.Ok} code={f.Code} codeName={Str(f.CodeName, f.CodeNameLength)} errmsg={Str(f.Errmsg, f.ErrmsgLength)} " +
        $"cursor={f.CursorId} ns={Str(f.Ns, f.NsLength)} nRet={f.NReturned} n={f.N} nMod={f.NModified} we={f.WriteErrors} wce={f.WriteConcernError} " +
        $"ups={f.Upserted} killed={f.CursorsKilledCount}:{string.Join(',', f.CursorsKilled.Take(Math.Min(16, f.CursorsKilledCount)))} nf={f.CursorsNotFoundCount}";

    private static string Str(byte[] b, int len) => len < 0 ? "-" : Encoding.UTF8.GetString(b, 0, len);

    [Theory]
    [MemberData(nameof(Replies))]
    public void Every_two_way_split_gives_identical_facts(string name)
    {
        var (op, body) = Reply(name);
        var whole = Describe(Scan(op, body, []));
        for (var cut = 1; cut < body.Length; cut++)
            Assert.Equal(whole, Describe(Scan(op, body, [cut])));
        // and byte-at-a-time
        Assert.Equal(whole, Describe(Scan(op, body, Enumerable.Range(1, body.Length - 1))));
    }

    [Fact]
    public void Facts_are_correct()
    {
        var f = Scan(OpCodes.Msg, Reply("find").Body, []);
        Assert.True(f.Parsed);
        Assert.Equal(1.0, f.Ok);
        Assert.Equal(1234567890123L, f.CursorId);
        Assert.Equal(37, f.NReturned);
        Assert.Equal("db.users", Str(f.Ns, f.NsLength));

        f = Scan(OpCodes.Msg, Reply("getMore").Body, []);
        Assert.Equal(0L, f.CursorId);
        Assert.Equal(5, f.NReturned);

        f = Scan(OpCodes.Msg, Reply("insertErrors").Body, []);
        Assert.Equal(3, f.N);
        Assert.Equal(2, f.WriteErrors);
        Assert.True(f.WriteConcernError);

        f = Scan(OpCodes.Msg, Reply("update").Body, []);
        Assert.Equal(7, f.N);
        Assert.Equal(5, f.NModified);
        Assert.Equal(1, f.Upserted);

        f = Scan(OpCodes.Msg, Reply("error").Body, []);
        Assert.Equal(0.0, f.Ok);
        Assert.Equal(43, f.Code);
        Assert.Equal("CursorNotFound", Str(f.CodeName, f.CodeNameLength));
        Assert.Equal(ReplyFacts.ErrmsgCap, f.ErrmsgLength);

        f = Scan(OpCodes.Msg, Reply("killCursors").Body, []);
        Assert.Equal(20, f.CursorsKilledCount);
        Assert.Equal(Enumerable.Range(1, 16).Select(i => (long)i), f.CursorsKilled);
        Assert.Equal(1, f.CursorsNotFoundCount);

        f = Scan(OpCodes.Msg, Reply("oddTypes").Body, []);
        Assert.True(f.Parsed);
        Assert.Equal(1, f.Ok);

        f = Scan(OpCodes.Reply, Reply("opReply").Body, []);
        Assert.True(f.Parsed);
        Assert.Equal(1.0, f.Ok);

        f = Scan(OpCodes.Msg, Reply("deep").Body, []);
        Assert.Equal(1, f.NReturned);
        Assert.Equal(5L, f.CursorId);
    }

    [Fact]
    public void Garbage_never_throws()
    {
        var rng = new Random(42);
        for (var i = 0; i < 2000; i++)
        {
            var body = new byte[rng.Next(0, 300)];
            rng.NextBytes(body);
            if (body.Length > 5) body[4] = 0;
            Scan(OpCodes.Msg, body, [Math.Min(body.Length, rng.Next(0, 40))]);
        }
    }

    [Fact]
    public void Sequence_scanner_counts_all_documents_across_splits()
    {
        var bodyDoc = new BsonDocument { ["insert"] = "c", ["$db"] = "d" }.ToBson();
        var docs = Enumerable.Range(0, 50).Select(i => new BsonDocument { ["_id"] = i, ["pad"] = new string('p', i) }.ToBson()).ToList();
        var deletes = Enumerable.Range(0, 3).Select(i => new BsonDocument { ["q"] = new BsonDocument("_id", i), ["limit"] = 1 }.ToBson()).ToList();

        var ms = new MemoryStream();
        ms.Write(new byte[4]); // flags
        ms.WriteByte(0);
        ms.Write(bodyDoc);
        foreach (var (id, list) in new[] { ("documents", docs), ("deletes", deletes) })
        {
            ms.WriteByte(1);
            var idBytes = Encoding.UTF8.GetBytes(id);
            var size = 4 + idBytes.Length + 1 + list.Sum(d => d.Length);
            var sz = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(sz, size);
            ms.Write(sz);
            ms.Write(idBytes);
            ms.WriteByte(0);
            foreach (var d in list) ms.Write(d);
        }
        var body = ms.ToArray();

        for (var cut = 0; cut <= body.Length; cut += 7)
        {
            var s = new SequenceScanner();
            s.Begin(OpCodes.Msg, body.Length);
            s.Feed(body.AsSpan(0, cut));
            s.Feed(body.AsSpan(cut));
            Assert.Equal(2, s.SectionCount);
            Assert.Equal(50, s.DocCounts[0]);
            Assert.Equal(3, s.DocCounts[1]);
            Assert.Equal("documents", Encoding.UTF8.GetString(s.Identifiers[0], 0, s.IdentifierLengths[0]));
            Assert.Equal("deletes", Encoding.UTF8.GetString(s.Identifiers[1], 0, s.IdentifierLengths[1]));
        }
    }

    [Fact]
    public void Body_fields_after_a_large_inline_array_survive_any_split()
    {
        // Node 6.x shape: documents inline, $db/lsid/txnNumber at the end, far beyond commandBytes
        var lsid = Guid.NewGuid();
        var doc = new BsonDocument
        {
            ["insert"] = "c",
            ["documents"] = new BsonArray(Enumerable.Range(0, 300).Select(i => new BsonDocument { ["i"] = i, ["re"] = new BsonRegularExpression("x"), ["s"] = new string('s', 60) })),
            ["ordered"] = true,
            ["lsid"] = new BsonDocument("id", new BsonBinaryData(lsid, GuidRepresentation.Standard)),
            ["txnNumber"] = 42L,
            ["$db"] = "nodeconf",
        }.ToBson();
        var body = new byte[4 + 1 + doc.Length];
        doc.CopyTo(body, 5);
        foreach (var cut in new[] { 1, 5, 9, 100, doc.Length / 2, body.Length - 3, body.Length - 1 })
        {
            var s = new SequenceScanner();
            s.Begin(OpCodes.Msg, body.Length);
            s.Feed(body.AsSpan(0, cut));
            s.Feed(body.AsSpan(cut));
            Assert.Equal("nodeconf", Encoding.UTF8.GetString(s.Fields.Db, 0, s.Fields.DbLength));
            Assert.Equal(42L, s.Fields.TxnNumber);
            Assert.Equal(lsid.ToByteArray(bigEndian: true), s.Fields.LsidId.AsSpan(0, s.Fields.LsidIdLength).ToArray());
            Assert.Equal(0, s.SectionCount);
        }
    }
}
