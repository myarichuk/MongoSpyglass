using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Spyglass.Proxy.Config;
using Spyglass.Proxy.Export;
using Spyglass.Proxy.Forwarding;
using Spyglass.Proxy.Metrics;
using Spyglass.Proxy.Tests.Infrastructure;

namespace Spyglass.Proxy.Tests;

/// <summary>01 §2 TLS keys, URI TLS options, certificate loading and collector HTTPS trust. No mongod needed.</summary>
public sealed class TlsConfigTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "spyglass-tests", "tlscfg-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly Lazy<TestPki> _pki;

    public TlsConfigTests() => _pki = new(() => TestPki.Create(_dir));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static ProxyConfig Load(string uri, params string[] kv)
    {
        var args = new List<string> { "--upstream.uri", uri };
        args.AddRange(kv);
        return ConfigLoader.Load([.. args], new Dictionary<string, string?>());
    }

    [Fact]
    public void Uri_tls_options_fill_unset_keys()
    {
        var c = Load("mongodb://h/?tlsCAFile=/etc/ca.pem&tlsCertificateKeyFile=/etc/c.pem&tlsCertificateKeyFilePassword=pw&tlsAllowInvalidHostnames=true&tlsDisableOCSPEndpointCheck=true");
        Assert.Equal("/etc/ca.pem", c.Upstream.Tls.CaFile);
        Assert.Equal("/etc/c.pem", c.Upstream.Tls.CertFile);
        Assert.Equal("pw", c.Upstream.Tls.KeyPassword);
        Assert.True(c.Upstream.Tls.AllowInvalidHostnames);
        Assert.False(c.Upstream.Tls.AllowInvalidCertificates);
        Assert.DoesNotContain(ConfigLoader.Warnings(c), x => x.Contains("conflicts"));
        Assert.True(MongoUri.Parse("mongodb://h/?tlsCAFile=x").Tls); // any TLS option implies tls

        var insecure = Load("mongodb://h/?tls=true&tlsInsecure=true");
        Assert.True(insecure.Upstream.Tls.AllowInvalidCertificates);
        Assert.True(insecure.Upstream.Tls.AllowInvalidHostnames);
    }

    [Fact]
    public void Explicit_key_wins_over_uri_with_a_warning()
    {
        var c = Load("mongodb://h/?tlsCAFile=/uri/ca.pem&tlsAllowInvalidHostnames=true", "--upstream.tls.caFile", "/cfg/ca.pem", "--upstream.tls.allowInvalidHostnames", "false");
        Assert.Equal("/cfg/ca.pem", c.Upstream.Tls.CaFile);
        Assert.False(c.Upstream.Tls.AllowInvalidHostnames);
        var w = ConfigLoader.Warnings(c).ToList();
        Assert.Contains(w, x => x.Contains("tlscafile") && x.Contains("upstream.tls.caFile"));
        Assert.Contains(w, x => x.Contains("tlsallowinvalidhostnames") && x.Contains("upstream.tls.allowInvalidHostnames"));

        // same value: no noise
        var same = Load("mongodb://h/?tlsCAFile=/cfg/ca.pem", "--upstream.tls.caFile", "/cfg/ca.pem");
        Assert.Empty(ConfigLoader.Warnings(same));
    }

    [Fact]
    public void Key_password_expands_environment_and_listen_cert_needs_no_key_file()
    {
        var c = ConfigLoader.Load(["--upstream.uri", "mongodb://h", "--listen.tls.mode", "tls", "--listen.tls.certFile", "/x/c.pem",
            "--listen.tls.keyPassword", "${KEYPW}", "--upstream.tls.revocation", "offline"],
            new Dictionary<string, string?> { ["KEYPW"] = "from-env" });
        Assert.Equal("from-env", c.Listen.Tls.KeyPassword);
        Assert.Null(c.Listen.Tls.KeyFile);
        Assert.Equal(RevocationCheck.Offline, c.Upstream.Tls.Revocation);
        Assert.Throws<ConfigException>(() => Load("mongodb://h", "--upstream.tls.keyFile", "/k"));
        Assert.Throws<ConfigException>(() => Load("mongodb://h", "--upstream.tls.revocation", "sometimes"));
    }

    [Fact]
    public void Loads_every_certificate_format_with_the_chain()
    {
        var pki = _pki.Value;
        var combined = TlsMaterial.Load(pki.CombinedPemFile, null, null, "k", "kk");
        Assert.True(combined.Leaf.HasPrivateKey);
        Assert.Equal("CN=localhost", combined.Leaf.Subject);
        Assert.Single(combined.Extras);

        var split = TlsMaterial.Load(pki.ChainCertFile, pki.LeafKeyFile, null, "k", "kk");
        Assert.Equal(combined.Leaf.Thumbprint, split.Leaf.Thumbprint);
        Assert.Single(split.Extras);

        var enc = TlsMaterial.Load(pki.ChainCertFile, pki.EncryptedKeyFile, TestPki.Password, "k", "kk");
        Assert.Equal(combined.Leaf.Thumbprint, enc.Leaf.Thumbprint);
        var noPw = Assert.Throws<ConfigException>(() => TlsMaterial.Load(pki.ChainCertFile, pki.EncryptedKeyFile, null, "k", "kk"));
        Assert.Contains("encrypted", noPw.Message);
        var badPw = Assert.Throws<ConfigException>(() => TlsMaterial.Load(pki.ChainCertFile, pki.EncryptedKeyFile, "wrong", "k", "kk"));
        Assert.DoesNotContain("wrong", badPw.Message);

        var pfx = TlsMaterial.Load(pki.PfxFile, null, TestPki.Password, "k", "kk");
        Assert.True(pfx.Leaf.HasPrivateKey);
        Assert.Equal(combined.Leaf.Thumbprint, pfx.Leaf.Thumbprint);
        Assert.Single(pfx.Extras);

        // PKCS#12 by content, whatever the extension
        var odd = Path.Combine(_dir, "cert.bin");
        File.Copy(pki.PfxFile, odd);
        Assert.Equal(combined.Leaf.Thumbprint, TlsMaterial.Load(odd, null, TestPki.Password, "k", "kk").Leaf.Thumbprint);

        var certOnly = Assert.Throws<ConfigException>(() => TlsMaterial.Load(pki.ChainCertFile, null, null, "upstream.tls.certFile", "upstream.tls.keyFile"));
        Assert.Contains("upstream.tls.keyFile", certOnly.Message);
    }

    [Fact]
    public void Expiry_warnings()
    {
        var now = DateTimeOffset.UtcNow;
        var soon = TlsPkiHelpers.SelfSigned(now.AddDays(-30), now.AddDays(5));
        var expired = TlsPkiHelpers.SelfSigned(now.AddDays(-30), now.AddDays(-1));
        var fine = TlsPkiHelpers.SelfSigned(now.AddDays(-1), now.AddDays(100));
        Assert.Contains("expires in", TlsMaterial.ExpiryWarning(soon, "listen.tls.certFile", now));
        Assert.Contains("expired", TlsMaterial.ExpiryWarning(expired, "listen.tls.certFile", now));
        Assert.Null(TlsMaterial.ExpiryWarning(fine, "listen.tls.certFile", now));
    }

    [Fact]
    public void Cert_summary_shape_and_metric()
    {
        var leaf = TlsMaterial.Load(_pki.Value.CombinedPemFile, null, null, "k", "kk").Leaf;
        var s = CertSummary.From(leaf);
        var ms = new MemoryStream();
        using (var w = new System.Text.Json.Utf8JsonWriter(ms)) s.Write(w);
        var j = System.Text.Json.JsonDocument.Parse(ms.ToArray()).RootElement;
        Assert.Equal("CN=localhost", j.GetProperty("subject").GetString());
        Assert.Equal("CN=Spyglass Test Intermediate", j.GetProperty("issuer").GetString());
        Assert.Equal(64, j.GetProperty("sha256").GetString()!.Length);
        Assert.Equal("localhost", j.GetProperty("dns")[0].GetString());
        Assert.True(j.GetProperty("notAfter").GetInt64() > j.GetProperty("notBefore").GetInt64());
        Assert.True(j.GetProperty("notAfter").GetInt64() > 1_700_000_000_000_000);

        var m = new ProxyMetrics();
        m.TlsCertExpiry.WithLabels("listen", "").Set(s.NotAfterUnixSeconds);
        Assert.Contains($"spyglass_proxy_tls_cert_expiry_seconds{{hop=\"listen\",member=\"\"}} {s.NotAfterUnixSeconds}", m.Registry.Render());
    }

    // ---- collector over HTTPS ---------------------------------------------------------------

    /// <summary>A TcpListener + SslStream HTTPS endpoint: reads POST requests (Content-Length bodies) and answers 204.</summary>
    private sealed class FakeHttpsCollector : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly X509Certificate2 _cert;
        public int Requests;
        public int HandshakeFailures;
        public string Endpoint => $"https://localhost:{((IPEndPoint)_listener.LocalEndpoint).Port}/ingest/v0";

        public FakeHttpsCollector(LoadedCert cert)
        {
            _cert = cert.Leaf;
            _chain = SslStreamCertificateContext.Create(cert.Leaf, cert.Extras, offline: true);
            _listener.Start();
            _ = Task.Run(AcceptAsync);
        }

        private readonly SslStreamCertificateContext _chain;

        private async Task AcceptAsync()
        {
            while (true)
            {
                TcpClient c;
                try { c = await _listener.AcceptTcpClientAsync(); }
                catch (Exception) { return; }
                _ = Task.Run(() => ServeAsync(c));
            }
        }

        private async Task ServeAsync(TcpClient c)
        {
            using var _c = c;
            try
            {
                await using var ssl = new SslStream(c.GetStream());
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificateContext = _chain, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 });
                var buf = new byte[64 * 1024];
                while (true)
                {
                    var n = 0;
                    int end;
                    while ((end = Encoding.ASCII.GetString(buf, 0, n).IndexOf("\r\n\r\n", StringComparison.Ordinal)) < 0)
                    {
                        var r = await ssl.ReadAsync(buf.AsMemory(n));
                        if (r == 0) return;
                        n += r;
                    }
                    var head = Encoding.ASCII.GetString(buf, 0, end);
                    var len = int.Parse(head.Split("\r\n").First(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)).Split(':')[1].Trim());
                    var have = n - (end + 4);
                    while (have < len)
                    {
                        var r = await ssl.ReadAsync(buf.AsMemory(0, Math.Min(buf.Length, len - have)));
                        if (r == 0) return;
                        have += r;
                    }
                    Interlocked.Increment(ref Requests);
                    await ssl.WriteAsync("HTTP/1.1 204 No Content\r\nContent-Length: 0\r\n\r\n"u8.ToArray());
                }
            }
            catch (Exception e) when (e is AuthenticationException or IOException or ObjectDisposedException)
            {
                Interlocked.Increment(ref HandshakeFailures);
            }
        }

        public void Dispose() => _listener.Stop();
    }

    private static async Task<(long Sent, long Dropped)> PostAsync(CollectorConfig cfg)
    {
        var dropped = new Counter();
        await using var s = new CollectorSender(cfg, "src", dropped);
        s.Start();
        s.Enqueue("{\"type\":\"stats\"}"u8);
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (Interlocked.Read(ref s.SentBatches) == 0 && dropped.Value == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        return (Interlocked.Read(ref s.SentRecords), dropped.Value);
    }

    [Fact]
    public async Task Collector_https_with_caFile_and_intermediate_chain()
    {
        var pki = _pki.Value;
        using var server = new FakeHttpsCollector(TlsMaterial.Load(pki.CombinedPemFile, null, null, "k", "kk"));
        var (sent, _) = await PostAsync(new CollectorConfig { Endpoint = server.Endpoint, CaFile = pki.RootCaFile, FlushIntervalMs = 20 });
        Assert.Equal(1, sent);
        Assert.Equal(1, server.Requests);
    }

    [Fact]
    public async Task Collector_https_untrusted_without_caFile_is_refused_and_allowInvalid_accepts()
    {
        var pki = _pki.Value;
        using var server = new FakeHttpsCollector(TlsMaterial.Load(pki.CombinedPemFile, null, null, "k", "kk"));

        var (sent, dropped) = await PostAsync(new CollectorConfig { Endpoint = server.Endpoint, FlushIntervalMs = 20 }); // system store
        Assert.Equal(0, sent);
        Assert.Equal(1, dropped);
        Assert.Equal(0, server.Requests);

        var (sent2, _) = await PostAsync(new CollectorConfig { Endpoint = server.Endpoint, AllowInvalidCertificates = true, FlushIntervalMs = 20 });
        Assert.Equal(1, sent2);
    }

    [Fact]
    public async Task Collector_https_wrong_ca_is_refused()
    {
        var pki = _pki.Value;
        var other = TestCertificates.Create(Path.Combine(_dir, "other"), "Unrelated CA");
        using var server = new FakeHttpsCollector(TlsMaterial.Load(pki.CombinedPemFile, null, null, "k", "kk"));
        var (sent, dropped) = await PostAsync(new CollectorConfig { Endpoint = server.Endpoint, CaFile = other.CaFile, FlushIntervalMs = 20 });
        Assert.Equal(0, sent);
        Assert.Equal(1, dropped);
    }

    [Fact]
    public void Collector_ca_file_must_exist()
    {
        var e = Assert.Throws<ConfigException>(() => new CollectorSender(new CollectorConfig { Endpoint = "https://x/", CaFile = "/nope/ca.pem" }, "s", new Counter()));
        Assert.Contains("collector.caFile", e.Message);
    }
}

internal static class TlsPkiHelpers
{
    public static X509Certificate2 SelfSigned(DateTimeOffset from, DateTimeOffset to)
    {
        using var key = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest("CN=test", key, System.Security.Cryptography.HashAlgorithmName.SHA256);
        return req.CreateSelfSigned(from, to);
    }
}
