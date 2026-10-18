using System.Text;
using Spyglass.Collector.Ingest;
using Spyglass.Collector.Model;

namespace Spyglass.Collector.Tests;

/// <summary>06 §3 <c>records/*.ndjson</c>, consumer side: every example (full, then minimal) parses, and nothing of a full example is left unread.</summary>
public sealed class RecordVectorTests
{
    public static TheoryData<string> Files => Vectors.Files("records", "*.ndjson");

    [Theory]
    [MemberData(nameof(Files))]
    public void Every_example_parses(string file)
    {
        var lines = File.ReadAllLines(Path.Combine(Vectors.Root, "records", file)).Where(l => l.Length > 0).ToArray();
        Assert.Equal(2, lines.Length); // full, minimal
        var type = Path.GetFileNameWithoutExtension(file);
        for (var i = 0; i < lines.Length; i++)
        {
            var result = RecordParser.Parse(Encoding.UTF8.GetBytes(lines[i]), out var r, out var error);
            Assert.True(result == ParseResult.Ok, $"{file} line {i + 1}: {result} {error}");
            switch (type)
            {
                case "uow" or "pool": // read by the M3 analyzers (03 §5.3); kept verbatim until then
                    Assert.IsType<ContextRecord>(r);
                    break;
                case "hello" when i == 0:
                    Assert.Equal("""{"sampling":{"callsites":"first+1/100"}}""", r!.Extra); // informational: kept, not modelled
                    break;
                default:
                    Assert.True(r!.Extra == null, $"{file} line {i + 1}: fields the parser does not know: {r.Extra}");
                    break;
            }
        }
    }

    [Fact]
    public void Full_op_reads_every_field()
    {
        var op = (OpRecord)Parse("op", 0);
        Assert.Equal(("localhost:27017", 5512L, 88231, 1234L), (op.Member, op.ServerConnectionId!.Value, op.RequestId, op.ConnSeq));
        Assert.Equal(("OP_COMPRESSED", "find", "orders", "orders", true, "error"), (op.OpCode, op.Command, op.Db!, op.Coll!, op.Stream, op.Status));
        Assert.Equal((412, 19384, 3L, 812L, 845L), (op.ReqBytes, op.RespBytes, op.ReqLastUs, op.RespFirstUs!.Value, op.RespLastUs!.Value));
        var f = op.Reply!;
        Assert.Equal((0.0, 11000, "DuplicateKey", 8000000000000000017L, 17), (f.Ok!.Value, f.Code!.Value, f.CodeName!, f.CursorId!.Value, f.NReturned!.Value));
        Assert.Equal((5L, 5L, 2, true, 1, 1, 1, false), (f.N!.Value, f.NModified!.Value, f.WriteErrors, f.WriteConcernError, f.Upserted, f.CursorsKilled, f.CursorsNotFound, f.Parsed));
        Assert.Equal(("3fa1c09e5b7d2e11", 7L, "orders page", true, "zlib"), (op.Lsid!, op.TxnNumber!.Value, op.Comment!, op.CmdTruncated, op.Compressor!));
        Assert.Equal([("documents", 120)], op.SeqDocs!);
        Assert.Equal("find", MongoDB.Bson.Serialization.BsonSerializer.Deserialize<MongoDB.Bson.BsonDocument>(op.Cmd).GetElement(0).Name);
        Assert.Equal(809, op.ServerLatencyUs); // respFirst − reqLast
    }

    [Fact]
    public void Minimal_op_reads_the_02_4_defaults()
    {
        var op = (OpRecord)Parse("op", 1);
        Assert.Equal(("OP_MSG", false, 0, null, null), (op.OpCode, op.Stream, op.RespBytes, op.Reply, op.ServerConnectionId));
        Assert.False(op.Ack);
        Assert.False(op.HasReply);
        Assert.Null(op.ServerLatencyUs);
    }

    [Fact]
    public void Full_cmd_reads_every_field_and_builds_the_join_key()
    {
        var c = (CmdRecord)Parse("cmd", 0);
        Assert.Equal(new JoinKey("8f0c2c1e-0000-4000-8000-0000000000a1", "localhost:27017", 5512, 88231), c.Key);
        Assert.Equal(("71", "find", "orders", "error"), (c.OperationId!, c.Command, c.Db!, c.Status));
        Assert.Equal((1210L, 3400L, 2), (c.ClientUs!.Value, c.PoolWaitUs!.Value, c.Attempt!.Value));
        Assert.Equal(("4bf92f3577b34da6a3ce929d0e0e4736", "GET /orders/{id}", "http"), (c.UowId!, c.UowName!, c.UowKind!));
        Assert.Equal("00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01", c.Traceparent);
        Assert.Equal(("9c1e33a70b2f44d1", 8000000000000000017L, "11000"), (c.Callsite!, c.CursorId!.Value, c.ErrorCode!));
        Assert.Equal(["RetryableWriteError"], c.ErrorLabels!);
    }

    [Fact]
    public void Minimal_cmd_is_an_orphan()
    {
        var c = (CmdRecord)Parse("cmd", 1);
        Assert.Null(c.Key); // 02 §3.3
        Assert.Equal(("ping", "admin", "ok"), (c.Command, c.Db!, c.Status));
    }

    [Fact]
    public void Cmd_without_the_key_fields_is_rejected()
    {
        // null is "the exporter could not build it" (an orphan); absent is a malformed record
        var line = File.ReadAllLines(Path.Combine(Vectors.Root, "records", "cmd.ndjson"))[1].Replace("\"proxyId\":null,", "");
        Assert.Equal(ParseResult.Rejected, RecordParser.Parse(Encoding.UTF8.GetBytes(line), out _, out _));
    }

    [Fact]
    public void Callsite_and_hello_read_their_fields()
    {
        var cs = (CallsiteRecord)Parse("callsite", 0);
        Assert.Equal(("9c1e33a70b2f44d1", "dotnet"), (cs.Callsite, cs.Lang!));
        Assert.Equal([("OrdersRepository.GetByIdAsync", "src/Orders/OrdersRepository.cs", 42), ("OrdersController.Get", "src/Orders/OrdersController.cs", 18)],
            cs.Frames.Select(f => (f.Fn, f.File, f.Line ?? 0)));
        var h = (HelloRecord)Parse("hello", 0);
        Assert.Equal(("spyglass-dotnet", "0.1.0", 1, "orders-api", 4121L), (h.Exporter, h.Version!, h.Schema, h.App!, h.Pid!.Value));
    }

    private static Model.Record Parse(string type, int line)
    {
        var text = File.ReadAllLines(Path.Combine(Vectors.Root, "records", type + ".ndjson"))[line];
        Assert.Equal(ParseResult.Ok, RecordParser.Parse(Encoding.UTF8.GetBytes(text), out var r, out var error));
        return r!;
    }
}
