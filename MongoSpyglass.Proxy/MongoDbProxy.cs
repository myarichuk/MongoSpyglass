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

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _listener = new TcpListener(IPAddress.Any, _port);
        _listener.Start();
        _logger.LogInformation($"Started listening on incoming port {_port}");

        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
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

                    byte[] destBuffer = null;
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
                                    LogOpMsg(tag, msgRequestId, opMsg, memoryAllocator);
                                    break;
                                case OpCode.OP_REPLY:
                                    var opReply = OpReplyLoader.Instance.Load(memoryStream, memoryAllocator);
                                    if (correlationBuffer.TryGetRequest(msgResponseTo, out var requestMetrics))
                                    {
                                        var duration = (double)(System.Diagnostics.Stopwatch.GetTimestamp() - requestMetrics.TimestampStart) / System.Diagnostics.Stopwatch.Frequency * 1000;
                                        _logger.LogInformation($"[{tag}] Correlated OP_REPLY with request {msgResponseTo}. Latency: {duration:F2}ms");
                                    }
                                    LogOpReply(tag, msgRequestId, msgResponseTo, opReply);
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
                        await destStream.WriteAsync(destBuffer.AsMemory(0, stuffToWriteLength), _cts.Token).ConfigureAwait(false);
                    }
                    finally
                    {
                        System.Buffers.ArrayPool<byte>.Shared.Return(destBuffer);
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

    private void LogOpMsg(string tag, int requestId, OpMsg opMsg, ArenaAllocator allocator)
    {
        string cmdName = "unknown";
        string collection = "N/A";
        string payload = "{}";

        if (opMsg.Kind == 0)
        {
            // Use ArenaBsonReader for zero-allocation inspection
            var reader = new ArenaBsonReader(opMsg.DataSection, allocator);
            payload = opMsg.DataSection.Length > 0 ? new BsonDocument(BsonSerializer.Deserialize<BsonDocument>(opMsg.DataSection.ToArray())).ToJson() : "{}";
            
            // The first element in OP_MSG is usually the command name
            if (reader.Elements.Length > 0)
            {
                var firstElement = reader.Elements[0];
                cmdName = reader.GetStringValue(firstElement);
            }

            if (reader.TryFindElement("collection", out var colElement))
            {
                collection = reader.GetStringValue(colElement);
            }
            else if (reader.TryFindElement(cmdName, out var cmdElement) && cmdElement.Type == WireProtocol.BsonType.String)
            {
                collection = reader.GetStringValue(cmdElement);
            }

            _logger.LogInformation(
                "[{Tag}] OP_MSG #{RequestId} command={Command} collection={Collection} flags={Flags}",
                tag,
                requestId,
                cmdName,
                collection,
                opMsg.Flags);
        }
        else
        {
            _logger.LogInformation(
                "[{Tag}] OP_MSG #{RequestId} kind={Kind} flags={Flags} payloadBytes={PayloadLength}",
                tag,
                requestId,
                opMsg.Kind,
                opMsg.Flags,
                opMsg.DataSection.Length);
        }

        foreach (var listener in _listeners)
        {
            listener.OnMessage(tag, requestId, "OP_MSG", cmdName, collection, payload);
        }
    }

    private void LogOpReply(string tag, int requestId, int responseTo, OpReply opReply)
    {
        _logger.LogInformation(
            "[{Tag}] OP_REPLY #{RequestId} responseTo={ResponseTo} flags={Flags} cursor={CursorId} count={NumberReturned}",
            tag,
            requestId,
            responseTo,
            opReply.ResponseFlags,
            opReply.CursorID,
            opReply.NumberReturned);

        foreach (var listener in _listeners)
        {
            listener.OnMessage(tag, requestId, "OP_REPLY", "reply", "N/A", $"{{ \"cursorId\": {opReply.CursorID}, \"count\": {opReply.NumberReturned} }}");
        }
    }
}
