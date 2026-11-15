using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Spyglass.Collector;

/// <summary>
/// The collector's HTTPS certificate (03 §9): <c>SPYGLASS_TLS_CERT</c> is a PEM certificate (with its chain, and with
/// the key when <c>SPYGLASS_TLS_KEY</c> is unset) or a .pfx/.p12; <c>SPYGLASS_TLS_KEY_PASSWORD</c> opens an encrypted key
/// or PKCS#12. Errors are <see cref="ArgumentException"/>s naming the variable, like the other option errors.
/// </summary>
public sealed class CollectorTls
{
    /// <summary>The server certificate, with a private key a Linux TLS stack can use.</summary>
    public required X509Certificate2 Certificate { get; init; }
    /// <summary>The intermediates sent after the leaf (empty for a lone certificate or a PKCS#12 without a chain).</summary>
    public required X509Certificate2Collection Chain { get; init; }

    public static readonly TimeSpan ExpiryWarning = TimeSpan.FromDays(14);

    public static CollectorTls Load(string certPath, string? keyPath, string? password)
    {
        if (!File.Exists(certPath)) throw new ArgumentException($"SPYGLASS_TLS_CERT: file not found: {certPath}");
        if (keyPath != null && !File.Exists(keyPath)) throw new ArgumentException($"SPYGLASS_TLS_KEY: file not found: {keyPath}");
        var pw = string.IsNullOrEmpty(password) ? null : password;
        var ext = Path.GetExtension(certPath).ToLowerInvariant();
        return ext is ".pfx" or ".p12" ? FromPkcs12(certPath, pw) : FromPem(certPath, keyPath, pw);
    }

    private static CollectorTls FromPkcs12(string path, string? password)
    {
        try
        {
            var all = X509CertificateLoader.LoadPkcs12CollectionFromFile(path, password);
            var leaf = all.FirstOrDefault(c => c.HasPrivateKey)
                ?? throw new ArgumentException($"SPYGLASS_TLS_CERT: {path} has no private key");
            var chain = new X509Certificate2Collection();
            foreach (var c in all) if (!ReferenceEquals(c, leaf)) chain.Add(c);
            return new CollectorTls { Certificate = leaf, Chain = chain };
        }
        catch (CryptographicException e)
        {
            throw new ArgumentException($"SPYGLASS_TLS_CERT: cannot read {path} as PKCS#12 ({e.Message}); " +
                                        "if it is password protected set SPYGLASS_TLS_KEY_PASSWORD");
        }
    }

    private static CollectorTls FromPem(string certPath, string? keyPath, string? password)
    {
        string certText = File.ReadAllText(certPath), keyText = keyPath != null ? File.ReadAllText(keyPath) : certText;
        var certs = new List<X509Certificate2>();
        try
        {
            foreach (var pem in PemBlocks(certText, "CERTIFICATE")) certs.Add(X509Certificate2.CreateFromPem(pem));
        }
        catch (CryptographicException e)
        {
            throw new ArgumentException($"SPYGLASS_TLS_CERT: {certPath} holds an unreadable certificate ({e.Message})");
        }
        if (certs.Count == 0) throw new ArgumentException($"SPYGLASS_TLS_CERT: no PEM CERTIFICATE block in {certPath}");
        var keyVar = keyPath != null ? "SPYGLASS_TLS_KEY" : "SPYGLASS_TLS_CERT";
        var keyFile = keyPath ?? certPath;
        if (!keyText.Contains("PRIVATE KEY-----", StringComparison.Ordinal))
            throw new ArgumentException(keyPath != null
                ? $"SPYGLASS_TLS_KEY: no PEM private key in {keyFile}"
                : $"SPYGLASS_TLS_CERT: {certPath} has no private key; set SPYGLASS_TLS_KEY (or use a combined PEM or .pfx)");
        // the leaf is the certificate the key belongs to (a bundle may list the chain in either order)
        keyText = KeyBlock(keyText); // just the key: a combined file also holds certificates
        var encrypted = keyText.Contains("ENCRYPTED PRIVATE KEY", StringComparison.Ordinal);
        if (encrypted && password == null)
            throw new ArgumentException($"{keyVar}: the key in {keyFile} is encrypted; set SPYGLASS_TLS_KEY_PASSWORD");
        X509Certificate2? leaf = null;
        foreach (var c in certs)
        {
            try
            {
                using AsymmetricAlgorithm key = c.GetRSAPublicKey() != null ? RSA.Create() : c.GetECDsaPublicKey() != null ? ECDsa.Create()
                    : throw new ArgumentException($"SPYGLASS_TLS_CERT: {certPath} has a key type other than RSA or ECDSA");
                if (encrypted) key.ImportFromEncryptedPem(keyText, password);
                else key.ImportFromPem(keyText);
                using var withKey = key is RSA rsa ? c.CopyWithPrivateKey(rsa) : c.CopyWithPrivateKey((ECDsa)key);
                // PEM key → PKCS#12 → load: an in-memory key is not usable by SslStream on every platform
                leaf = X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.Exportable);
                break;
            }
            catch (Exception e) when (e is CryptographicException or ArgumentException { ParamName: not null } or InvalidOperationException) { } // not this certificate's key (or a wrong password, reported below)
        }
        if (leaf == null)
            throw new ArgumentException($"{keyVar}: the private key in {keyFile} matches no certificate in {certPath}" +
                                        (password != null ? " (or SPYGLASS_TLS_KEY_PASSWORD is wrong)" : ""));
        var chain = new X509Certificate2Collection();
        foreach (var c in certs) if (c.Thumbprint != leaf.Thumbprint) chain.Add(c);
        return new CollectorTls { Certificate = leaf, Chain = chain };
    }

    private static string KeyBlock(string text)
    {
        var rest = text.AsSpan();
        while (PemEncoding.TryFind(rest, out var f))
        {
            if (rest[f.Label].EndsWith("PRIVATE KEY", StringComparison.Ordinal)) return rest[f.Location].ToString();
            rest = rest[f.Location.End..];
        }
        return text;
    }

    private static IEnumerable<string> PemBlocks(string text, string label)
    {
        string begin = $"-----BEGIN {label}-----", end = $"-----END {label}-----";
        for (var i = text.IndexOf(begin, StringComparison.Ordinal); i >= 0; i = text.IndexOf(begin, i, StringComparison.Ordinal))
        {
            var e = text.IndexOf(end, i, StringComparison.Ordinal);
            if (e < 0) yield break;
            e += end.Length;
            yield return text[i..e];
            i = e;
        }
    }
}
