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

            var fullBodySpan = new ReadOnlySpan<byte>(bodyPtr, bodyLength);

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

            // Pointer for metadata extraction
            byte* metadataPtr = bodyPtr;
            int metadataLen = bodyLength;

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

                // Find the first Kind 0 section (Body)
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
                        int seqSize = BinaryPrimitives.ReadInt32LittleEndian(new ReadOnlySpan<byte>(p, 4));
                        p += seqSize;
                    }
                    else break;
                }
            }
            else if (opCode == OpCode.OP_QUERY)
            {
                // OP_QUERY: flags (4) + fullCollectionName (CString) + numberToSkip (4) + numberToReturn (4) + query (BSON)
                byte* p = metadataPtr + 4; // skip flags
                
                // Extract collection name for better identification
                byte* collStart = p;
                while (p < metadataPtr + metadataLen && *p != 0) p++; 
                if (tag == "to") {
                     // We could store the collection name here if ObservedMessage had a field for it
                }

                if (p < metadataPtr + metadataLen) p++; // skip null terminator
                p += 8; // skip skip/return
                metadataLen -= (int)(p - metadataPtr);
                metadataPtr = p;
            }
            else if (opCode == OpCode.OP_REPLY)
            {
                // OP_REPLY: flags (4) + cursorId (8) + startingFrom (4) + numberReturned (4)
                if (metadataLen >= 20)
                {
                    docCount = BinaryPrimitives.ReadInt32LittleEndian(new ReadOnlySpan<byte>(metadataPtr + 16, 4));
                    metadataPtr += 20;
                    metadataLen -= 20;
                }
            }
            else if (opCode == OpCode.OP_GET_MORE)
            {
                // OP_GET_MORE: ZERO (4) + fullCollectionName (CString) + numberToReturn (4) + cursorId (8)
                // No BSON document to parse here, but we should identify it.
                metadataLen = 0; 
            }
            else if (opCode == OpCode.OP_KILL_CURSORS)
            {
                // OP_KILL_CURSORS: ZERO (4) + numberOfCursors (4) + cursorIds (int64[])
                metadataLen = 0;
            }
            else if (opCode == (OpCode)2001 || opCode == (OpCode)2002 || opCode == (OpCode)2006)
            {
                // Legacy OP_UPDATE (2001), OP_INSERT (2002), OP_DELETE (2006)
                // We'll mark them as identified but currently not deep-parsing BSON headers for these
                // to avoid malformed BSON errors.
                metadataLen = 0; 
            }

            var doc = metadataLen >= 5 ? Bson.ArenaBsonReader.ReadInPlace(metadataPtr, metadataLen, arena) : default;
            
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
            
            observed.Release(); // Release the initial reference from Rent()
        }
        catch (Exception e)
        {
            if (_logger.IsEnabled(LogLevel.Trace))
            {
                _logger.LogTrace(e, "Error observing message");
            }
            tracker.Release();
        }
    }
}
