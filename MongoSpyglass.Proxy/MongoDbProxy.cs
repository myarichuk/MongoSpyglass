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
    private readonly CancellationTokenSource _cts = new();
    private readonly IPEndPoint _mongoDbServer;
    private readonly int _port;
    private readonly ILogger<MongoDbProxy> _logger;
    private readonly IEnumerable<ITrafficListener> _listeners;
    private TcpListener? _listener;

    public MongoDbProxy(IPEndPoint mongoDbServer, int incomingPort, ILogger<MongoDbProxy> logger, IEnumerable<ITrafficListener> listeners)
    {
        _mongoDbServer = mongoDbServer;
        _port = incomingPort;
        _logger = logger;
        _listeners = listeners;

        _retryPolicy = Policy
            .Handle<SocketException>()
            .WaitAndRetryAsync(3,
                attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt)),
                (exception, timeSpan, retryCount, context) =>
                {
                    _logger.LogError(exception, $"Retry {retryCount} after {timeSpan.Seconds} seconds delay due to '{context["Message"]}'");
                });
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _listener = new TcpListener(IPAddress.Any, _port);
        _listener.Start();
        _logger.LogInformation($"Started listening on incoming port {_port}");

        _ = Task.Run(AcceptConnectionsAsync, _cts.Token);

        return Task.CompletedTask;
    }

    private async Task AcceptConnectionsAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(_cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error accepting client connection");
                continue;
            }

            _ = Task.Run(() => ProxyConnection(client), _cts.Token);
        }
    }

    private async Task ProxyConnection(TcpClient client)
    {
        using var clientScope = client;
        var connectionId = Guid.NewGuid().ToString("N");
        _logger.LogDebug($"Accepted connection {connectionId} from {client.Client.RemoteEndPoint}");

        using var server = new TcpClient();
        await server.ConnectAsync(_mongoDbServer.Address, _mongoDbServer.Port);
        _logger.LogDebug($"Connected to MongoDB server {_mongoDbServer}");

        using var correlationBuffer = new CorrelationRingBuffer();
        using var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);

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

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _cts.Cancel();
        _listener?.Stop();
        return Task.CompletedTask;
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
        try
        {
            var bytes = (byte*)arena.Alloc((nuint)message.Length);
            message.CopyTo(new Span<byte>(bytes, (int)message.Length));

            var header = (MsgHeader*)bytes;
            int requestId = header->RequestID;
            int responseTo = header->ResponseTo;
            OpCode opCode = header->OpCode;

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
            _logger.LogDebug(e, $"Error observing message (OpCode: {correlationBuffer.ToString()}, Conn: {connectionId})");
            tracker.Release();
        }
    }

    private bool ShouldParseBody(OpCode opCode, int bodyLength)
    {
        // Don't parse high-frequency/low-value opcodes
        if (opCode == OpCode.OP_GET_MORE || opCode == OpCode.OP_KILL_CURSORS) return false;
        if ((int)opCode is 2001 or 2002 or 2006) return false; // Legacy UPDATE, INSERT, DELETE

        // OP_MSG under ~128 bytes are almost always administrative heartbeats (ping, hello, isMaster)
        if (opCode == OpCode.OP_MSG && bodyLength < 128) return false;

        return true;
    }
}
