using System.Net;
using System.Net.Http.Headers;
using System.Text;
using MongoDB.Bson;
using Spyglass.Collector.Ingest;
using Spyglass.Collector.Shapes;

namespace Spyglass.Collector.Tests;

/// <summary>CI-11/12/13/21 (parser), CA-4 (BSON bounds), CI-16/CA-22 (options), CA-3/CA-20/UD-14 (HTTP auth).</summary>
public sealed class ParserHardeningTests
{
    private const string Op = """{"type":"op","ts":1,"src":"p","member":"h:1","requestId":1,"connSeq":1,"command":"find","status":"ok","reqBytes":1""";

    private static ParseResult P(string line, out Model.Record? rec) => RecordParser.Parse(Encoding.UTF8.GetBytes(line), out rec, out _);

    [Fact]
    public void Nested_value_under_reply_parsed_is_rejected()
    {
        Assert.Equal(ParseResult.Rejected, P(Op + ""","reply":{"parsed":{"x":1},"ok":0},"command":"evil"}""", out _));
        Assert.Equal(ParseResult.Ok, P(Op + ""","reply":{"parsed":false}}""", out var r));
        Assert.False(((Model.OpRecord)r!).Reply!.Parsed);
    }

    [Fact]
    public void Non_object_array_elements_are_rejected()
    {
        Assert.Equal(ParseResult.Rejected, P("""{"type":"callsite","ts":1,"src":"s","callsite":"c","frames":["a",{"fn":"f"}],"lang":"dotnet"}""", out _));
        Assert.Equal(ParseResult.Rejected, P("""{"type":"proxy","ts":1,"src":"s","members":[1]}""", out _));
        Assert.Equal(ParseResult.Ok, P("""{"type":"callsite","ts":1,"src":"s","callsite":"c","frames":[{"fn":"f"}],"lang":"dotnet"}""", out var r));
        Assert.Single(((Model.CallsiteRecord)r!).Frames);
    }

    [Theory]
    [InlineData("""{"type":"hello","ts":1,"src":"s","exporter":"x"} garbage {""")]
    [InlineData("""{"type":"hello","ts":1,"src":"s","exporter":"x"}{"a":1}""")]
    [InlineData("""{"type":"hello","ts":1,"src":"s","exporter":"x" """)]
    public void Trailing_bytes_and_truncated_records_are_not_ok(string line) =>
        Assert.NotEqual(ParseResult.Ok, P(line, out _));

    [Fact]
    public void Int32_fields_out_of_range_are_rejected_not_wrapped()
    {
        Assert.Equal(ParseResult.Rejected, P(Op + ""","reply":{"ok":1,"nReturned":4294967296}}""", out _));
        Assert.Equal(ParseResult.Rejected, P(Op + ""","reply":{"ok":0,"code":4294967297}}""", out _));
        Assert.Equal(ParseResult.Ok, P(Op + ""","reply":{"ok":1,"nReturned":2147483647}}""", out var r));
        Assert.Equal(int.MaxValue, ((Model.OpRecord)r!).Reply!.NReturned);
    }

    [Fact]
    public void Invalid_utf8_is_rejected_whichever_field_it_is_in()
    {
        foreach (var field in new[] { "member", "command", "status" })
        {
            var line = Encoding.UTF8.GetBytes(Op.Replace($"\"{field}\":\"", $"\"{field}\":\"m") + "}").ToList();
            var at = line.IndexOf((byte)'m', Encoding.UTF8.GetString([.. line]).IndexOf($"\"{field}\":\"m", StringComparison.Ordinal));
            line.Insert(at + 1, 0xFF);
            Assert.Equal(ParseResult.Rejected, RecordParser.Parse([.. line], out _, out _));
        }
    }

    [Fact]
    public void Uow_and_pool_records_build_no_extra_string()
    {
        Assert.Equal(ParseResult.Ok, P("""{"type":"uow","ts":1,"src":"s","id":"u","name":"n"}""", out var r));
        Assert.Null(r!.Extra);
    }
}

