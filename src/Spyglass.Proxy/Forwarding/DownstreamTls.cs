using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Spyglass.Proxy.Config;

namespace Spyglass.Proxy.Forwarding;

/// <summary>Client → proxy TLS (01 §6.2).</summary>
public sealed class DownstreamTls
{
    public X509Certificate2 Certificate { get; }
    public string Sha256Fingerprint { get; }
    public bool SelfSigned { get; }
    private readonly SslServerAuthenticationOptions _options;

    public DownstreamTls(DownstreamTlsConfig config, string advertiseHost)
    {
        if (config.CertFile != null)
        {
            if (!File.Exists(config.CertFile)) throw new ConfigException($"'listen.tls.certFile' {config.CertFile} not found");
            if (!File.Exists(config.KeyFile)) throw new ConfigException($"'listen.tls.keyFile' {config.KeyFile} not found");
            using var pem = X509Certificate2.CreateFromPemFile(config.CertFile, config.KeyFile);
            // Re-import so the private key is usable by SslStream on every platform.
            Certificate = X509CertificateLoader.LoadPkcs12(pem.Export(X509ContentType.Pkcs12), null);
        }
        else
        {
            Certificate = CreateSelfSigned(advertiseHost);
            SelfSigned = true;
        }
        Sha256Fingerprint = Convert.ToHexString(SHA256.HashData(Certificate.RawData));
        _options = new SslServerAuthenticationOptions
        {
            ServerCertificate = Certificate,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            ClientCertificateRequired = false,
        };
    }

    public async Task<SslStream> AuthenticateAsync(Stream inner, CancellationToken ct)
    {
        var ssl = new SslStream(inner, leaveInnerStreamOpen: false);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await ssl.AuthenticateAsServerAsync(_options, timeout.Token).ConfigureAwait(false);
            return ssl;
        }
        catch
        {
            await ssl.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static X509Certificate2 CreateSelfSigned(string host)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest($"CN={host}", key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        if (IPAddress.TryParse(host, out var ip)) san.AddIpAddress(ip);
        else san.AddDnsName(host);
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        san.AddIpAddress(IPAddress.IPv6Loopback);
        req.CertificateExtensions.Add(san.Build());
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(1));
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pkcs12), null);
    }

    /// <summary>PEM of the certificate, so clients can pin it with <c>tlsCAFile</c>.</summary>
    public string CertificatePem => Certificate.ExportCertificatePem();
}
