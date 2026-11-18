using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Spyglass.Proxy.Config;
using Spyglass.Proxy.Diagnostics;

namespace Spyglass.Proxy.Forwarding;

/// <summary>Opens proxy → member connections, TLS as configured (01 §6.1).</summary>
public sealed class UpstreamConnector
{
    private readonly bool _tls;
    private readonly UpstreamTlsConfig _tlsConfig;
    private readonly X509Certificate2Collection? _trust;
    private readonly X509RevocationMode _revocation;
    private readonly SslStreamCertificateContext? _clientContext;
    private readonly ConcurrentDictionary<HostPort, CertSummary> _serverCerts = new();
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public UpstreamConnector(ProxyConfig config, MongoUri uri)
    {
        _tlsConfig = config.Upstream.Tls;
        _revocation = TlsTrust.Mode(_tlsConfig.Revocation);
        _tls = _tlsConfig.Enabled switch
        {
            TriState.True => true,
            TriState.False => false,
            _ => uri.IsSrv || uri.Tls == true
        };
        if (_tls && _tlsConfig.CaFile != null)
            _trust = TlsTrust.LoadCaFile(_tlsConfig.CaFile, "upstream.tls.caFile");
        if (_tls && _tlsConfig.CertFile != null)
        {
            var loaded = TlsMaterial.Load(_tlsConfig.CertFile, _tlsConfig.KeyFile, _tlsConfig.KeyPassword, "upstream.tls.certFile", "upstream.tls.keyFile");
            _clientContext = SslStreamCertificateContext.Create(loaded.Leaf, loaded.Extras, offline: true);
            ClientCertificate = loaded.Leaf;
            ClientCert = CertSummary.From(loaded.Leaf);
        }
    }

    /// <summary>Summary of the configured client certificate (null if none).</summary>
    internal CertSummary? ClientCert { get; }
    internal X509Certificate2? ClientCertificate { get; }

    /// <summary>Last server certificate seen per upstream member (any handshake, trusted or not).</summary>
    internal CertSummary? ServerCert(HostPort member) => _serverCerts.GetValueOrDefault(member);

    internal IEnumerable<KeyValuePair<HostPort, CertSummary>> ServerCerts => _serverCerts;

    /// <summary>"system", "caFile" or "insecure" (02 §4.2).</summary>
    internal string TrustMode => _tlsConfig.AllowInvalidCertificates ? "insecure" : _trust != null ? "caFile" : "system";

    public bool UsesTls => _tls;

    /// <summary>Test seam: wraps every stream this connector returns (fault injection).</summary>
    internal Func<Stream, Stream>? StreamDecorator { get; set; }

    public async Task<(Socket Socket, Stream Stream)> ConnectAsync(HostPort member, CancellationToken ct)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        try
        {
            socket.NoDelay = true;
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);

            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(ConnectTimeout);
                EndPoint ep = IPAddress.TryParse(member.Host, out var ip) ? new IPEndPoint(ip, member.Port) : new DnsEndPoint(member.Host, member.Port);
                await socket.ConnectAsync(ep, timeout.Token).ConfigureAwait(false);
            }

            Stream stream = new NetworkStream(socket, ownsSocket: true);
            if (!_tls)
                return (socket, StreamDecorator?.Invoke(stream) ?? stream);

            var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(ConnectTimeout);
                await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = member.Host,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    RemoteCertificateValidationCallback = (_, cert, chain, errors) => Validate(member, cert, chain, errors),
                    ClientCertificateContext = _clientContext,
                    CertificateRevocationCheckMode = _revocation,
                }, timeout.Token).ConfigureAwait(false);
            }
            return (socket, StreamDecorator?.Invoke(ssl) ?? ssl);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private bool Validate(HostPort member, X509Certificate? cert, X509Chain? chain, SslPolicyErrors errors)
    {
        if (cert != null)
            Capture(member, cert);
        return TlsTrust.Validate($"upstream TLS {member}", "upstream.tls.caFile", cert, chain, errors,
            _trust, _tlsConfig.AllowInvalidCertificates, _tlsConfig.AllowInvalidHostnames, _revocation);
    }

    // Handshake path: the summary (subject, SANs, ...) is only built when the fingerprint changes.
    private void Capture(HostPort member, X509Certificate cert)
    {
        try
        {
            var fp = CertSummary.Fingerprint(cert);
            if (_serverCerts.TryGetValue(member, out var known) && known.Sha256 == fp) return;
            _serverCerts[member] = CertSummary.From(cert);
        }
        catch (Exception e) when (e is System.Security.Cryptography.CryptographicException or ArgumentException)
        {
            Log.Debug($"upstream TLS {member}: cannot summarize server certificate: {e.Message}");
        }
    }
}
