using System.Net.Sockets;
using Spyglass.Proxy.Diagnostics;
using Spyglass.Proxy.Topology;

namespace Spyglass.Proxy.Forwarding;

/// <summary>One client connection and its dedicated upstream connection (I3, 01 §3.1).</summary>
internal sealed class ProxyConnection
{
    private static readonly TimeSpan HalfCloseGrace = TimeSpan.FromSeconds(30);

    private readonly ProxyServer _server;
    private readonly Member _member;
    private readonly Socket _client;
    private readonly ConnectionContext _ctx;

    public ProxyConnection(ProxyServer server, Member member, Socket client, long connSeq)
    {
        _server = server;
        _member = member;
        _client = client;
        _ctx = new ConnectionContext(connSeq, member, server, client.RemoteEndPoint?.ToString() ?? "?");
    }

    public ConnectionContext Context => _ctx;

    public async Task RunAsync(CancellationToken shutdown)
    {
        Stream? clientStream = null;
        Socket? upSocket = null;
        Stream? upStream = null;
        var observer = _server.Observer;
        var reason = CloseReason.ProxyShutdown;
        string? detail = null;
        var opened = false;

        try
        {
            _client.NoDelay = true;
            _client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            clientStream = new NetworkStream(_client, ownsSocket: true);

            if (_server.DownstreamTls is { } tls)
            {
                try
                {
                    clientStream = await tls.AuthenticateAsync(clientStream, shutdown).ConfigureAwait(false);
                    _ctx.ClientTls = true;
                }
                catch (Exception e) when (e is not OperationCanceledException || !shutdown.IsCancellationRequested)
                {
                    _member.AcceptedTlsFailed.Inc();
                    Log.Debug($"conn {_ctx.ConnSeq} from {_ctx.ClientEndpoint}: client TLS handshake failed: {e.Message}");
                    reason = CloseReason.TlsFailed;
                    detail = e.Message;
                    return;
                }
            }

            try
            {
                (upSocket, upStream) = await _server.Upstream.ConnectAsync(_member.Upstream, shutdown).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException || !shutdown.IsCancellationRequested)
            {
                // Don't accept bytes we can't deliver (01 §3.1 step 3).
                _member.AcceptedUpstreamFailed.Inc();
                Log.Warn($"conn {_ctx.ConnSeq}: upstream {_member.Upstream} connect failed: {e.Message}");
                reason = CloseReason.UpstreamConnectFailed;
                detail = e.Message;
                observer.Closed(_ctx, reason, detail);
                return;
            }

            _member.AcceptedOk.Inc();
            _member.OpenConnections.Inc();
            opened = true;
            observer.Opened(_ctx);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
            var config = _server.Config;
            var c2s = new Pump(clientStream, upStream, isRequest: true, _ctx, observer.CreateTap(_ctx, true), config,
                _member.BytesFromClient, _member.FramesFromClient, _member.AddedLatency);
            var s2c = new Pump(upStream, clientStream, isRequest: false, _ctx, observer.CreateTap(_ctx, false), config,
                _member.BytesFromServer, _member.FramesFromServer, _member.AddedLatency);

            var c2sTask = c2s.RunAsync(cts.Token);
            var s2cTask = s2c.RunAsync(cts.Token);
            var firstTask = await Task.WhenAny(c2sTask, s2cTask).ConfigureAwait(false);
            var firstIsClient = firstTask == c2sTask;
            var first = await firstTask.ConfigureAwait(false);
            var firstPump = firstIsClient ? c2s : s2c;

            (reason, detail) = Classify(first, firstIsClient, firstPump);

            if (first == PumpEnd.SourceClosed)
            {
                // Propagate the half-close and let the other direction drain (01 §3.1 step 5).
                await TryShutdownSendAsync(firstIsClient ? upSocket : _client, firstIsClient ? upStream : clientStream).ConfigureAwait(false);
                var other = firstIsClient ? s2cTask : c2sTask;
                if (await Task.WhenAny(other, Task.Delay(HalfCloseGrace, shutdown)).ConfigureAwait(false) != other)
                    cts.Cancel();
            }
            else
            {
                cts.Cancel();
            }

            try { await Task.WhenAll(c2sTask, s2cTask).ConfigureAwait(false); }
            catch { /* pumps report through PumpEnd; nothing to add */ }

            if (first == PumpEnd.InvalidFrame)
            {
                _member.InvalidFrames.Inc();
                Log.Warn($"conn {_ctx.ConnSeq} ({_member.Advertised}): invalid frame from {(firstIsClient ? "client" : "server")}, header {firstPump.InvalidFrameHeaderHex}; closing");
            }
        }
        catch (Exception e)
        {
            Log.Error($"conn {_ctx.ConnSeq}: unexpected {e.GetType().Name}: {e.Message}");
            detail ??= e.Message;
        }
        finally
        {
            if (upStream != null) await SafeDisposeAsync(upStream).ConfigureAwait(false);
            upSocket?.Dispose();
            if (clientStream != null) await SafeDisposeAsync(clientStream).ConfigureAwait(false);
            _client.Dispose();
            if (opened)
            {
                _member.OpenConnections.Dec();
                observer.Closed(_ctx, reason, detail);
            }
            Log.Debug($"conn {_ctx.ConnSeq} closed: {reason.Wire()} in={_ctx.BytesFromClient} out={_ctx.BytesFromServer}");
        }
    }

    private static (CloseReason, string?) Classify(PumpEnd end, bool clientSide, Pump pump) => end switch
    {
        PumpEnd.InvalidFrame => (CloseReason.InvalidFrame, pump.InvalidFrameHeaderHex),
        PumpEnd.Cancelled => (CloseReason.ProxyShutdown, null),
        PumpEnd.SourceClosed or PumpEnd.SourceFailed => (clientSide ? CloseReason.ClientClosed : CloseReason.UpstreamClosed, pump.Failure?.Message),
        // a write failure is the *other* side going away
        PumpEnd.DestinationFailed => (clientSide ? CloseReason.UpstreamClosed : CloseReason.ClientClosed, pump.Failure?.Message),
        _ => (CloseReason.ProxyShutdown, null),
    };

    private static async ValueTask TryShutdownSendAsync(Socket? socket, Stream? stream)
    {
        try
        {
            if (stream is System.Net.Security.SslStream ssl)
                await ssl.ShutdownAsync().ConfigureAwait(false); // TLS close_notify, then FIN below
            socket?.Shutdown(SocketShutdown.Send);
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException or IOException or InvalidOperationException)
        {
        }
    }

    private static async ValueTask SafeDisposeAsync(Stream s)
    {
        try { await s.DisposeAsync().ConfigureAwait(false); }
        catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException) { }
    }
}
