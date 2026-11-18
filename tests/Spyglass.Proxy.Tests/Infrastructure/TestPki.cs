using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Spyglass.Proxy.Tests.Infrastructure;

/// <summary>
/// root CA → intermediate CA → localhost server leaf, plus a client certificate issued by the root, written in every
/// format the proxy accepts (combined PEM, cert + key, encrypted key, .pfx).
/// </summary>
public sealed class TestPki
{
    public const string Password = "s3cret-pw!";

    public required string RootCaFile { get; init; }
    /// <summary>Leaf + intermediate (chain order), no key.</summary>
    public required string ChainCertFile { get; init; }
    public required string LeafKeyFile { get; init; }
    /// <summary>Leaf + intermediate + private key in one PEM.</summary>
    public required string CombinedPemFile { get; init; }
    /// <summary>PKCS#8 key encrypted with <see cref="Password"/>.</summary>
    public required string EncryptedKeyFile { get; init; }
    /// <summary>Leaf + intermediate + key, PKCS#12 protected with <see cref="Password"/>.</summary>
    public required string PfxFile { get; init; }
    /// <summary>Leaf only, no intermediate.</summary>
    public required string LeafOnlyCertFile { get; init; }
    public required string ClientCertFile { get; init; }
    public required string ClientKeyFile { get; init; }
    public required string ClientCombinedPemFile { get; init; }
    public required string ClientPfxFile { get; init; }

    /// <summary>Mongod-style view: the root as CA, the chain PEM as server certificate.</summary>
    public TestCertificates AsTestCertificates() => new()
    {
        CaFile = RootCaFile,
        ServerPemFile = CombinedPemFile,
        ServerCertFile = ChainCertFile,
        ServerKeyFile = LeafKeyFile,
    };

    private static X509Certificate2 Issue(string cn, X509Certificate2? issuer, RSA key, bool ca, bool client, DateTimeOffset notAfter, bool server = true)
    {
        var req = new CertificateRequest($"CN={cn}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(ca, false, 0, true));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
        if (ca)
            req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        else
        {
            req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
            var eku = new OidCollection();
            if (server) eku.Add(new Oid("1.3.6.1.5.5.7.3.1"));
            if (client) eku.Add(new Oid("1.3.6.1.5.5.7.3.2"));
            req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(eku, false));
            if (server)
            {
                var san = new SubjectAlternativeNameBuilder();
                san.AddDnsName("localhost");
                san.AddIpAddress(IPAddress.Loopback);
                req.CertificateExtensions.Add(san.Build());
            }
        }
        var from = DateTimeOffset.UtcNow.AddDays(-1);
        if (issuer == null)
            return req.CreateSelfSigned(from, notAfter);
        req.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(issuer, true, false));
        var serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7F;
        using var signed = req.Create(issuer, from, notAfter, serial);
        return signed.CopyWithPrivateKey(key);
    }

    public static TestPki Create(string dir, int leafValidDays = 180)
    {
        Directory.CreateDirectory(dir);
        using var rootKey = RSA.Create(2048);
        using var interKey = RSA.Create(2048);
        using var leafKey = RSA.Create(2048);
        using var clientKey = RSA.Create(2048);
        using var root = Issue("Spyglass Test Root", null, rootKey, ca: true, client: false, DateTimeOffset.UtcNow.AddYears(2));
        using var inter = Issue("Spyglass Test Intermediate", root, interKey, ca: true, client: false, DateTimeOffset.UtcNow.AddYears(1));
        using var leaf = Issue("localhost", inter, leafKey, ca: false, client: true, DateTimeOffset.UtcNow.AddDays(leafValidDays));
        using var client = Issue("spyglass-client", root, clientKey, ca: false, client: true, DateTimeOffset.UtcNow.AddDays(180), server: false);

        string F(string n) => Path.Combine(dir, n);
        var leafPem = leaf.ExportCertificatePem() + "\n";
        var interPem = inter.ExportCertificatePem() + "\n";
        var keyPem = leafKey.ExportPkcs8PrivateKeyPem() + "\n";
        var clientPem = client.ExportCertificatePem() + "\n";
        var clientKeyPem = clientKey.ExportPkcs8PrivateKeyPem() + "\n";

        File.WriteAllText(F("root.pem"), root.ExportCertificatePem());
        File.WriteAllText(F("chain.crt"), leafPem + interPem);
        File.WriteAllText(F("leaf.key"), keyPem);
        File.WriteAllText(F("combined.pem"), leafPem + interPem + keyPem);
        File.WriteAllText(F("leaf-encrypted.key"), leafKey.ExportEncryptedPkcs8PrivateKeyPem(Password,
            new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 10_000)));
        File.WriteAllText(F("leaf-only.crt"), leafPem);
        File.WriteAllBytes(F("chain.pfx"), new X509Certificate2Collection { leaf, X509CertificateLoader.LoadCertificate(inter.RawData) }.Export(X509ContentType.Pkcs12, Password)!);
        File.WriteAllText(F("client.crt"), clientPem);
        File.WriteAllText(F("client.key"), clientKeyPem);
        File.WriteAllText(F("client.pem"), clientPem + clientKeyPem);
        File.WriteAllBytes(F("client.p12"), client.Export(X509ContentType.Pkcs12, Password));

        return new TestPki
        {
            RootCaFile = F("root.pem"),
            ChainCertFile = F("chain.crt"),
            LeafKeyFile = F("leaf.key"),
            CombinedPemFile = F("combined.pem"),
            EncryptedKeyFile = F("leaf-encrypted.key"),
            PfxFile = F("chain.pfx"),
            LeafOnlyCertFile = F("leaf-only.crt"),
            ClientCertFile = F("client.crt"),
            ClientKeyFile = F("client.key"),
            ClientCombinedPemFile = F("client.pem"),
            ClientPfxFile = F("client.p12"),
        };
    }
}
