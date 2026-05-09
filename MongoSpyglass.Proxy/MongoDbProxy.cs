using System.Net.Sockets;
using System.Net;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Polly.Retry;
using Polly;
using MongoSpyglass.Proxy.WireProtocol;
using MongoSpyglass.Proxy.WireProtocol.Raw;
using SharpArena.Allocators;
using SharpArena.Collections;
using MongoSpyglass.Proxy.Profiling;
using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Buffers;
using MongoSpyglass.Proxy.Bson;
using MongoSpyglass.Proxy.Memory;

namespace MongoSpyglass.Proxy;

public class MongoDbProxy : IHostedService
{
    private readonly AsyncRetryPolicy _retryPolicy;
    private readonly IProxySettingsProvider _settingsProvider;
    private readonly ILogger<MongoDbProxy> _logger;
    private readonly IEnumerable<ITrafficListener> _listeners;
    private readonly object _lifecycleLock = new();
    
    private IPEndPoint _mongoDbServer = new(IPAddress.Loopback, 27017);
    private int _port = 27018;
    private TcpListener? _listener;
    private CancellationTokenSource? _runCts;
    private Task? _acceptTask;

    public MongoDbProxy(IProxySettingsProvider settingsProvider, ILogger<MongoDbProxy> logger, IEnumerable<ITrafficListener> listeners)
    {
        _settingsProvider = settingsProvider;
        _logger = logger;
        _listeners = listeners;

        _retryPolicy = Policy
            .Handle<SocketException>()
            .WaitAndRetryAsync(3,
                attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt)),
                (exception, timeSpan, retryCount, context) =>
                {
                    _logger.LogError(exception, $"Retry {retryCount} after {timeSpan.Seconds} seconds delay");
                });

        _settingsProvider.OnSettingsChanged += HandleSettingsChanged;
    }

    private void HandleSettingsChanged()
    {
        _logger.LogInformation("Proxy settings changed, restarting listener...");
        _ = RestartProxyAsync();
    }

    private async Task RestartProxyAsync()
    {
        await StopProxyInternalAsync();
        StartProxyInternal();
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        StartProxyInternal();
        return Task.CompletedTask;
    }

    private void StartProxyInternal()
    {
        lock (_lifecycleLock)
        {
            if (_runCts != null) return; // Already running

            var settings = _settingsProvider.GetCurrentSettings();
            _mongoDbServer = settings.TargetServer;
            _port = settings.IncomingPort;

            _runCts = new CancellationTokenSource();
            _listener = new TcpListener(IPAddress.Any, _port);
            
            try 
            {
                _listener.Start();
                _logger.LogInformation($"Proxy started: listening on port {_port}, forwarding to {_mongoDbServer}");
                _acceptTask = Task.Run(() => AcceptConnectionsAsync(_runCts.Token), _runCts.Token);
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, $"Failed to start proxy listener on port {_port}");
                _runCts.Cancel();
                _runCts = null;
            }
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await StopProxyInternalAsync();
    }

    private async Task StopProxyInternalAsync()
    {
        CancellationTokenSource? cts;
        Task? acceptTask;

        lock (_lifecycleLock)
        {
            _logger.LogInformation("Stopping proxy listener...");
            _listener?.Stop();
            _listener = null;
            cts = _runCts;
            acceptTask = _acceptTask;
            _runCts = null;
            _acceptTask = null;
        }

        if (cts != null)
        {
            cts.Cancel();
            if (acceptTask != null)
            {
                try { await acceptTask; } catch { }
            }
            cts.Dispose();
        }
    }

    private async Task AcceptConnectionsAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception e)
            {
                if (!ct.IsCancellationRequested)
                {
                    _logger.LogError(e, "Error accepting client connection");
                }
                continue;
            }

            _ = Task.Run(() => ProxyConnection(client, ct), ct);
        }
    }

    private async Task ProxyConnection(TcpClient client, CancellationToken ct)
    {
        using var clientScope = client;
        var connectionId = Guid.NewGuid().ToString("N");
        _logger.LogDebug($"Accepted connection {connectionId} from {client.Client.RemoteEndPoint}");

        using var server = new TcpClient();
        try 
        {
            await server.ConnectAsync(_mongoDbServer.Address, _mongoDbServer.Port, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Failed to connect {connectionId} to target MongoDB server {_mongoDbServer}");
            return;
        }

        _logger.LogDebug($"Connected {connectionId} to MongoDB server {_mongoDbServer}");

        using var correlationBuffer = new CorrelationRingBuffer();
        using var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var clientStream = client.GetStream();
        var serverStream = server.GetStream();

        var clientReader = PipeReader.Create(clientStream);
        var clientWriter = PipeWriter.Create(clientStream);
        var serverReader = PipeReader.Create(serverStream);
        var serverWriter = PipeWriter.Create(serverStream);

        var t1 = ProcessPipeAsync(clientReader, serverWriter, "to", connectionId, correlationBuffer, connectionCts.Token);
        var t2 = ProcessPipeAsync(serverReader, clientWriter, "from", connectionId, correlationBuffer, connectionCts.Token);

        await Task.WhenAny(t1, t2);
        connectionCts.Cancel();
        try
        {
            await Task.WhenAll(t1, t2);
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            _logger.LogTrace(e, "Error waiting for proxy pipelines to complete");
        }
        finally
        {
            foreach (var listener in _listeners)
            {
                listener.OnConnectionClosed(connectionId);
            }
        }
    }

    private async Task ProcessPipeAsync(PipeReader reader, PipeWriter writer, string tag, string connectionId, CorrelationRingBuffer correlationBuffer, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                ReadResult result = await reader.ReadAsync(ct);
                ReadOnlySequence<byte> buffer = result.Buffer;

                while (TryReadMessage(ref buffer, out var message))
                {
                    // 1. Immediate Forward
                    foreach (var segment in message)
                    {
                        await writer.WriteAsync(segment, ct);
                    }
                    await writer.FlushAsync(ct);

                    // 2. Async Observability
                    ObserveMessage(tag, connectionId, message, correlationBuffer);
                }

                reader.AdvanceTo(buffer.Start, buffer.End);

                if (result.IsCompleted)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogTrace($"Pipeline {tag} cancelled");
        }
        catch (IOException e) when (e.InnerException is SocketException { SocketErrorCode: SocketError.OperationAborted } or SocketException { SocketErrorCode: SocketError.ConnectionReset })
        {
            _logger.LogTrace($"Pipeline {tag} closed: {e.Message}");
        }
        catch (Exception e)
        {
            _logger.LogError(e, $"Error processing pipeline for {tag}");
        }
        finally
        {
            await reader.CompleteAsync();
            await writer.CompleteAsync();
        }
    }

    private bool TryReadMessage(ref ReadOnlySequence<byte> buffer, out ReadOnlySequence<byte> message)
    {
        if (buffer.Length < 4)
        {
            message = default;
            return false;
        }

        Span<byte> lengthBytes = stackalloc byte[4];
        buffer.Slice(0, 4).CopyTo(lengthBytes);
        int length = BinaryPrimitives.ReadInt32LittleEndian(lengthBytes);

        if (buffer.Length < length)
        {
            message = default;
            return false;
        }

        message = buffer.Slice(0, length);
        buffer = buffer.Slice(length);
        return true;
    }

    private unsafe void ObserveMessage(string tag, string connectionId, ReadOnlySequence<byte> message, CorrelationRingBuffer correlationBuffer)
    {
        var tracker = ArenaPool.Shared.Rent();
        var arena = tracker.Arena;
        OpCode opCode = (OpCode)0;

        try
        {
            var bytes = (byte*)arena.Alloc((nuint)message.Length);
            message.CopyTo(new Span<byte>(bytes, (int)message.Length));

            var header = (MsgHeader*)bytes;
            int requestId = header->RequestID;
            int responseTo = header->ResponseTo;
            opCode = header->OpCode;

            int headerSize = Unsafe.SizeOf<MsgHeader>();
            var bodyPtr = bytes + headerSize;
            int bodyLength = (int)message.Length - headerSize;

            int docCount = 0;
            double? durationMs = null;
            if (tag == "from" && responseTo != 0 && correlationBuffer.TryGetRequest(responseTo, out var reqMetrics))
            {
                durationMs = (double)(System.Diagnostics.Stopwatch.GetTimestamp() - reqMetrics.TimestampStart) / System.Diagnostics.Stopwatch.Frequency * 1000;
            }

            if (tag == "to")
            {
                correlationBuffer.RecordRequest(requestId, new OperationMetrics { TimestampStart = System.Diagnostics.Stopwatch.GetTimestamp(), OpCode = opCode, RequestId = requestId });
            }

            // Determine if parsing is needed
            bool needsParse = _listeners.Any(l => l.NeedsFullDocument) && ShouldParseBody(opCode, bodyLength);
            
            // Pointer for metadata extraction
            byte* metadataPtr = bodyPtr;
            int metadataLen = bodyLength;
            BlittableBsonDocument doc = default;

            if (needsParse)
            {
                // Adjust metadataPtr for BSON document extraction based on OpCode
                if (opCode == OpCode.OP_MSG)
                {
                    // OP_MSG: flagBits (4) + sections + [optional checksum (4)]
                    int flagBits = BinaryPrimitives.ReadInt32LittleEndian(new ReadOnlySpan<byte>(metadataPtr, 4));
                    bool checksumPresent = (flagBits & 1) != 0;
                    
                    if (checksumPresent)
                    {
                        metadataLen -= 4;
                    }

                    // Move past flagBits
                    metadataPtr += 4;
                    metadataLen -= 4;

                    // Iterate through sections to find the first Kind 0 (Body) section.
                    byte* p = metadataPtr;
                    while (p < metadataPtr + metadataLen)
                    {
                        byte kind = *p++;
                        if (kind == 0)
                        {
                            metadataLen -= (int)(p - metadataPtr);
                            metadataPtr = p;
                            break;
                        }
                        else if (kind == 1)
                        {
                            if (p + 4 > metadataPtr + metadataLen) break;
                            int seqSize = BinaryPrimitives.ReadInt32LittleEndian(new ReadOnlySpan<byte>(p, 4));
                            p += seqSize;
                        }
                        else break;
                    }
                }
                else if (opCode == OpCode.OP_QUERY)
                {
                    byte* p = metadataPtr + 4; // skip flags
                    while (p < metadataPtr + metadataLen && *p != 0) p++; 
                    if (p < metadataPtr + metadataLen) p++; // skip null terminator
                    p += 8; // skip skip/return
                    metadataLen -= (int)(p - metadataPtr);
                    metadataPtr = p;
                }
                else if (opCode == OpCode.OP_REPLY)
                {
                    if (metadataLen >= 20)
                    {
                        docCount = BinaryPrimitives.ReadInt32LittleEndian(new ReadOnlySpan<byte>(metadataPtr + 16, 4));
                        metadataPtr += 20;
                        metadataLen -= 20;
                    }
                }
                else
                {
                    // For other opcodes (UPDATE, INSERT, DELETE etc), we don't have a standardized simple document start
                    metadataLen = 0;
                }

                if (metadataLen >= 5)
                {
                    doc = Bson.ArenaBsonReader.ReadInPlace(metadataPtr, metadataLen, arena);
                }
            }
            
            if (opCode == OpCode.OP_MSG && !doc.IsDefault)
            {
                // Extract doc count from cursor batches
                if (doc.TryGetElementOffset("cursor", out var cursorOff))
                {
                    var cursorDoc = doc.GetDocument(cursorOff, arena);
                    if (cursorDoc.TryGetElementOffset("firstBatch", out var fbOff))
                    {
                        docCount = cursorDoc.GetArray(fbOff, arena).Count;
                    }
                    else if (cursorDoc.TryGetElementOffset("nextBatch", out var nbOff))
                    {
                        docCount = cursorDoc.GetArray(nbOff, arena).Count;
                    }
                }
            }

            var observed = new ObservedMessage(tag, connectionId, requestId, responseTo, opCode, doc, bodyPtr, bodyLength, tracker, durationMs, (int)message.Length, docCount);

            foreach (var listener in _listeners)
            {
                observed.AddRef();
                listener.OnMessage(in observed);
            }
            
            observed.Release(); 
        }
        catch (Exception e)
        {
            _logger.LogDebug(e, $"Error observing message (OpCode: {opCode}, Conn: {connectionId})");
            tracker.Release();
        }
    }

    private bool ShouldParseBody(OpCode opCode, int bodyLength)
    {
        // Don't parse high-frequency/low-value opcodes that usually don't contain metadata for us
        if (opCode == OpCode.OP_GET_MORE || opCode == OpCode.OP_KILL_CURSORS) return false;
        if ((int)opCode is 2001 or 2002 or 2006) return false; // Legacy UPDATE, INSERT, DELETE

        // Always parse OP_MSG because it contains the command metadata.
        // Small control messages (hello, ping) are cheap to parse and necessary for filtering.
        // Large data messages (find results) are only skipped if NO listener wants them (handled in ObserveMessage).
        return true;
    }
}