public sealed class BsonBoundsTests
{
    /// <summary><c>{find:"c", filter:{a:&lt;string claiming 40 bytes&gt;}, z:"…padding…"}</c>: the string runs past <c>filter</c>'s end but not the buffer's.</summary>
    private static byte[] Overrunning()
    {
        static byte[] Doc(Action<BinaryWriter> body)
        {
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            w.Write(0);
            body(w);
            w.Write((byte)0);
            var b = ms.ToArray();
            BitConverter.TryWriteBytes(b, b.Length);
            return b;
        }
        var inner = Doc(w => { w.Write((byte)0x02); w.Write("a"u8); w.Write((byte)0); w.Write(40); w.Write("XY"u8); w.Write((byte)0); });
        return Doc(w =>
        {
            w.Write((byte)0x02); w.Write("find"u8); w.Write((byte)0); w.Write(2); w.Write("c"u8); w.Write((byte)0);
            w.Write((byte)0x03); w.Write("filter"u8); w.Write((byte)0); w.Write(inner);
            w.Write((byte)0x02); w.Write("z"u8); w.Write((byte)0); w.Write(60); w.Write(new byte[59]); w.Write((byte)0);
        });
    }

    [Fact]
    public void An_element_that_runs_past_its_document_is_invalid()
    {
        var bytes = Overrunning();
        Assert.False(BsonSpan.Validate(bytes));
        Assert.Throws<FormatException>(() => FastBson.Decode(bytes));
    }

    [Fact]
    public void Decimal_more_markers_and_subtypes_do_not_throw()
    {
        var nan = Decimal128.Parse("NaN");
        var huge = Decimal128.Parse("1E+30");
        foreach (var d in new[] { nan, huge })
        {
            var cmd = new BsonDocument
            {
                ["find"] = "c",
                ["filter"] = new BsonDocument("a", new BsonDocument("$in", new BsonArray { 1, new BsonDocument("$more", d) })),
                ["x"] = new BsonDocument("$binData", new BsonDocument { ["subtype"] = d, ["length"] = 3 }),
            };
            ShapeNormalizer.Normalize("find", "d", "c", cmd, false, true);
        }
    }
}

public sealed class OptionsHardeningTests
{
    private static CollectorOptions Load(params (string, string)[] env) =>
        CollectorOptions.Load([], env.ToDictionary(e => e.Item1, e => (string?)e.Item2));

    [Fact]
    public void Unknown_spyglass_variables_warn_instead_of_refusing_to_start()
    {
        var o = Load(("SPYGLASS_TOKEN", "t"), ("SPYGLASS_ADMIN_PASSWORD", "p"), ("SPYGLASS_UPSTREAM__URI", "mongodb://x"));
        Assert.Single(o.Warnings);
    }

    [Fact]
    public void Empty_token_is_rejected() =>
        Assert.Throws<ArgumentException>(() => Load(("SPYGLASS_TOKEN", ""), ("SPYGLASS_ADMIN_PASSWORD", "p")));

    [Fact]
    public void Auth_header_alone_selects_header_mode_and_conflicts_are_errors_whatever_the_order()
    {
        Assert.Equal(AuthMode.Header, Load(("SPYGLASS_TOKEN", "t"), ("SPYGLASS_AUTH_HEADER", "X-U")).Auth);
        Assert.Equal(AuthMode.Header, Load(("SPYGLASS_TOKEN", "t"), ("SPYGLASS_AUTH_HEADER", "X-U"), ("SPYGLASS_AUTH", "header")).Auth);
        Assert.Throws<ArgumentException>(() => Load(("SPYGLASS_TOKEN", "t"), ("SPYGLASS_AUTH_HEADER", "X-U"), ("SPYGLASS_AUTH", "password"), ("SPYGLASS_ADMIN_PASSWORD", "p")));
        Assert.Throws<ArgumentException>(() => Load(("SPYGLASS_AUTH", "password"), ("SPYGLASS_ADMIN_PASSWORD", "p"), ("SPYGLASS_TOKEN", "t"), ("SPYGLASS_AUTH_HEADER", "X-U")));
    }

