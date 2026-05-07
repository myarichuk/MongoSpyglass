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
using MongoSpyglass.Proxy.WireProtocol.Raw.Loaders;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoSpyglass.Proxy.Profiling;
using System.Buffers.Binary;

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
        _logger.LogDebug($"Accepted connection from {client.Client.RemoteEndPoint}");

        using var server = new TcpClient();
        await server.ConnectAsync(_mongoDbServer.Address, _mongoDbServer.Port);
        _logger.LogDebug($"Connected to MongoDB server {_mongoDbServer}");

        using var correlationBuffer = new CorrelationRingBuffer();

        var t1 = Task.Run(async () =>
        {
            using var allocator = new ArenaAllocator();
            while(!_cts.IsCancellationRequested)
            {
                try
                {
                    allocator.Reset();
                    if (!await ForwardTrafficAsync(client, server, "to", allocator, correlationBuffer))
                        break;
                }
                catch (Exception e)
                {
                    _logger.LogError(e, $"Error forwarding traffic to server");
                    throw;
                }
            }
        }, _cts.Token);

        var t2 = Task.Run(async () =>
        {
            using var allocator = new ArenaAllocator();
            while(!_cts.IsCancellationRequested)
            {
                try
                {
                    allocator.Reset();
                    if (!await ForwardTrafficAsync(server, client, "from", allocator, correlationBuffer))
                        break;
                }
                catch (Exception e)
                {
                    _logger.LogError(e, $"Error forwarding traffic from server");
                    throw;
                }
            }
        }, _cts.Token);

        await Task.WhenAll(t1, t2);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _cts.Cancel();
        _listener?.Stop();
        return Task.CompletedTask;
    }

    private async Task<bool> ForwardTrafficAsync(TcpClient source, TcpClient destination, string tag, ArenaAllocator memoryAllocator, CorrelationRingBuffer correlationBuffer)
    {        
        var sourceStream = source.GetStream();
        var destStream = destination.GetStream();

        try
        {
            var headerSize = Unsafe.SizeOf<MsgHeader>();
            var headerBuffer = System.Buffers.ArrayPool<byte>.Shared.Rent(headerSize);

            try
            {
                try
                {
                    await sourceStream.ReadExactlyAsync(headerBuffer.AsMemory(0, headerSize), _cts.Token).ConfigureAwait(false);
                }
                catch (EndOfStreamException) { return false; }
                catch (OperationCanceledException) { return false; }

                int msgLength, msgRequestId, msgResponseTo;
                OpCode msgOpCode;
                unsafe
                {
                    fixed (byte* pHeaderBuffer = headerBuffer)
                    {
                        var pHeader = (MsgHeader*)pHeaderBuffer;
                        msgLength = pHeader->MessageLength;
                        msgRequestId = pHeader->RequestID;
                        msgResponseTo = pHeader->ResponseTo;
                        msgOpCode = pHeader->OpCode;
                    }
                }

                if (msgLength < headerSize)
                {
                    throw new InvalidOperationException($"Invalid message length '{msgLength}' for opcode '{msgOpCode}'.");
                }

                int bodyLength = msgLength - headerSize;
                var bodyBuffer = System.Buffers.ArrayPool<byte>.Shared.Rent(bodyLength);

                try
                {
                    try
                    {
                        await sourceStream.ReadExactlyAsync(bodyBuffer.AsMemory(0, bodyLength), _cts.Token).ConfigureAwait(false);
                    }
                    catch (EndOfStreamException) { return false; }
                    catch (OperationCanceledException) { return false; }

                    byte[]? destBuffer = null;
                    int stuffToWriteLength = 0;

                    unsafe
                    {
                        var buffer = memoryAllocator.Allocate<byte>(bodyLength);
                        bodyBuffer.AsSpan(0, bodyLength).CopyTo(buffer);

                        fixed (byte* pBuffer = &MemoryMarshal.GetReference<byte>(buffer))
                        {
                            using var memoryStream = new UnmanagedMemoryStream(pBuffer, buffer.Length);
                            switch (msgOpCode)
                            {
                                case OpCode.OP_QUERY:
                                    correlationBuffer.RecordRequest(msgRequestId, new OperationMetrics { TimestampStart = System.Diagnostics.Stopwatch.GetTimestamp(), OpCode = msgOpCode, RequestId = msgRequestId });
                                    var opQuery = OpQueryLoader.Instance.Load(memoryStream, memoryAllocator);
                                    var typedOpQuery = MongoSpyglass.Proxy.WireProtocol.Typed.OpQuery.FromRaw(opQuery);
                                    LogOpQuery(tag, msgRequestId, typedOpQuery);
                                    break;
                                case OpCode.OP_MSG:
                                    correlationBuffer.RecordRequest(msgRequestId, new OperationMetrics { TimestampStart = System.Diagnostics.Stopwatch.GetTimestamp(), OpCode = msgOpCode, RequestId = msgRequestId });
                                    var opMsg = OpMsgLoader.Instance.Load(memoryStream, memoryAllocator);
                                    
                                    double? msgDuration = null;
                                    if (msgResponseTo != 0 && correlationBuffer.TryGetRequest(msgResponseTo, out var reqMetrics))
                                    {
                                        msgDuration = (double)(System.Diagnostics.Stopwatch.GetTimestamp() - reqMetrics.TimestampStart) / System.Diagnostics.Stopwatch.Frequency * 1000;
                                    }
                                    
                                    LogOpMsg(tag, msgRequestId, opMsg, memoryAllocator, msgDuration);
                                    break;
                                case OpCode.OP_REPLY:
                                    var opReply = OpReplyLoader.Instance.Load(memoryStream, memoryAllocator);
                                    double? replyDuration = null;
                                    if (correlationBuffer.TryGetRequest(msgResponseTo, out var requestMetrics))
                                    {
                                        replyDuration = (double)(System.Diagnostics.Stopwatch.GetTimestamp() - requestMetrics.TimestampStart) / System.Diagnostics.Stopwatch.Frequency * 1000;
                                        _logger.LogInformation($"[{tag}] Correlated OP_REPLY with request {msgResponseTo}. Latency: {replyDuration:F2}ms");
                                    }
                                    LogOpReply(tag, msgRequestId, msgResponseTo, opReply, replyDuration);
                                    break;
                                default:
                                    _logger.LogDebug($"Unsupported opCode: {msgOpCode}, forwarding transparently.");
                                    break;
                            }

                            var msgHeader = new MsgHeader { MessageLength = msgLength, RequestID = msgRequestId, ResponseTo = msgResponseTo, OpCode = msgOpCode };
                            var stuffToWrite = BuildWireMessage(memoryAllocator, msgHeader, buffer);

                            destBuffer = System.Buffers.ArrayPool<byte>.Shared.Rent(stuffToWrite.Length);
                            stuffToWriteLength = stuffToWrite.Length;
                            stuffToWrite.CopyTo(destBuffer);
                        }
                    }

                    try
                    {
                        if (destBuffer != null)
                        {
                            await destStream.WriteAsync(destBuffer.AsMemory(0, stuffToWriteLength), _cts.Token).ConfigureAwait(false);
                        }
                    }
                    finally
                    {
                        if (destBuffer != null)
                        {
                            System.Buffers.ArrayPool<byte>.Shared.Return(destBuffer);
                        }
                    }

                    return true;
                }
                finally
                {
                    System.Buffers.ArrayPool<byte>.Shared.Return(bodyBuffer);
                }
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(headerBuffer);
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e, $"Error forwarding traffic from {source.Client.RemoteEndPoint} to {destination.Client.RemoteEndPoint}");
            throw;
        }
    }

    private static unsafe Span<byte> BuildWireMessage(ArenaAllocator allocator, MsgHeader header, Span<byte> body)
    {
        int headerSize = Unsafe.SizeOf<MsgHeader>();
        var frame = allocator.Allocate<byte>(headerSize + body.Length);

        fixed (byte* pFrame = &MemoryMarshal.GetReference<byte>(frame))
        {
            var pHeader = (MsgHeader*)pFrame;
            *pHeader = header;
        }

        body.CopyTo(frame[headerSize..]);
        return frame;
    }

    private void LogOpQuery(string tag, int requestId, WireProtocol.Typed.OpQuery opQuery)
    {
        var payload = opQuery.Query.ToJson();
        _logger.LogInformation(
            "[{Tag}] OP_QUERY #{RequestId} {Collection} skip={NumberToSkip} return={NumberToReturn} query={Query}",
            tag,
            requestId,
            opQuery.FullCollectionName,
            opQuery.NumberToSkip,
            opQuery.NumberToReturn,
            payload);

        foreach (var listener in _listeners)
        {
            listener.OnMessage(tag, requestId, "OP_QUERY", "find", opQuery.FullCollectionName, payload);
        }
    }

    private void LogOpMsg(string tag, int requestId, OpMsg opMsg, ArenaAllocator allocator, double? durationMs = null)
    {
        string cmdName = "unknown";
        string collection = "N/A";
        string payload = "{}";

        var sections = opMsg.Sections.AsSpan();
        int offset = 0;

        while (offset < sections.Length)
        {
            byte kind = sections[offset++];
            if (kind == 0)
            {
                var bsonData = sections[offset..];
                var reader = new ArenaBsonReader(bsonData, allocator);
                
                // Only take the first section's payload for logging/UI for now
                if (payload == "{}")
                {
                    payload = bsonData.Length > 0 ? new BsonDocument(BsonSerializer.Deserialize<BsonDocument>(bsonData.ToArray())).ToJson() : "{}";
                }

                if (reader.Elements.Length > 0)
                {
                    var firstElement = reader.Elements[0];
                    if (cmdName == "unknown")
                    {
                        cmdName = reader.GetElementName(firstElement);
                    }

                    if (collection == "N/A")
                    {
                        if (firstElement.Type == WireProtocol.BsonType.String)
                        {
                            collection = reader.GetStringValue(firstElement);
                        }
                        else if (reader.TryFindElement("collection", out var colElement) && colElement.Type == WireProtocol.BsonType.String)
                        {
                            collection = reader.GetStringValue(colElement);
                        }
                    }
                }
                
                // Advance offset by BSON length
                offset += BinaryPrimitives.ReadInt32LittleEndian(bsonData.Slice(0, 4));
            }
            else if (kind == 1)
            {
                int size = BinaryPrimitives.ReadInt32LittleEndian(sections.Slice(offset, 4));
                offset += size;
            }
            else
            {
                break; // Unknown kind or corrupted
            }
        }

        _logger.LogInformation(
            "[{Tag}] OP_MSG #{RequestId} command={Command} collection={Collection} flags={Flags} duration={Duration}ms",
            tag,
            requestId,
            cmdName,
            collection,
            opMsg.Flags,
            durationMs?.ToString("F2") ?? "-");

        foreach (var listener in _listeners)
        {
            listener.OnMessage(tag, requestId, "OP_MSG", cmdName, collection, payload, durationMs);
        }
    }

    private void LogOpReply(string tag, int requestId, int responseTo, OpReply opReply, double? durationMs = null)
    {
        _logger.LogInformation(
            "[{Tag}] OP_REPLY #{RequestId} responseTo={ResponseTo} flags={Flags} cursor={CursorId} count={NumberReturned} duration={Duration}ms",
            tag,
            requestId,
            responseTo,
            opReply.ResponseFlags,
            opReply.CursorID,
            opReply.NumberReturned,
            durationMs?.ToString("F2") ?? "-");

        foreach (var listener in _listeners)
        {
            listener.OnMessage(tag, requestId, "OP_REPLY", "reply", "N/A", $"{{ \"cursorId\": {opReply.CursorID}, \"count\": {opReply.NumberReturned} }}", durationMs);
        }
    }
}
