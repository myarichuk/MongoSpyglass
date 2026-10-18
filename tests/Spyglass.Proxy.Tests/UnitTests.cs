using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using Spyglass.Proxy.Config;
using Spyglass.Proxy.Observation;
using Spyglass.Proxy.Wire;

namespace Spyglass.Proxy.Tests;

public sealed class ConfigTests
{
    private static readonly Dictionary<string, string?> NoEnv = new();

    private static ProxyConfig LoadYaml(string yaml, Dictionary<string, string?>? env = null, params string[] args)
    {
        var path = Path.Combine(Path.GetTempPath(), $"spyglass-{Guid.NewGuid():N}.yaml");
        File.WriteAllText(path, yaml);
        try { return ConfigLoader.Load(["--config", path, .. args], env ?? NoEnv); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Spec_example_loads()
    {
        // 01 §2, verbatim structure
        var c = LoadYaml("""
            upstream:
              uri: "mongodb://h1:27017,h2:27017/?replicaSet=rs0"   # comment
              topology: auto
              tls:
                enabled: auto
                caFile: null
                allowInvalidCertificates: false
            listen:
              bindAddress: 127.0.0.1
              advertiseHost: localhost
              basePort: 27017
              portRange: [27017, 27099]
              tls:
                mode: none
            compression: strip
            capture:
              commandBytes: 16384
              replyBytes: 0
              values: truncated
              truncateStringsAt: 64
              redactFieldPattern: "(?i)^(pass(word)?|pwd|secret|token|api[-_]?key|authorization|cookie|ssn|card(number)?)$"
            limits:
              maxClientConnections: 4096
              observeQueueBytes: 67108864
            collector:
              endpoint: "http://spyglass-collector:4318/ingest/v0"
              token: "${SPYGLASS_TOKEN}"
            discovery:
              advertiseToken: true
            metrics:
              bind: 127.0.0.1:9187
              commandLabels: [command, database, collection, app]
              maxSeries: 5000
            """, new Dictionary<string, string?> { ["SPYGLASS_TOKEN"] = "abc" });
        Assert.Equal("mongodb://h1:27017,h2:27017/?replicaSet=rs0", c.Upstream.Uri);
        Assert.Equal(27099, c.Listen.PortRangeEnd);
        Assert.Equal("abc", c.Collector.Token);
        Assert.Equal(ValueMode.Truncated, c.Capture.Values);
    }

    [Fact]
    public void Precedence_is_cli_over_env_over_file()
    {
        var c = LoadYaml("upstream:\n  uri: mongodb://file:1/\nlisten:\n  basePort: 30000\n  portRange: [30000, 30010]\n",
            new Dictionary<string, string?> { ["SPYGLASS_LISTEN__BASEPORT"] = "30001" }, "--listen.basePort", "30002");
        Assert.Equal(30002, c.Listen.BasePort);
        c = LoadYaml("upstream:\n  uri: mongodb://file:1/\nlisten:\n  basePort: 30000\n  portRange: [30000, 30010]\n",
            new Dictionary<string, string?> { ["SPYGLASS_LISTEN__BASEPORT"] = "30001" });
        Assert.Equal(30001, c.Listen.BasePort);
    }

    [Theory]
    [InlineData("upstream:\n  uri: mongodb://a/\n  urii: x\n", "upstream.urii")]
    [InlineData("upstream:\n  uri: mongodb://a/\ncapture:\n  values: everything\n", "capture.values")]
    [InlineData("upstream:\n  uri: mongodb+srv://a.b.c/\n  tls:\n    enabled: false\n", "SRV implies TLS")]
    [InlineData("upstream:\n  uri: http://a/\n", "upstream.uri")]
    [InlineData("listen:\n  basePort: 1\n", "upstream.uri")]
    [InlineData("upstream:\n  uri: mongodb://a/\nlisten:\n  basePort: 99999\n", "listen.basePort")]
    [InlineData("upstream:\n  uri: mongodb://a/\n- x\n", "block sequences")]
    public void Invalid_config_names_the_key(string yaml, string expected)
    {
        var e = Assert.Throws<ConfigException>(() => LoadYaml(yaml));
        Assert.Contains(expected, e.Message);
    }

    [Theory]
    [InlineData("mongodb://user:p%40ss@h1:1,h2/db?replicaSet=rs0&tls=true", false, "h1:1,h2:27017", "rs0")]
    [InlineData("mongodb+srv://cluster0.example.net/?authSource=admin", true, "cluster0.example.net:27017", null)]
    [InlineData("mongodb://[::1]:27018/", false, "[::1]:27018", null)]
    [InlineData("mongodb://LocalHost./", false, "localhost:27017", null)]
    public void Uri_parsing(string uri, bool srv, string hosts, string? rs)
    {
        var u = MongoUri.Parse(uri);
        Assert.Equal(srv, u.IsSrv);
        Assert.Equal(hosts, string.Join(',', u.Hosts));
        Assert.Equal(rs, u.ReplicaSet);
    }

    [Theory]
    [InlineData("LocalHost:27017", "localhost:27017")]
    [InlineData("[::1]:27018", "[::1]:27018")]
    [InlineData("[0:0:0:0:0:0:0:1]:5", "[::1]:5")]
    [InlineData("db.example.net.:27017", "db.example.net:27017")]
    [InlineData("host", "host:27017")]
    public void Address_normalization(string input, string expected) =>
        Assert.Equal(expected, HostPort.Parse(input).ToString()); // 02 §3.2
}

public sealed class RedactorTests
{
    private static BsonDocument Redact(BsonDocument cmd, ValueMode mode = ValueMode.Truncated, bool hello = false, int capture = int.MaxValue)
    {
        var r = new Redactor(new CaptureConfig { Values = mode });
        var bytes = cmd.ToBson();
        var (bson, _, _) = r.Redact(cmd.GetElement(0).Name, bytes.AsSpan(0, Math.Min(capture, bytes.Length)), null, hello, captureTruncated: capture < bytes.Length);
        return BsonSerializer.Deserialize<BsonDocument>(bson);
    }

    [Fact]
    public void Truncated_mode()
    {
        var d = Redact(new BsonDocument
        {
            ["find"] = "users",
            ["filter"] = new BsonDocument
            {
                ["name"] = new string('a', 100),
                ["tags"] = new BsonDocument("$in", new BsonArray(Enumerable.Range(0, 50))),
                ["blob"] = new BsonBinaryData(new byte[33]),
                ["profile"] = new BsonDocument("Password", "x"),
                ["profile.apiKey"] = "y",
            },
            ["$db"] = "app",
        });
        var f = d["filter"].AsBsonDocument;
        Assert.Equal(new string('a', 64) + "…(+36)", f["name"].AsString);
        var arr = f["tags"]["$in"].AsBsonArray;
        Assert.Equal(21, arr.Count);
        Assert.Equal(30, arr[20]["$more"].AsInt32);
        Assert.Equal(33, f["blob"]["$binData"]["length"].AsInt32);
        Assert.Equal("$redacted", f["profile"]["Password"].AsString);
        Assert.Equal("$redacted", f["profile.apiKey"].AsString);
        Assert.Equal("users", d["find"].AsString);
        Assert.Equal("app", d["$db"].AsString);
    }

    [Fact]
    public void Shape_mode_tags_leaves()
    {
        var d = Redact(new BsonDocument
        {
            ["find"] = "users",
            ["filter"] = new BsonDocument { ["a"] = 1, ["b"] = "s", ["c"] = ObjectId.GenerateNewId(), ["d"] = DateTime.UtcNow, ["e"] = true, ["f"] = BsonNull.Value, ["g"] = new BsonBinaryData(Guid.NewGuid(), GuidRepresentation.Standard) },
        }, ValueMode.Shape);
        var f = d["filter"].AsBsonDocument;
        Assert.Equal(["?number", "?string", "?objectId", "?date", "?bool", "?null", "?uuid"], f.Values.Select(v => v.AsString));
    }

    [Fact]
    public void Speculative_authenticate_and_auth_commands()
    {
        var hello = Redact(new BsonDocument { ["hello"] = 1, ["speculativeAuthenticate"] = new BsonDocument("saslStart", 1) }, hello: true);
        Assert.Equal("$redacted", hello["speculativeAuthenticate"].AsString);
        var sasl = Redact(new BsonDocument { ["saslStart"] = 1, ["payload"] = new BsonBinaryData(new byte[10]) });
        Assert.Equal(new BsonDocument("$redacted", "saslStart"), sasl);
    }

    [Fact]
    public void Truncated_capture_keeps_complete_elements_only()
    {
        var cmd = new BsonDocument { ["find"] = "users", ["filter"] = new BsonDocument("x", new string('z', 500)), ["limit"] = 1 };
        var d = Redact(cmd, capture: 60);
        Assert.Equal("users", d["find"].AsString);
        Assert.False(d.Contains("filter"));  // cut inside: dropped, not emitted half-way
        Assert.False(d.Contains("limit"));
    }

    [Fact]
    public void Exporter_token()
    {
        Assert.True(RequestParser.IsExporterToken("spyglass-dotnet"));
        Assert.True(RequestParser.IsExporterToken("spyglass-node"));
        Assert.False(RequestParser.IsExporterToken("spyglass-"));
        Assert.False(RequestParser.IsExporterToken("Spyglass-node"));
        Assert.False(RequestParser.IsExporterToken("spyglass-node!"));
    }

    [Fact]
    public void Hello_rewrite_drops_checksum_and_appends_spyglass_last()
    {
        var doc = new BsonDocument { ["ismaster"] = true, ["hosts"] = new BsonArray { "A:1", "b:2" }, ["me"] = "a:1", ["primary"] = "b:2", ["$spyglass"] = "stale", ["ok"] = 1.0 }.ToBson();
        var frame = new byte[16 + 4 + 1 + doc.Length + 4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(frame, frame.Length);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(12), OpCodes.Msg);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(16), 1 | 2); // checksum + moreToCome
        doc.CopyTo(frame, 21);

        var map = new Map();
        var facts = new HelloReplyFacts();
        var spy = new BsonDocument("v", 0).ToBson();
        var outFrame = HelloRewriter.RewriteReply(frame, new HelloRewriteContext { RewriteTopology = true, Map = map, Self = "proxy:1", SpyglassDocument = spy }, facts, out var failure)!;
        Assert.Equal(RewriteFailure.None, failure);
        Assert.Equal(outFrame.Length, System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(outFrame));
        Assert.Equal(2u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(outFrame.AsSpan(16))); // checksum flag cleared, moreToCome kept
        var reply = BsonSerializer.Deserialize<BsonDocument>(outFrame.AsSpan(21).ToArray());
        Assert.Equal(new BsonArray { "proxy:1", "proxy:2" }, reply["hosts"]);
        Assert.Equal("proxy:1", reply["me"].AsString);
        Assert.Equal("proxy:2", reply["primary"].AsString);
        Assert.Equal("$spyglass", reply.Names.Last());
        Assert.Equal(new BsonDocument("v", 0), reply["$spyglass"]);
        Assert.Equal(1, reply.Names.Count(n => n == "$spyglass"));
        Assert.Equal(["a:1", "b:2"], facts.Members.Select(m => m.ToString()));
    }

    private sealed class Map : IAddressMap
    {
        public string? Advertise(HostPort upstream) => upstream.Host == "a" ? "proxy:1" : "proxy:2";
    }
}
