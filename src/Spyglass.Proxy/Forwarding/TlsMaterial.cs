using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Spyglass.Proxy.Config;
using Spyglass.Proxy.Diagnostics;

namespace Spyglass.Proxy.Forwarding;

/// <summary>A configured certificate with a private key, plus the other certificates that came with it (the chain to send).</summary>
internal sealed record LoadedCert(X509Certificate2 Leaf, X509Certificate2Collection Extras);

/// <summary>
/// Certificate loading shared by <c>listen.tls</c> and <c>upstream.tls</c> client certs (01 §6): PEM cert + key in one
/// file, PEM cert with <c>keyFile</c>, encrypted PEM key with <c>keyPassword</c>, or PKCS#12 (.pfx/.p12, by extension
/// or because the content is not PEM). Loaded once at startup; a bad file is a ConfigException naming the key.
/// </summary>
internal static class TlsMaterial
{
    public static LoadedCert Load(string certFile, string? keyFile, string? keyPassword, string certKey, string keyKey)
    {
        if (!File.Exists(certFile)) throw new ConfigException($"'{certKey}' {certFile} not found");
        if (keyFile != null && !File.Exists(keyFile)) throw new ConfigException($"'{keyKey}' {keyFile} not found");
        try
        {
            var bytes = File.ReadAllBytes(certFile);
            var ext = Path.GetExtension(certFile);
            var pkcs12 = ext.Equals(".pfx", StringComparison.OrdinalIgnoreCase) || ext.Equals(".p12", StringComparison.OrdinalIgnoreCase)
                         || bytes.AsSpan().IndexOf("-----BEGIN"u8) < 0;
            return pkcs12 ? LoadPkcs12(bytes, keyPassword, certKey, certFile) : LoadPem(Encoding.UTF8.GetString(bytes), keyFile, keyPassword, certKey, keyKey, certFile);
        }
        catch (Exception e) when (e is CryptographicException or ArgumentException or FormatException)
        {
            // Messages never contain the password.
            throw new ConfigException($"'{certKey}' {certFile}: cannot load certificate/key: {e.Message}");
        }
    }

    private static LoadedCert LoadPkcs12(byte[] bytes, string? password, string certKey, string certFile)
    {
        var all = X509CertificateLoader.LoadPkcs12Collection(bytes, password);
        var leaf = all.FirstOrDefault(c => c.HasPrivateKey)
                   ?? throw new ConfigException($"'{certKey}' {certFile} contains no certificate with a private key");
        var extras = new X509Certificate2Collection();
        foreach (var c in all)
            if (!ReferenceEquals(c, leaf) && c.Thumbprint != leaf.Thumbprint) extras.Add(c);
        return new LoadedCert(leaf, extras);
    }

    private static LoadedCert LoadPem(string certPem, string? keyFile, string? password, string certKey, string keyKey, string certFile)
    {
        var keyPem = keyFile != null ? File.ReadAllText(keyFile) : certPem;
        if (!keyPem.Contains("PRIVATE KEY", StringComparison.Ordinal))
            throw new ConfigException(keyFile != null
                ? $"'{keyKey}' {keyFile} contains no PEM private key (certificate: '{certKey}' {certFile})"
                : $"'{certKey}' {certFile} contains no private key; set '{keyKey}' or use a combined PEM or .pfx");
        var encrypted = keyPem.Contains("ENCRYPTED", StringComparison.Ordinal);
        if (encrypted && password == null)
            throw new ConfigException($"the private key in {keyFile ?? certFile} is encrypted; set the matching keyPassword");

        using var pem = encrypted
            ? X509Certificate2.CreateFromEncryptedPem(certPem, keyPem, password)
            : X509Certificate2.CreateFromPem(certPem, keyPem);
        // Re-import so the private key is usable by SslStream on every platform.
        var leaf = X509CertificateLoader.LoadPkcs12(pem.Export(X509ContentType.Pkcs12), null);

        var chain = new X509Certificate2Collection();
        chain.ImportFromPem(certPem);
        var extras = new X509Certificate2Collection();
        foreach (var c in chain)
            if (c.Thumbprint != leaf.Thumbprint) extras.Add(c);
        return new LoadedCert(leaf, extras);
    }

    /// <summary>Startup warning for an expired, not-yet-valid or soon-to-expire certificate (null when fine).</summary>
    public static string? ExpiryWarning(X509Certificate2 c, string key, DateTimeOffset now)
    {
        if (c.NotAfter.ToUniversalTime() < now.UtcDateTime)
            return $"{key}: certificate {c.Subject} expired {c.NotAfter.ToUniversalTime():u}";
        if (c.NotBefore.ToUniversalTime() > now.UtcDateTime)
            return $"{key}: certificate {c.Subject} is not valid before {c.NotBefore.ToUniversalTime():u}";
        var left = c.NotAfter.ToUniversalTime() - now.UtcDateTime;
        return left < TimeSpan.FromDays(14)
            ? $"{key}: certificate {c.Subject} expires in {(int)left.TotalDays} days ({c.NotAfter.ToUniversalTime():u})"
            : null;
    }
}