    [Fact]
    public void Trusted_proxies_parse_cidrs_and_addresses()
    {
        var o = Load(("SPYGLASS_TOKEN", "t"), ("SPYGLASS_AUTH_HEADER", "X-U"), ("SPYGLASS_AUTH_HEADER_TRUSTED_PROXIES", "10.0.0.0/8, 192.168.1.5"));
        Assert.Equal(2, o.TrustedProxies.Count);
        Assert.Contains(o.TrustedProxies, n => n.Contains(IPAddress.Parse("10.1.2.3")));
        Assert.Contains(o.TrustedProxies, n => n.Contains(IPAddress.Parse("192.168.1.5")));
        Assert.Throws<ArgumentException>(() => Load(("SPYGLASS_TOKEN", "t"), ("SPYGLASS_TRUSTED_PROXIES", "nope")));
    }
}

public sealed class LoginThrottleTests
{
    [Fact]
    public void Backs_off_per_address_and_clears_on_success()
    {
        long now = 1000;
        var t = new LoginThrottle(() => now);
        var a = IPAddress.Parse("10.0.0.1");
        for (var i = 0; i < 5; i++) { Assert.False(t.Locked(a, out _)); t.Failed(a); }
        Assert.False(t.Locked(a, out _)); // five free attempts
        t.Failed(a);
        Assert.True(t.Locked(a, out var retry));
        Assert.InRange(retry, 1, 2);
        Assert.False(t.Locked(IPAddress.Parse("10.0.0.2"), out _));
        now += 2000;
        Assert.False(t.Locked(a, out _));
        t.Succeeded(a);
        Assert.False(t.Locked(a, out _));
    }

    [Fact]
    public void A_flood_from_many_addresses_slows_failures_but_never_locks_another_address()
    {
        // FC-2: a global hard lock was a lever for anyone to keep every user out; it is now a delay on failed answers only
        long now = 1000;
        var t = new LoginThrottle(() => now);
        Assert.Equal(0, t.ExtraDelayMs());
        for (var i = 0; i < 60; i++) t.Failed(IPAddress.Parse($"10.0.{i / 200}.{i % 200 + 1}"));
        Assert.False(t.Locked(IPAddress.Parse("10.9.9.9"), out _));
        Assert.True(t.ExtraDelayMs() > 0);
        now += 61_000;
        Assert.Equal(0, t.ExtraDelayMs());
    }

    [Fact]
    public void Ipv6_addresses_share_their_64_and_failures_decay()
    {
        long now = 1000;
        var t = new LoginThrottle(() => now);
        for (var i = 0; i < 6; i++) t.Failed(IPAddress.Parse($"2001:db8:1:2::{i + 1}")); // six addresses of one /64
        Assert.True(t.Locked(IPAddress.Parse("2001:db8:1:2:ffff::9"), out _)); // same /64
        Assert.True(t.Locked(IPAddress.Parse("2001:db8:1:2::99"), out _));
        Assert.False(t.Locked(IPAddress.Parse("2001:db8:1:3::1"), out _));
        // decay: a quiet spell forgets the old burst, so one more failure is not another max lock
        now += 20 * 60_000;
        Assert.False(t.Locked(IPAddress.Parse("2001:db8:1:2::99"), out _));
        t.Failed(IPAddress.Parse("2001:db8:1:2::99"));
        Assert.False(t.Locked(IPAddress.Parse("2001:db8:1:2::99"), out _));
    }

    [Fact]
    public void An_ipv4_mapped_address_is_the_ipv4_address()
    {
        long now = 1000;
        var t = new LoginThrottle(() => now);
        for (var i = 0; i < 6; i++) t.Failed(IPAddress.Parse("1.2.3.4").MapToIPv6());
        Assert.True(t.Locked(IPAddress.Parse("1.2.3.4"), out _));
    }
}

