using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using static Spyglass.Collector.Tests.Records;

namespace Spyglass.Collector.Tests;

/// <summary>03 §6 / 02 §4.2: the proxy's TLS summary through ingest and the API; 03 §9: the collector's own HTTPS.</summary>
public sealed class TlsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("spyglass-tls").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    // ---- proxy record: config.tls and members[].cert ----

    private const string Cert = """{"subject":"CN=mongo-a","issuer":"CN=Corp Issuing CA","notBefore":1700000000000000,"notAfter":1900000000000000,"sha256":"AB12","dns":["mongo-a","localhost"]}""";
    private const string Tls = """{"upstream":{"enabled":true,"trust":"caFile","caFile":"/etc/ssl/corp-ca.pem","allowInvalidHostnames":false,"revocation":"none"},"listen":{"mode":"none"},"collector":{"https":true,"trust":"system","caFile":null}}""";

    private static string ProxyLine(string? tls, string? cert, string extraConfig = "") =>
        $$$"""{"type":"proxy","ts":{{{T0}}},"src":"{{{Records.Proxy}}}","version":"0.1.0","schema":1,"topology":"replicaSet","setName":"rs0","members":[{"member":"localhost:27017","upstream":"db1:27017"{{{(cert != null ? ",\"cert\":" + cert : "")}}}}],"config":{"compression":"strip","values":"truncated"{{{(tls != null ? ",\"tls\":" + tls : "")}}}{{{extraConfig}}}}}""";

    private static async Task<(Server, HttpClient)> Plain()
    {
        var s = await Server.StartAsync(new CollectorOptions { Bind = new IPEndPoint(IPAddress.Loopback, 0), Token = "t", Auth = AuthMode.None, Data = null, Workers = 1 });
        return (s, new HttpClient { BaseAddress = s.BaseAddress });
    }

    private static async Task<JsonElement> ProducerAfter(Server s, HttpClient http, params string[] lines)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/ingest/v0") { Content = new ByteArrayContent(Batch(lines)) };
        req.Headers.Authorization = new("Bearer", "t");
        var r = await http.SendAsync(req);
        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        Assert.Equal("0", r.Headers.TryGetValues("X-Spyglass-Rejected", out var rej) ? rej.Single() : "0");
        await s.Collector.DrainAsync(TimeSpan.FromSeconds(5));
        return JsonDocument.Parse(await http.GetStringAsync("/api/v1/producers")).RootElement.GetProperty("items")[0].Clone();
    }

    [Fact]
    public async Task Tls_and_member_certs_are_echoed_raw()
    {
        var (s, http) = await Plain();
        await using var srv = s; using var cli = http;
        var p = await ProducerAfter(s, http, ProxyLine(Tls, Cert));
        Assert.Equal("caFile", p.GetProperty("tls").GetProperty("upstream").GetProperty("trust").GetString());
        Assert.True(p.GetProperty("tls").GetProperty("collector").GetProperty("https").GetBoolean());
        var cert = p.GetProperty("members")[0].GetProperty("cert");
        Assert.Equal("CN=Corp Issuing CA", cert.GetProperty("issuer").GetString());
        Assert.Equal(1900000000000000, cert.GetProperty("notAfter").GetInt64());
        Assert.Equal("localhost", cert.GetProperty("dns")[1].GetString());
    }

    [Fact]
    public async Task Without_tls_the_fields_are_absent_and_a_later_record_replaces_them()
    {
        var (s, http) = await Plain();
        await using var srv = s; using var cli = http;
        var p = await ProducerAfter(s, http, ProxyLine(null, null));
        Assert.False(p.TryGetProperty("tls", out _));
        Assert.False(p.GetProperty("members")[0].TryGetProperty("cert", out _));
        p = await ProducerAfter(s, http, ProxyLine(Tls, Cert));
        Assert.True(p.TryGetProperty("tls", out _));
        p = await ProducerAfter(s, http, ProxyLine(null, null)); // the proxy dropped TLS: the old summary must not linger
        Assert.False(p.TryGetProperty("tls", out _));
        Assert.False(p.GetProperty("members")[0].TryGetProperty("cert", out _));
    }

    [Fact]
    public async Task Oversized_or_malformed_tls_is_ignored_and_the_record_still_accepted()
    {
        var (s, http) = await Plain();
        await using var srv = s; using var cli = http;
        var bigTls = "{\"upstream\":{\"caFile\":\"" + new string('x', 9000) + "\"}}";
        var bigCert = "{\"subject\":\"" + new string('y', 3000) + "\"}";
        var p = await ProducerAfter(s, http, ProxyLine(bigTls, bigCert));
        Assert.Equal("0.1.0", p.GetProperty("version").GetString()); // accepted: the rest of the record is applied
        Assert.Equal("rs0", p.GetProperty("setName").GetString());
        Assert.Equal("truncated", p.GetProperty("values").GetString());
        Assert.False(p.TryGetProperty("tls", out _));
        Assert.False(p.GetProperty("members")[0].TryGetProperty("cert", out _));
        // a string, an array, null where an object belongs
        foreach (var bad in (string[])["\"nope\"", "[1,2]", "null", "7"])
        {
            p = await ProducerAfter(s, http, ProxyLine(bad, bad));
            Assert.False(p.TryGetProperty("tls", out _), bad);
            Assert.False(p.GetProperty("members")[0].TryGetProperty("cert", out _), bad);
            Assert.Equal("rs0", p.GetProperty("setName").GetString());
        }
        // fields after the tls object still parse
        p = await ProducerAfter(s, http, ProxyLine(Tls, Cert, ",\"commandBytes\":16384"));
        Assert.True(p.TryGetProperty("tls", out _));
    }

    // ---- collector HTTPS ----

    private (X509Certificate2 Cert, RSA Key) SelfSigned(DateTimeOffset? notAfter = null)
    {
        var key = RSA.Create(2048);
        var req = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        req.CertificateExtensions.Add(san.Build());
        return (req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), notAfter ?? DateTimeOffset.UtcNow.AddDays(90)), key);
    }

    private string Write(string name, string text) { var p = Path.Combine(_dir, name); File.WriteAllText(p, text); return p; }

    private static CollectorOptions Https(string cert, string? key = null, string? password = null) => new()
    {
        Bind = new IPEndPoint(IPAddress.Loopback, 0), Token = "t", Auth = AuthMode.Password, AdminPassword = "pw", Data = null, Workers = 1,
        TlsCert = cert, TlsKey = key, TlsKeyPassword = password,
    };

    private static HttpClient Trusting(Server s, Action<X509Certificate2?, X509Chain?>? seen = null) => new(new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = (_, c, chain, _) => { seen?.Invoke(c, chain); return true; },
    }) { BaseAddress = s.BaseAddress };

    private async Task Serves(CollectorOptions o)
    {
        await using var s = await Server.StartAsync(o);
        Assert.Equal("https", s.BaseAddress.Scheme);
        using var http = Trusting(s);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/healthz")).StatusCode);
        // the container HEALTHCHECK probe: TLS when the certificate is configured, nothing validated
        var bind = $"127.0.0.1:{s.BaseAddress.Port}";
        Assert.Equal(0, await HealthProbe.RunAsync(["--bind", bind, "--tls-cert", o.TlsCert!]));
        Assert.Equal(1, await HealthProbe.RunAsync(["--bind", bind])); // plain HTTP at an HTTPS listener is not healthy
    }

    [Fact]
    public async Task Serves_https_from_a_combined_pem()
    {
        var (c, k) = SelfSigned();
        await Serves(Https(Write("combined.pem", c.ExportCertificatePem() + "\n" + k.ExportPkcs8PrivateKeyPem())));
    }

    [Fact]
    public async Task Serves_https_from_separate_pem_files_and_an_rsa_pkcs1_key()
    {
        var (c, k) = SelfSigned();
        await Serves(Https(Write("c.pem", c.ExportCertificatePem()), Write("k.pem", k.ExportPkcs8PrivateKeyPem())));
        await Serves(Https(Write("c1.pem", c.ExportCertificatePem()), Write("k1.pem", k.ExportRSAPrivateKeyPem())));
    }

    [Fact]
    public async Task Serves_https_from_an_encrypted_pem_key()
    {
        var (c, k) = SelfSigned();
        var enc = k.ExportEncryptedPkcs8PrivateKeyPem("s3cret", new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 1000));
        await Serves(Https(Write("c.pem", c.ExportCertificatePem()), Write("k.pem", enc), "s3cret"));
        var e = await Assert.ThrowsAsync<ArgumentException>(() => Server.StartAsync(Https(Write("c.pem", c.ExportCertificatePem()), Write("k.pem", enc))));
        Assert.Contains("SPYGLASS_TLS_KEY_PASSWORD", e.Message);
        e = await Assert.ThrowsAsync<ArgumentException>(() => Server.StartAsync(Https(Write("c.pem", c.ExportCertificatePem()), Write("k.pem", enc), "wrong")));
        Assert.Contains("SPYGLASS_TLS_KEY", e.Message);
    }

    [Fact]
    public async Task Serves_https_from_a_pfx_with_and_without_a_password()
    {
        var (c, k) = SelfSigned();
        var withKey = c;
        await Serves(Https(WritePfx("plain.pfx", withKey, null)));
        await Serves(Https(WritePfx("pw.p12", withKey, "pfx-pass"), null, "pfx-pass"));
        var e = await Assert.ThrowsAsync<ArgumentException>(() => Server.StartAsync(Https(WritePfx("pw2.pfx", withKey, "pfx-pass"))));
        Assert.Contains("SPYGLASS_TLS_CERT", e.Message);
        Assert.Contains("SPYGLASS_TLS_KEY_PASSWORD", e.Message);
    }

    private string WritePfx(string name, X509Certificate2 c, string? password)
    {
        var p = Path.Combine(_dir, name);
        File.WriteAllBytes(p, c.Export(X509ContentType.Pfx, password));
        return p;
    }

    [Fact]
    public async Task Intermediates_in_the_pem_are_sent_with_the_leaf()
    {
        // root → intermediate → leaf; .NET sends the intermediate and (rightly) not the root
        using var rootKey = RSA.Create(2048);
        var rootReq = new CertificateRequest("CN=Test Root", rootKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        rootReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 1, true));
        using var root = rootReq.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-3), DateTimeOffset.UtcNow.AddDays(365));
        using var caKey = RSA.Create(2048);
        var caReq = new CertificateRequest("CN=Test Issuing CA", caKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        caReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using var ca = caReq.Create(root, DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddDays(300), RandomNumberGenerator.GetBytes(8));
        using var key = RSA.Create(2048);
        var req = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder(); san.AddDnsName("localhost"); san.AddIpAddress(IPAddress.Loopback);
        req.CertificateExtensions.Add(san.Build());
        using var caSigner = ca.CopyWithPrivateKey(caKey);
        using var leaf = req.Create(caSigner, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(90), RandomNumberGenerator.GetBytes(8));
        var path = Write("chain.pem", key.ExportPkcs8PrivateKeyPem() + "\n" + leaf.ExportCertificatePem() + "\n" + ca.ExportCertificatePem()); // key first: order is free

        await using var s = await Server.StartAsync(Https(path));
        var elements = 0;
        using var http = Trusting(s, (_, chain) => elements = chain?.ChainElements.Count ?? 0);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/healthz")).StatusCode);
        Assert.True(elements >= 2, $"chain of {elements}"); // leaf + the intermediate that only came over the wire
    }

    [Fact]
    public async Task The_session_cookie_is_Secure_over_https_only()
    {
        var (c, k) = SelfSigned();
        await using var s = await Server.StartAsync(Https(Write("c.pem", c.ExportCertificatePem() + "\n" + k.ExportPkcs8PrivateKeyPem())));
        using var https = Trusting(s);
        var r = await https.PostAsJsonAsync("/api/v1/login", new { password = "pw" });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var cookie = r.Headers.GetValues("Set-Cookie").Single();
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);

        await using var plain = await Server.StartAsync(new CollectorOptions { Bind = new IPEndPoint(IPAddress.Loopback, 0), Token = "t", Auth = AuthMode.Password, AdminPassword = "pw", Data = null, Workers = 1 });
        using var http = new HttpClient { BaseAddress = plain.BaseAddress };
        cookie = (await http.PostAsJsonAsync("/api/v1/login", new { password = "pw" })).Headers.GetValues("Set-Cookie").Single();
        Assert.DoesNotContain("secure", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Bad_tls_settings_name_the_variable()
    {
        var (c, k) = SelfSigned();
        var good = Write("good.pem", c.ExportCertificatePem() + "\n" + k.ExportPkcs8PrivateKeyPem());
        async Task<string> Fails(CollectorOptions o) => (await Assert.ThrowsAsync<ArgumentException>(() => Server.StartAsync(o))).Message;

        Assert.Contains("SPYGLASS_TLS_CERT", await Fails(Https(Path.Combine(_dir, "missing.pem"))));
        Assert.Contains("SPYGLASS_TLS_KEY", await Fails(Https(good, Path.Combine(_dir, "missing.key"))));
        Assert.Contains("SPYGLASS_TLS_CERT", await Fails(Https(Write("junk.pem", "not a certificate"))));
        Assert.Contains("SPYGLASS_TLS_CERT", await Fails(Https(Write("junk.pfx", "not a pfx"))));
        Assert.Contains("SPYGLASS_TLS_CERT", await Fails(Https(Write("nokey.pem", c.ExportCertificatePem()))));            // cert without a key
        Assert.Contains("SPYGLASS_TLS_KEY", await Fails(Https(good, Write("nokey2.pem", "-----BEGIN NOPE-----"))));
        var (_, otherKey) = SelfSigned();
        Assert.Contains("SPYGLASS_TLS_KEY", await Fails(Https(Write("c.pem", c.ExportCertificatePem()), Write("other.key", otherKey.ExportPkcs8PrivateKeyPem()))));
        Assert.Contains("SPYGLASS_TLS_KEY", await Fails(new CollectorOptions { TlsKey = good, Token = "t", Auth = AuthMode.None }));
    }

    [Fact]
    public void Settings_come_from_env_and_flags()
    {
        var o = CollectorOptions.Load(["--tls-key", "/k.pem"], new Dictionary<string, string?>
        {
            ["SPYGLASS_TLS_CERT"] = "/c.pem", ["SPYGLASS_TLS_KEY_PASSWORD"] = "pw", ["SPYGLASS_TOKEN"] = "t", ["SPYGLASS_AUTH"] = "none",
        });
        Assert.Equal(("/c.pem", "/k.pem", "pw"), (o.TlsCert, o.TlsKey, o.TlsKeyPassword));
        Assert.Equal("/x.pfx", CollectorOptions.Load(["--tls-cert", "/x.pfx"], new Dictionary<string, string?> { ["SPYGLASS_TOKEN"] = "t", ["SPYGLASS_AUTH"] = "none" }).TlsCert);
    }

    [Fact]
    public async Task An_expiring_certificate_warns_at_startup_but_serves()
    {
        var (c, k) = SelfSigned(DateTimeOffset.UtcNow.AddDays(3));
        var o = Https(Write("soon.pem", c.ExportCertificatePem() + "\n" + k.ExportPkcs8PrivateKeyPem()));
        var err = new StringWriter();
        var was = Console.Error;
        Console.SetError(err);
        try { await Serves(o); }
        finally { Console.SetError(was); }
        Assert.Contains("SPYGLASS_TLS_CERT", err.ToString());
        Assert.Contains("expires", err.ToString());
    }
}