/// <summary>Custom-root trust (a PEM CA bundle) for outgoing TLS: upstream members and the collector (01 §6.1, §11).</summary>
internal static class TlsTrust
{
    public static X509Certificate2Collection LoadCaFile(string path, string key)
    {
        if (!File.Exists(path))
            throw new ConfigException($"'{key}' {path} not found");
        var trust = new X509Certificate2Collection();
        try { trust.ImportFromPemFile(path); }
        catch (CryptographicException e) { throw new ConfigException($"'{key}' {path}: {e.Message}"); }
        if (trust.Count == 0)
            throw new ConfigException($"'{key}' {path} contains no certificates");
        return trust;
    }

    public static X509RevocationMode Mode(RevocationCheck r) => r switch
    {
        RevocationCheck.Online => X509RevocationMode.Online,
        RevocationCheck.Offline => X509RevocationMode.Offline,
        _ => X509RevocationMode.NoCheck
    };

    /// <summary>
    /// The RemoteCertificateValidationCallback body. <paramref name="trust"/> null = system store (the platform's
    /// verdict in <paramref name="errors"/>, revocation per SslClientAuthenticationOptions.CertificateRevocationCheckMode);
    /// otherwise the chain is rebuilt against that bundle only. A name mismatch is its own, separately waivable, failure.
    /// </summary>
    public static bool Validate(string what, string trustKey, X509Certificate? cert, X509Chain? chain, SslPolicyErrors errors,
        X509Certificate2Collection? trust, bool allowInvalidCertificates, bool allowInvalidHostnames, X509RevocationMode revocation)
    {
        if (cert == null)
            return false;
        if (allowInvalidCertificates)
            return true;

        if ((errors & SslPolicyErrors.RemoteCertificateNameMismatch) != 0 && !allowInvalidHostnames)
        {
            Log.Warn($"{what}: certificate name does not match host");
            return false;
        }

        if (trust == null)
            return (errors & ~SslPolicyErrors.RemoteCertificateNameMismatch) == SslPolicyErrors.None;

        using var custom = new X509Chain();
        custom.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        custom.ChainPolicy.CustomTrustStore.AddRange(trust);
        custom.ChainPolicy.RevocationMode = revocation;
        if (chain != null)
        {
            // intermediates the peer sent (the platform puts them in ExtraStore even when its own build was partial)
            custom.ChainPolicy.ExtraStore.AddRange(chain.ChainPolicy.ExtraStore);
            foreach (var el in chain.ChainElements.Skip(1))
                custom.ChainPolicy.ExtraStore.Add(el.Certificate);
        }
        using var leaf = new X509Certificate2(cert);
        var ok = custom.Build(leaf);
        if (!ok)
            Log.Warn($"{what}: certificate not trusted by {trustKey}: " +
                     string.Join("; ", custom.ChainStatus.Select(s => s.StatusInformation.Trim())));
        return ok;
    }
}

/// <summary>What the UI shows about a certificate (02 §4.2 CERT). Never contains key material.</summary>
internal sealed class CertSummary
{
    public required string Subject { get; init; }
    public required string Issuer { get; init; }
    public required DateTimeOffset NotBefore { get; init; }
    public required DateTimeOffset NotAfter { get; init; }
    public required string Sha256 { get; init; }
    public required string[] Dns { get; init; }

    public static string Fingerprint(X509Certificate c) => Convert.ToHexString(SHA256.HashData(c.GetRawCertData()));

    public static CertSummary From(X509Certificate c)
    {
        using var c2 = new X509Certificate2(c);
        var dns = new List<string>();
        foreach (var ext in c2.Extensions)
        {
            if (ext is not X509SubjectAlternativeNameExtension san) continue;
            foreach (var n in san.EnumerateDnsNames())
            {
                if (dns.Count >= 16) break;
                dns.Add(n);
            }
        }
        return new CertSummary
        {
            Subject = c2.Subject,
            Issuer = c2.Issuer,
            NotBefore = c2.NotBefore.ToUniversalTime(),
            NotAfter = c2.NotAfter.ToUniversalTime(),
            Sha256 = Fingerprint(c2),
            Dns = dns.ToArray(),
        };
    }

    private static long Micros(DateTimeOffset t) => (t.UtcTicks - DateTime.UnixEpoch.Ticks) / 10;

    public long NotAfterUnixSeconds => NotAfter.ToUnixTimeSeconds();

    public void Write(Utf8JsonWriter j)
    {
        j.WriteStartObject();
        j.WriteString("subject", Subject);
        j.WriteString("issuer", Issuer);
        j.WriteNumber("notBefore", Micros(NotBefore));
        j.WriteNumber("notAfter", Micros(NotAfter));
        j.WriteString("sha256", Sha256);
        j.WriteStartArray("dns");
        foreach (var d in Dns) j.WriteStringValue(d);
        j.WriteEndArray();
        j.WriteEndObject();
    }
}
