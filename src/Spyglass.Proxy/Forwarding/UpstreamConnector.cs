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
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public UpstreamConnector(ProxyConfig config, MongoUri uri)
    {
        _tlsConfig = config.Upstream.Tls;
        _tls = _tlsConfig.Enabled switch
        {
            TriState.True => true,
            TriState.False => false,
            _ => uri.IsSrv || uri.Tls == true
        };
        if (_tls && _tlsConfig.CaFile != null)
        {
            if (!File.Exists(_tlsConfig.CaFile))
                throw new ConfigException($"'upstream.tls.caFile' {_tlsConfig.CaFile} not found");
            _trust = [];
            _trust.ImportFromPemFile(_tlsConfig.CaFile);
            if (_trust.Count == 0)
                throw new ConfigException($"'upstream.tls.caFile' {_tlsConfig.CaFile} contains no certificates");
        }
    }

    public bool UsesTls => _tls;

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
                return (socket, stream);

            var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(ConnectTimeout);
                await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = member.Host,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    RemoteCertificateValidationCallback = (_, cert, chain, errors) => Validate(member, cert, chain, errors),
                }, timeout.Token).ConfigureAwait(false);
            }
            return (socket, ssl);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private bool Validate(HostPort member, X509Certificate? cert, X509Chain? chain, SslPolicyErrors errors)
    {
        if (cert == null)
            return false;
        if (_tlsConfig.AllowInvalidCertificates)
            return true;

        if ((errors & SslPolicyErrors.RemoteCertificateNameMismatch) != 0 && !_tlsConfig.AllowInvalidHostnames)
        {
            Log.Warn($"upstream TLS {member}: certificate name does not match host");
            return false;
        }

        if (_trust == null)
            return (errors & ~SslPolicyErrors.RemoteCertificateNameMismatch) == SslPolicyErrors.None;

        // Custom trust: rebuild the chain against the configured CA bundle only.
        using var custom = new X509Chain();
        custom.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        custom.ChainPolicy.CustomTrustStore.AddRange(_trust);
        custom.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        if (chain != null)
            foreach (var el in chain.ChainElements.Skip(1))
                custom.ChainPolicy.ExtraStore.Add(el.Certificate);
        var ok = custom.Build(new X509Certificate2(cert));
        if (!ok)
            Log.Warn($"upstream TLS {member}: certificate not trusted by upstream.tls.caFile: " +
                     string.Join("; ", custom.ChainStatus.Select(s => s.StatusInformation.Trim())));
        return ok;
    }
}
