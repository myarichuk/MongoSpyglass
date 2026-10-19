using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MongoDB.Bson;
using static Spyglass.Collector.Tests.Records;

namespace Spyglass.Collector.Tests;

/// <summary>03 §8 API and §9 auth over real HTTP.</summary>
public sealed class ApiTests : IAsyncLifetime
{
    private const string Token = "ingest-token-1", Password = "pw-correct-horse";
    private Server _s = null!;
    private HttpClient _anon = null!, _admin = null!;

    public async ValueTask InitializeAsync()
    {
        _s = await Server.StartAsync(new CollectorOptions
        {
            Bind = new IPEndPoint(IPAddress.Loopback, 0), Token = Token, Auth = AuthMode.Password, AdminPassword = Password, Data = null, Workers = 2,
        });
        _anon = new HttpClient { BaseAddress = _s.BaseAddress };
        _admin = new HttpClient { BaseAddress = _s.BaseAddress };
        _admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("admin:" + Password)));
    }

    public async ValueTask DisposeAsync()
    {
        _anon.Dispose();
        _admin.Dispose();
        await _s.DisposeAsync();
    }

    private async Task<HttpResponseMessage> Post(string token, params string[] lines)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/ingest/v0") { Content = new ByteArrayContent(Batch(lines)) };
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-ndjson");
        if (token != "") req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _anon.SendAsync(req);
    }

    private async Task Ingest(params string[] lines)
    {
        Assert.Equal(HttpStatusCode.NoContent, (await Post(Token, lines)).StatusCode);
        await _s.Collector.DrainAsync(TimeSpan.FromSeconds(5));
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (_s.Collector.Writer.Backlog > 0 && DateTime.UtcNow < deadline) await Task.Delay(10);
        await Task.Delay(400);
    }

    private async Task<JsonElement> Get(string path)
    {
        var r = await _admin.GetAsync(path);
        Assert.True(r.IsSuccessStatusCode, $"{path}: {(int)r.StatusCode}");
        return JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    [Fact]
    public async Task Ingest_requires_the_token()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await Post("", Op(1, "ping"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Post("wrong", Op(1, "ping"))).StatusCode);
        var ok = await Post(Token, Op(1, "ping"), "garbage");
        Assert.Equal(HttpStatusCode.NoContent, ok.StatusCode);
        Assert.Equal("1", ok.Headers.GetValues("X-Spyglass-Rejected").Single());
    }

    [Fact]
    public async Task Api_requires_auth_healthz_does_not()
    {
        Assert.Equal(HttpStatusCode.OK, (await _anon.GetAsync("/healthz")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _anon.GetAsync("/api/v1/ops")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _admin.GetAsync("/api/v1/ops")).StatusCode);

        using var bad = new HttpClient { BaseAddress = _s.BaseAddress };
        bad.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("admin:nope")));
        Assert.Equal(HttpStatusCode.Unauthorized, (await bad.GetAsync("/api/v1/ops")).StatusCode);

        // login form → HttpOnly SameSite=Strict session cookie
        var cookies = new CookieContainer();
        using var browser = new HttpClient(new HttpClientHandler { CookieContainer = cookies }) { BaseAddress = _s.BaseAddress };
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.PostAsync("/api/v1/login", JsonContent("""{"password":"x"}"""))).StatusCode);
        var login = await browser.PostAsync("/api/v1/login", JsonContent($$"""{"password":"{{Password}}"}"""));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var setCookie = login.Headers.GetValues("Set-Cookie").Single();
        Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/api/v1/alerts")).StatusCode);
        await browser.PostAsync("/api/v1/logout", null);
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("/api/v1/alerts")).StatusCode);
    }

    private static StringContent JsonContent(string json) => new(json, Encoding.UTF8, "application/json");

    [Fact]
    public void Auth_none_only_on_loopback()
    {
        var o = new CollectorOptions { Bind = new IPEndPoint(IPAddress.Any, 8080), Auth = AuthMode.None, IngestInsecure = true };
        Assert.Throws<ArgumentException>(() => CollectorOptions.Validate(o));
        Assert.Throws<ArgumentException>(() => CollectorOptions.Validate(new CollectorOptions { IngestInsecure = false, Auth = AuthMode.None }));
        Assert.Throws<ArgumentException>(() => CollectorOptions.Validate(new CollectorOptions { IngestInsecure = true, Auth = AuthMode.Password }));
    }

    [Fact]
    public async Task Ops_shapes_alerts_and_mute()
    {
        var lines = new List<string> { Conn(1, "orders-api") };
        for (var i = 0; i < 25; i++)
            lines.Add(Op(i, "find", new BsonDocument { ["find"] = "orders", ["filter"] = new BsonDocument("customerId", i) }, ts: T0 + i * 10_000L, latencyUs: i == 24 ? 300_000 : 1000 + i));
        await Ingest([.. lines]);

        var page = await Get("/api/v1/ops?limit=10");
        Assert.Equal(10, page.GetProperty("items").GetArrayLength());
        var next = page.GetProperty("next").GetString();
        var page2 = await Get($"/api/v1/ops?limit=10&cursor={next}");
        Assert.NotEqual(page.GetProperty("items")[0].GetProperty("id").GetString(), page2.GetProperty("items")[0].GetProperty("id").GetString());

        var slowId = (await Get("/api/v1/ops?minLatencyUs=200000")).GetProperty("items").EnumerateArray().Single().GetProperty("id").GetString();
        var detail = await Get($"/api/v1/ops/{slowId}");
        var op = detail.GetProperty("op");
        Assert.Contains("\"customerId\" : 24", op.GetProperty("command_ejson").GetString());
        Assert.Equal(300_000, op.GetProperty("serverLatency_us").GetInt64());
        Assert.Equal("?number", op.GetProperty("shape").GetProperty("filter").GetProperty("customerId").GetString());
        Assert.Equal("slow-shape", detail.GetProperty("alerts")[0].GetProperty("rule").GetString());

        var shapes = await Get($"/api/v1/shapes?from={T0 - 60_000_000}&to={T0 + 60_000_000}&sort=totalTime");
        var top = shapes.GetProperty("items")[0];
        Assert.Equal(25, top.GetProperty("count").GetInt64());
        Assert.InRange(top.GetProperty("max_us").GetDouble(), 297_000, 303_000);
        var hash = top.GetProperty("shapeHash").GetString();
        var shape = await Get($"/api/v1/shapes/{hash}?from={T0 - 60_000_000}&to={T0 + 60_000_000}");
        Assert.Equal(slowId, shape.GetProperty("slowest")[0].GetProperty("id").GetString());
        Assert.Equal(10, shape.GetProperty("latest").GetArrayLength());

        var alert = (await Get("/api/v1/alerts?state=open")).GetProperty("items")[0];
        var id = alert.GetProperty("id").GetString();
        Assert.Equal(HttpStatusCode.Unauthorized, (await _anon.PostAsync($"/api/v1/alerts/{id}/mute", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _admin.PostAsync($"/api/v1/alerts/{id}/mute", null)).StatusCode);
        await Ingest(Op(100, "find", new BsonDocument { ["find"] = "orders", ["filter"] = new BsonDocument("customerId", 1) }, latencyUs: 400_000));
        var after = (await Get("/api/v1/alerts")).GetProperty("items").EnumerateArray().Single(a => a.GetProperty("id").GetString() == id);
        Assert.True(after.GetProperty("muted").GetBoolean());
        Assert.Equal(1, after.GetProperty("occurrences").GetInt64());
    }

    [Fact]
    public async Task Live_feed_streams_filtered_events()
    {
        using var stream = await _admin.GetStreamAsync("/api/v1/live?app=wanted");
        using var reader = new StreamReader(stream);
        Assert.Equal(": connected", await reader.ReadLineAsync());
        await Post(Token, Conn(1, "other"), Conn(2, "wanted", sid: 8),
            Op(1, "find", connSeq: 1, sid: 7), Op(2, "insert", connSeq: 2, sid: 8));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        string? line;
        while ((line = await reader.ReadLineAsync(cts.Token)) != null && !line.StartsWith("event:")) { }
        Assert.Equal("event: op.created", line);
        var data = await reader.ReadLineAsync(cts.Token);
        var ev = JsonDocument.Parse(data!["data: ".Length..]).RootElement;
        Assert.Equal("wanted", ev.GetProperty("app").GetString());
        Assert.Equal("insert", ev.GetProperty("command").GetString());
    }

    [Fact]
    public async Task Producers_connections_apps_export_metrics()
    {
        await Ingest(
            $$$"""{"type":"proxy","ts":{{{T0}}},"src":"{{{Records.Proxy}}}","version":"0.1.0","schema":1,"topology":"replicaSet","setName":"rs0","members":[{"member":"localhost:27017","upstream":"db1:27017"}],"config":{"compression":"strip","values":"truncated","commandBytes":16384}}""",
            $$$"""{"type":"stats","ts":{{{T0}}},"src":"{{{Records.Proxy}}}","dropped":{"no_slot":3,"export":1},"sent":{"records":10,"batches":1}}""",
            Conn(1, "orders-api"), Op(1, "find"));
        var p = (await Get("/api/v1/producers")).GetProperty("items")[0];
        Assert.Equal("proxy", p.GetProperty("kind").GetString());
        Assert.Equal("rs0", p.GetProperty("setName").GetString());
        Assert.Equal(3, p.GetProperty("dropped").GetProperty("no_slot").GetInt64());
        Assert.Equal("orders-api", (await Get("/api/v1/connections")).GetProperty("items")[0].GetProperty("app").GetString());
        Assert.Equal("orders-api", (await Get("/api/v1/apps")).GetProperty("items")[0].GetProperty("app").GetString());

        var export = await _admin.PostAsync($"/api/v1/export?from={T0 - 1_000_000}&to={T0 + 1_000_000}", null);
        var ndjson = (await export.Content.ReadAsStringAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var header = JsonDocument.Parse(ndjson[0]).RootElement;
        Assert.Equal("export", header.GetProperty("type").GetString());
        Assert.Equal("truncated", header.GetProperty("redaction").GetProperty(Records.Proxy).GetString()); // 03 §9
        Assert.Contains(ndjson.Skip(1), l => l.Contains("\"command\":\"find\""));

        var metrics = await (await _admin.GetAsync("/api/v1/metrics")).Content.ReadAsStringAsync();
        Assert.Contains("spyglass_collector_ingest_records_total{result=\"accepted\"} 4", metrics);
    }
}
