using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using MongoDB.Bson;
using Spyglass.Collector.Ui.Services;
using Spyglass.Collector.Ui.Shared;
using static Spyglass.Collector.Tests.Records;

namespace Spyglass.Collector.Tests;

/// <summary>The SPA's DTOs (compiled in from src/Spyglass.Collector.Ui) against the JSON the collector really serves (03 §8, UD-21).</summary>
public sealed class UiContractTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private Server _s = null!;
    private HttpClient _http = null!;

    public async ValueTask InitializeAsync()
    {
        _s = await Server.StartAsync(new CollectorOptions { Bind = new IPEndPoint(IPAddress.Loopback, 0), IngestInsecure = true, Auth = AuthMode.None, Data = null, Workers = 1 });
        _http = new HttpClient { BaseAddress = _s.BaseAddress };
        var lines = new List<string>
        {
            $$$"""{"type":"proxy","ts":{{{T0}}},"src":"{{{Records.Proxy}}}","version":"0.1.0","schema":1,"topology":"replicaSet","setName":"rs0","members":[{"member":"localhost:27017","upstream":"db1:27017","cert":{"subject":"CN=a","issuer":"CN=ca","notBefore":1700000000000000,"notAfter":1900000000000000,"sha256":"AB","dns":["a"]}}],"config":{"compression":"strip","values":"truncated","commandBytes":16384,"tls":{"upstream":{"enabled":true,"trust":"insecure"},"listen":{"mode":"none"},"collector":{"https":false} } } }""",
            Conn(1, "orders-api"),
        };
        for (var i = 0; i < 3; i++)
            lines.Add(Op(i, "find", new BsonDocument { ["find"] = "orders", ["filter"] = new BsonDocument { ["_id"] = ObjectId.GenerateNewId(), ["at"] = DateTime.UtcNow } },
                ts: T0 + i * 1000, latencyUs: i == 2 ? 300_000 : 1000, nReturned: 1));
        var r = await _http.PostAsync("/ingest/v0", new ByteArrayContent(Batch([.. lines])));
        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        await _s.Collector.DrainAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(500);
    }

    public async ValueTask DisposeAsync()
    {
        _http.Dispose();
        await _s.DisposeAsync();
    }

    private async Task<JsonElement> Get(string path) =>
        JsonDocument.Parse(await _http.GetStringAsync(path)).RootElement.Clone();

    /// <summary>Every property the server wrote must be one the DTO reads: a rename on either side fails here instead of showing nulls.</summary>
    private static void AssertCovers<T>(JsonElement server)
    {
        var known = typeof(T).GetProperties().Select(p => p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? JsonNamingPolicy.CamelCase.ConvertName(p.Name)).ToHashSet();
        var missing = server.EnumerateObject().Select(p => p.Name).Where(n => !known.Contains(n)).ToList();
        Assert.True(missing.Count == 0, $"{typeof(T).Name} does not read: {string.Join(", ", missing)}");
    }

    [Fact]
    public async Task Ops_and_detail_deserialize()
    {
        var page = await Get("/api/v1/ops?limit=10");
        AssertCovers<OpSummary>(page.GetProperty("items")[0]);
        var ops = page.Deserialize<OpPage>(Web)!;
        Assert.Equal(3, ops.Items.Length);
        Assert.All(ops.Items, o => { Assert.Equal("find", o.Command); Assert.Equal("shop.orders", o.Ns); Assert.NotEqual(0, o.Ts); Assert.NotNull(o.ServerLatencyUs); });
        Assert.Contains(ops.Items, o => o.ServerLatencyUs == 300_000);

        var detailJson = await Get($"/api/v1/ops/{ops.Items[0].Id}");
        AssertCovers<OpDetailDto>(detailJson.GetProperty("op"));
        var d = detailJson.Deserialize<OpWithAlerts>(Web)!.Op;
        Assert.Equal(ops.Items[0].Id, d.Id);
        Assert.NotNull(d.CommandEjson);
        Assert.True(d.Timings.ReqLast > 0);

        // and the shell conversion runs on what the server really sends
        var shell = MongoShell.RunCommand(d.Db, d.CommandEjson)!;
        Assert.StartsWith("db.getSiblingDB(\"shop\").runCommand(", shell);
        Assert.Contains("ObjectId(\"", shell);
        Assert.Contains("ISODate(\"", shell);
        Assert.DoesNotContain("$oid", shell);
        Assert.DoesNotContain("$db", shell);
    }

    [Fact]
    public async Task Shapes_alerts_producers_deserialize()
    {
        var range = $"from={T0 - 60_000_000}&to={T0 + 60_000_000}";
        var shapes = await Get($"/api/v1/shapes?{range}");
        AssertCovers<ShapeRow>(shapes.GetProperty("items")[0]);
        var rows = shapes.Deserialize<ShapePage>(Web)!;
        Assert.Equal(3, rows.Items[0].Count);
        Assert.True(rows.Items[0].MaxUs >= 300_000 * 0.97);

        var hash = rows.Items[0].ShapeHash;
        var detailJson = await Get($"/api/v1/shapes/{hash}?{range}");
        AssertCovers<ShapeDetail>(detailJson);
        var detail = detailJson.Deserialize<ShapeDetail>(Web)!;
        Assert.NotEmpty(detail.Series);
        Assert.Equal(3, detail.Latest.Length);
        Assert.Equal(300_000, detail.Slowest[0].ServerLatencyUs);

        var alertsJson = await Get("/api/v1/alerts?state=open");
        AssertCovers<AlertDto>(alertsJson.GetProperty("items")[0]);
        var alert = alertsJson.GetProperty("items").Deserialize<AlertDto[]>(Web)!.First(a => a.Rule == "slow-shape");
        Assert.NotEmpty(alert.SampleOpIds);
        Assert.NotEqual("", AlertRules.Explain(alert.Rule));

        var producers = await Get("/api/v1/producers");
        AssertCovers<ProducerDto>(producers.GetProperty("items")[0]);
        var p = producers.GetProperty("items").Deserialize<ProducerDto[]>(Web)!.Single();
        Assert.Equal("insecure", p.Tls.GetProperty("upstream").GetProperty("trust").GetString());
        Assert.Equal("CN=ca", p.Members[0].GetProperty("cert").GetProperty("issuer").GetString());
        Assert.Equal("rs0", p.SetName);
        Assert.Equal(JsonValueKind.Array, p.Members.ValueKind);
    }

    [Fact]
    public async Task The_date_header_gives_the_server_clock()
    {
        var r = await _http.GetAsync("/api/v1/whoami");
        Assert.NotNull(r.Headers.Date);
        Assert.Equal(0, Fmt.OffsetFromDate(r.Headers.Date!.Value, DateTime.UtcNow)); // same machine: within the noise floor
    }
}

