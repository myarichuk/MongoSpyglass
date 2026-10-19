using System.Net.Sockets;
using Spyglass.Proxy.Config;
using Spyglass.Proxy.Forwarding;
using Spyglass.Proxy.Wire;

namespace Spyglass.Proxy.Topology;

/// <summary>
/// A proxy-internal monitor connection (01 §5.1): unauthenticated <c>hello</c> on a persistent
/// connection. Never reported as client traffic.
/// </summary>
internal sealed class HelloClient : IAsyncDisposable
{
    private readonly UpstreamConnector _connector;
    private readonly HostPort _target;
    private Socket? _socket;
    private Stream? _stream;
    private bool _handshaken;
    private int _requestId;

    public HelloClient(UpstreamConnector connector, HostPort target)
    {
        _connector = connector;
        _target = target;
    }

    public async Task<HelloReplyFacts> HelloAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            if (_stream == null)
            {
                (_socket, _stream) = await _connector.ConnectAsync(_target, cts.Token).ConfigureAwait(false);
                _handshaken = false;
            }

            var frame = BuildHello(++_requestId, includeClient: !_handshaken);
            await _stream.WriteAsync(frame, cts.Token).ConfigureAwait(false);

            var header = new byte[FrameHeader.Size];
            await _stream.ReadExactlyAsync(header, cts.Token).ConfigureAwait(false);
            var h = FrameHeader.Read(header);
            if (h.MessageLength < FrameHeader.Size || h.MessageLength > ConnectionContext.DefaultMaxFrame)
                throw new IOException($"invalid hello reply length {h.MessageLength}");
            var reply = new byte[h.MessageLength];
            header.CopyTo(reply, 0);
            await _stream.ReadExactlyAsync(reply.AsMemory(FrameHeader.Size), cts.Token).ConfigureAwait(false);

            var facts = new HelloReplyFacts();
            HelloRewriter.ExtractFacts(reply, facts);
            var doc = HelloRewriter.FirstDocument(reply);
            if (doc.IsEmpty)
                throw new IOException("hello reply has no document");
            if (Bson.TryGetElement(doc, "ok"u8, out var ok) && ok.AsDouble() != 1)
                throw new IOException("hello failed: " + (Bson.TryGetElement(doc, "errmsg"u8, out var m) ? m.AsString() : "ok != 1"));
            _handshaken = true;
            return facts;
        }
        catch
        {
            await ResetAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static byte[] BuildHello(int requestId, bool includeClient)
    {
        var w = new BsonWriter(256);
        w.WriteRaw(new byte[FrameHeader.Size]);
        w.WriteInt32Raw(0);   // flags
        w.WriteByteRaw(0);    // kind 0
        w.StartDocument();
        w.WriteInt32("hello", 1);
        w.WriteBoolean("helloOk", true);
        if (includeClient)
        {
            w.StartDocument("client");
            w.StartDocument("driver");
            w.WriteString("name", "spyglass-proxy");
            w.WriteString("version", typeof(HelloClient).Assembly.GetName().Version?.ToString(3) ?? "0");
            w.EndDocument();
            w.StartDocument("os");
            w.WriteString("type", OperatingSystem.IsLinux() ? "Linux" : OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "Darwin" : "unknown");
            w.EndDocument();
            w.EndDocument();
        }
        w.WriteString("$db", "admin");
        w.EndDocument();
        var frame = w.ToArray();
        new FrameHeader(frame.Length, requestId, 0, OpCodes.Msg).Write(frame);
        return frame;
    }

    private async ValueTask ResetAsync()
    {
        if (_stream != null)
            await _stream.DisposeAsync().ConfigureAwait(false);
        _socket?.Dispose();
        _stream = null;
        _socket = null;
    }

    public ValueTask DisposeAsync() => ResetAsync();
}
