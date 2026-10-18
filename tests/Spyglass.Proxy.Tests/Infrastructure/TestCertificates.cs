using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Spyglass.Proxy.Tests.Infrastructure;

/// <summary>A throwaway CA plus a localhost leaf for mongod (06 §2.4).</summary>
public sealed class TestCertificates
{
    public required string CaFile { get; init; }
    /// <summary>Leaf certificate and private key in one PEM, as mongod's --tlsCertificateKeyFile wants.</summary>
    public required string ServerPemFile { get; init; }
    public required string ServerCertFile { get; init; }
    public required string ServerKeyFile { get; init; }

    public static TestCertificates Create(string dir, string caName = "Spyglass Test CA")
    {
        Directory.CreateDirectory(dir);
        using var caKey = RSA.Create(2048);
        var caReq = new CertificateRequest($"CN={caName}", caKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        caReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        caReq.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        caReq.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(caReq.PublicKey, false));
        using var ca = caReq.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));

        using var leafKey = RSA.Create(2048);
        var leafReq = new CertificateRequest("CN=localhost", leafKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        leafReq.CertificateExtensions.Add(san.Build());
        leafReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        leafReq.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        // serverAuth + clientAuth: replica set members present this certificate to each other.
        leafReq.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1"), new Oid("1.3.6.1.5.5.7.3.2")], false));
        leafReq.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(ca, true, false));
        var serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7F;
        using var leaf = leafReq.Create(ca, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddMonths(6), serial);

        var caFile = Path.Combine(dir, "ca.pem");
        File.WriteAllText(caFile, ca.ExportCertificatePem());
        var certPem = leaf.ExportCertificatePem();
        var keyPem = leafKey.ExportPkcs8PrivateKeyPem();
        var certFile = Path.Combine(dir, "server.crt");
        var keyFile = Path.Combine(dir, "server.key");
        var pemFile = Path.Combine(dir, "server.pem");
        File.WriteAllText(certFile, certPem);
        File.WriteAllText(keyFile, keyPem);
        File.WriteAllText(pemFile, certPem + "\n" + keyPem + "\n");
        return new TestCertificates { CaFile = caFile, ServerPemFile = pemFile, ServerCertFile = certFile, ServerKeyFile = keyFile };
    }
}