public sealed class UiLogicTests
{
    [Fact]
    public void Shell_syntax_for_every_extended_json_type()
    {
        const string ejson = """
            {"find":"orders","filter":{"_id":{"$oid":"650000000000000000000001"},"at":{"$date":"2024-01-02T03:04:05.678Z"},
             "old":{"$date":{"$numberLong":"-1000"}},"n":{"$numberLong":"9007199254740993"},"d":{"$numberDecimal":"1.5"},"x":{"$numberDouble":"NaN"},
             "u":{"$binary":{"base64":"EjRWeJq83vASNFZ4mrze8A==","subType":"04"}},"b":{"$binary":{"base64":"AQID","subType":"00"}},
             "t":{"$timestamp":{"t":5,"i":2}},"r":{"$regularExpression":{"pattern":"^a","options":"i"}},"lo":{"$minKey":1},"hi":{"$maxKey":1},
             "plain":{"$gt":3},"list":[1,"two",{"$oid":"650000000000000000000002"}],"empty":{},"e":[]},
             "$db":"shop","lsid":{"id":{"$binary":{"base64":"AAAA","subType":"04"}}},"$clusterTime":{"clusterTime":{"$timestamp":{"t":1,"i":1}}}}
            """;
        var s = MongoShell.RunCommand("shop", ejson)!;
        foreach (var expect in (string[])[
            "db.getSiblingDB(\"shop\").runCommand(", "\"_id\": ObjectId(\"650000000000000000000001\")", "ISODate(\"2024-01-02T03:04:05.678Z\")",
            "new Date(-1000)", "NumberLong(\"9007199254740993\")", "NumberDecimal(\"1.5\")", "\"x\": NaN",
            "UUID(\"12345678-9abc-def0-1234-56789abcdef0\")", "BinData(0, \"AQID\")", "Timestamp(5, 2)", "new RegExp(\"^a\", \"i\")",
            "MinKey()", "MaxKey()", "\"$gt\": 3", "\"empty\": {}", "\"e\": []", "ObjectId(\"650000000000000000000002\")"])
            Assert.Contains(expect, s);
        Assert.DoesNotContain("$db", s);
        Assert.DoesNotContain("lsid", s);
        Assert.DoesNotContain("$clusterTime", s);
        Assert.DoesNotContain("$oid", s);
    }

    [Theory]
    [InlineData("(undecodable)")]
    [InlineData("")]
    [InlineData("{\"find\":\"orders\",\"filter\":{\"a\":")] // a truncated capture
    [InlineData("[1,2]")]
    public void Unconvertible_input_yields_null_instead_of_throwing(string input) => Assert.Null(MongoShell.RunCommand("shop", input));