public sealed class HeaderAuthTests
{
    private static async Task<Server> Start(IReadOnlyList<IPNetwork>? trusted, AuthMode auth = AuthMode.Header)
    {
        var o = new CollectorOptions
        {
            Bind = new IPEndPoint(IPAddress.Loopback, 0), Token = "t", Auth = auth, AdminPassword = "pw", Data = null, Workers = 1,
        };
        if (trusted != null) o.TrustedProxies = trusted;
        return await Server.StartAsync(o);
    }

    [Fact]
    public async Task Header_is_believed_only_from_a_trusted_peer()
    {
        foreach (var (trusted, expected) in new (IReadOnlyList<IPNetwork>?, HttpStatusCode)[]
                 {
                     (null, HttpStatusCode.OK), // loopback by default
                     ([new IPNetwork(IPAddress.Parse("10.0.0.0"), 8)], HttpStatusCode.Unauthorized), // the peer (127.0.0.1) is not the proxy
                     ([], HttpStatusCode.Unauthorized),
                 })
        {
            await using var s = await Start(trusted);
            using var http = new HttpClient { BaseAddress = s.BaseAddress };
            http.DefaultRequestHeaders.Add("X-Forwarded-User", "alice");
            Assert.Equal(expected, (await http.GetAsync("/api/v1/whoami")).StatusCode);
        }
    }

    [Fact]
    public async Task Login_hardening_and_secure_cookie_behind_a_trusted_proxy()
    {
        await using var s = await Start(null, AuthMode.Password);
        using var http = new HttpClient { BaseAddress = s.BaseAddress };
        static StringContent Json(string j) => new(j, Encoding.UTF8, "application/json");

        Assert.Equal(HttpStatusCode.Unauthorized, (await http.PostAsync("/api/v1/login", Json("""{"password":123}"""))).StatusCode); // not a 500
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.PostAsync("/api/v1/login", Json("[1]"))).StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await http.PostAsync("/api/v1/login", Json("{\"password\":\"" + new string('x', 100_000) + "\"}"))).StatusCode);

        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/login") { Content = Json("""{"password":"pw"}""") };
        req.Headers.Add("X-Forwarded-Proto", "https");
        var ok = await http.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Contains("secure", string.Join(";", ok.Headers.GetValues("Set-Cookie")), StringComparison.OrdinalIgnoreCase);

        // an untrusted peer's X-Forwarded-Proto is ignored
        await using var s2 = await Start([new IPNetwork(IPAddress.Parse("10.0.0.0"), 8)], AuthMode.Password);
        using var http2 = new HttpClient { BaseAddress = s2.BaseAddress };
        using var req2 = new HttpRequestMessage(HttpMethod.Post, "/api/v1/login") { Content = Json("""{"password":"pw"}""") };
        req2.Headers.Add("X-Forwarded-Proto", "https");
        var ok2 = await http2.SendAsync(req2);
        Assert.DoesNotContain("secure", string.Join(";", ok2.Headers.GetValues("Set-Cookie")).Replace("samesite", ""), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Repeated_wrong_passwords_are_throttled()
    {
        await using var s = await Start(null, AuthMode.Password);
        using var http = new HttpClient { BaseAddress = s.BaseAddress };
        var last = HttpStatusCode.OK;
        for (var i = 0; i < 8 && last != HttpStatusCode.TooManyRequests; i++)
            last = (await http.PostAsync("/api/v1/login", new StringContent("""{"password":"no"}""", Encoding.UTF8, "application/json"))).StatusCode;
        Assert.Equal(HttpStatusCode.TooManyRequests, last);
        // the right password is refused too while the address is locked
        Assert.Equal(HttpStatusCode.TooManyRequests, (await http.PostAsync("/api/v1/login", new StringContent("""{"password":"pw"}""", Encoding.UTF8, "application/json"))).StatusCode);
    }
}