    [Fact]
    public void Duplicate_keys_do_not_throw() => Assert.NotNull(MongoShell.RunCommand("shop", """{"find":"a","filter":{"x":1,"x":2}}"""));

    [Theory]
    [InlineData("shapes?range=1h", "shapes?range=1h")]
    [InlineData("ops/abc", "ops/abc")]
    [InlineData("", "")]
    [InlineData(null, "")]
    [InlineData("https://evil.example/", "")]
    [InlineData("//evil.example", "")]
    [InlineData("/evil", "")]
    [InlineData("\\\\evil.example", "")]
    [InlineData("javascript:alert(1)", "")]
    [InlineData("data:text/html,x", "")]
    [InlineData("login?return=x", "")]
    [InlineData("\t//evil.example", "")]
    [InlineData(" //evil.example/x", "")]
    [InlineData("+//evil", "+//evil")] // not a space, and not a path an URL parser reads as scheme-relative (it is base-relative)
    [InlineData("//evil.example ", "")]
    [InlineData("x ", "")]
    [InlineData(" x", "")]
    [InlineData("\n//evil", "")]
    [InlineData("/\\evil", "")]
    [InlineData("/%2F/evil", "")]
    [InlineData("\u0085//evil", "")]
    [InlineData("a\\b", "")]
    [InlineData("c:/x", "")]
    [InlineData("x?u=http://ok.example", "x?u=http://ok.example")] // a colon after the first path segment is just a query
    public void Login_return_is_only_ever_local(string? input, string expected) => Assert.Equal(expected, Redirect.Safe(input));

    [Fact]
    public void Every_rule_of_spec_03_5_2_has_an_explanation()
    {
        foreach (var rule in (string[])["slow-shape", "n-plus-one", "n-plus-one-suspected", "repeated-query", "unbounded-scan", "large-result", "unbounded-in",
                     "cursor-abandoned", "cursor-not-exhausted", "pool-starvation", "client-overhead", "error-burst", "unacknowledged-writes", "exporter-skew"])
            Assert.NotEqual("", AlertRules.Explain(rule));
        Assert.Equal("", AlertRules.Explain("no-such-rule"));
    }

    [Fact]
    public void Times_are_utc_and_ages_never_negative()
    {
        Assert.Equal("1970-01-01 00:00:01 UTC", Fmt.Stamp(1_000_000));
        Assert.Equal("00:00:01.500", Fmt.Clock(1_500_000));
        Assert.Equal("0s", Fmt.Ago(5_000_000, 1_000_000)); // a timestamp from the "future"
        Assert.Equal("2m", Fmt.Ago(0, 150_000_000));
    }

    [Fact]
    public void Ranges_follow_the_server_clock()
    {
        Fmt.ServerOffsetUs = 3_600_000_000; // the server is an hour ahead of this browser
        try
        {
            var browserNow = (DateTime.UtcNow - DateTime.UnixEpoch).Ticks / 10;
            var to = long.Parse(TimeRange.Query("15m").Split('&')[1][3..]);
            Assert.InRange(to - browserNow, 3_600_000_000 - 1_000_000, 3_600_000_000 + 1_000_000);
        }
        finally { Fmt.ServerOffsetUs = 0; }
        Assert.Equal("1h", TimeRange.Normalize("nonsense"));
        Assert.Equal("from=0&to=900000000", TimeRange.Query("15m", 900_000_000));
        Assert.Equal(0, Fmt.OffsetFromDate(DateTimeOffset.UtcNow.AddMilliseconds(800), DateTime.UtcNow));
        Assert.InRange(Fmt.OffsetFromDate(DateTimeOffset.UtcNow.AddMinutes(5), DateTime.UtcNow), 299_000_000, 301_000_000);
    }

    [Fact]
    public void Chart_is_laid_out_on_a_time_axis_with_gaps_and_a_bounded_column_count()
    {
        static SeriesPoint P(long m, long n = 1) => new(m, n, 0, 100, 200);
        var l = SeriesLayout.Of([P(100), P(101), P(110)]);
        Assert.Equal(100, l.FirstMinute);
        Assert.Equal(11, l.TotalMinutes);
        Assert.Equal(3, l.Columns.Length);
        Assert.Equal([2, 1], l.Runs().Select(r => r.Length)); // the idle minutes 102-109 break the line
        Assert.True(l.X(110) > l.X(101) * 4);

        var big = SeriesLayout.Of(Enumerable.Range(0, 10_080).Select(i => P(i)).ToArray());
        Assert.True(big.Columns.Length <= 300);
        Assert.Equal(10_080, big.Columns.Sum(c => c.Count));
        Assert.Single(big.Runs());
        Assert.Empty(SeriesLayout.Of([]).Columns);
    }
}
