using System.Net.Sockets;
using System.Net;
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
// ReSharper disable ComplexConditionExpression

// ReSharper disable ExceptionNotDocumentedOptional
// ReSharper disable ExceptionNotDocumentedOptional
// ReSharper disable ExceptionNotDocumented
// ReSharper disable ExceptionNotDocumented

namespace MongoSpyglass.Proxy;

public class MongoDbProxy : IHostedService
{
    private readonly AsyncRetryPolicy _retryPolicy;

    private readonly CancellationTokenSource _cts = new();

    private readonly IPEndPoint _mongoDbServer;
    private readonly int _port;
    private readonly ILogger<MongoDbProxy> _logger;
    private TcpListener? _listener;

    public MongoDbProxy(IPEndPoint mongoDbServer, int incomingPort, ILogger<MongoDbProxy> logger)
    {
        _mongoDbServer = mongoDbServer;
        _port = incomingPort;
        _logger = logger;

        _retryPolicy = Policy
            .Handle<SocketException>()
            .WaitAndRetryAsync(3, // retry 3 times
                attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt)), // exponential back off
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

    private void ProxyConnection(TcpClient client)
    {
        using var clientScope = client;

        // Accept a client connection
        _logger.LogDebug($"Accepted connection from {client.Client.RemoteEndPoint}");

        // Connect to MongoDB server
        using var server = new TcpClient();
        server.Connect(_mongoDbServer.Address, _mongoDbServer.Port);
        _logger.LogDebug($"Connected to MongoDB server {_mongoDbServer}");

        // Start proxy
        var t1 = Task.Run(async () =>
        {
            using var allocator = new ArenaAllocator();
            while(!_cts.IsCancellationRequested)
            {
                try
                {
                    allocator.Reset();
                    if (!await ForwardTrafficAsync(client, server, "to", allocator))
                    {
                        break;
                    }
                }
                catch (Exception e)
                {
                    _logger.LogError(e, $"Error forwarding traffic from {client.Client.RemoteEndPoint} to {server.Client.RemoteEndPoint}");
                    throw; //for now, TODO: make better error handling
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
                    if (!await ForwardTrafficAsync(server, client, "from", allocator))
                    {
                        break;
                    }
                }
                catch (Exception e)
                {
                    _logger.LogError(e, $"Error forwarding traffic from {client.Client.RemoteEndPoint} to {server.Client.RemoteEndPoint}");
                    throw; //for now, TODO: make better error handling
                }
            }
        }, _cts.Token);

        Task.WaitAll(t1, t2);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _cts.Cancel();
        _listener?.Stop();
        return Task.CompletedTask;
    }


    private async Task<bool> ForwardTrafficAsync(TcpClient source, TcpClient destination, string tag, ArenaAllocator memoryAllocator)
    {        
        var sourceStream = source.GetStream();
        var destStream = destination.GetStream();

        try
        {
            var headerSize = System.Runtime.CompilerServices.Unsafe.SizeOf<MsgHeader>();
            var headerBuffer = System.Buffers.ArrayPool<byte>.Shared.Rent(headerSize);

            try
            {
                try
                {
                    await sourceStream.ReadExactlyAsync(headerBuffer.AsMemory(0, headerSize), _cts.Token).ConfigureAwait(false);
                }
                catch (EndOfStreamException)
                {
                    return false;
                }
                catch (OperationCanceledException)
                {
                    return false;
                }

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
                    catch (EndOfStreamException)
                    {
                        return false;
                    }
                    catch (OperationCanceledException)
                    {
                        return false;
                    }

                    byte[] destBuffer = null;
                    int stuffToWriteLength = 0;

                    unsafe
                    {
                        var buffer = memoryAllocator.Allocate<byte>(bodyLength);
                        bodyBuffer.AsSpan(0, bodyLength).CopyTo(buffer);

                        fixed (byte* pBuffer = &System.Runtime.InteropServices.MemoryMarshal.GetReference<byte>(buffer))
                        {
                            using var memoryStream = new UnmanagedMemoryStream(pBuffer, buffer.Length);
                            switch (msgOpCode)
                            {
                                case OpCode.OP_QUERY:
                                    var opQuery = OpQueryLoader.Instance.Load(memoryStream, memoryAllocator);
                                    var typedOpQuery = MongoSpyglass.Proxy.WireProtocol.Typed.OpQuery.FromRaw(opQuery);
                                    LogOpQuery(tag, msgRequestId, typedOpQuery);
                                    break;
                                case OpCode.OP_MSG:
                                    var opMsg = OpMsgLoader.Instance.Load(memoryStream, memoryAllocator);
                                    LogOpMsg(tag, msgRequestId, opMsg);
                                    break;
                                default:
                                    _logger.LogDebug($"Unsupported opCode: {msgOpCode}, forwarding transparently.");
                                    break;
                            }

                            // We need to pass a MsgHeader to BuildWireMessage, but it's a ref struct, so create it here:
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
                        _logger.LogDebug($"Wrote {stuffToWriteLength} bytes to {destination.Client.RemoteEndPoint}");
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
            throw; //for now, TODO: make better error handling
        }
    }

    private static unsafe Span<byte> BuildWireMessage(ArenaAllocator allocator, MsgHeader header, Span<byte> body)
    {
        int headerSize = System.Runtime.CompilerServices.Unsafe.SizeOf<MsgHeader>();
        var frame = allocator.Allocate<byte>(headerSize + body.Length);

        fixed (byte* pFrame = &System.Runtime.InteropServices.MemoryMarshal.GetReference<byte>(frame))
        {
            var pHeader = (MsgHeader*)pFrame;
            *pHeader = header;
        }

        body.CopyTo(frame[headerSize..]);
        return frame;
    }


    private void LogOpQuery(string tag, int requestId, WireProtocol.Typed.OpQuery opQuery)
    {
        _logger.LogInformation(
            "[{Tag}] OP_QUERY #{RequestId} {Collection} skip={NumberToSkip} return={NumberToReturn} flags={Flags} query={Query}",
            tag,
            requestId,
            opQuery.FullCollectionName,
            opQuery.NumberToSkip,
            opQuery.NumberToReturn,
            opQuery.Flags,
            opQuery.Query.ToJson());
    }

    private void LogOpMsg(string tag, int requestId, OpMsg opMsg)
    {
        if (opMsg.Kind == 0)
        {
            var document = opMsg.DataSection.AsBson();
            _logger.LogInformation(
                "[{Tag}] OP_MSG #{RequestId} kind=0 flags={Flags} body={Body}",
                tag,
                requestId,
                opMsg.Flags,
                document.ToJson());
            return;
        }

        _logger.LogInformation(
            "[{Tag}] OP_MSG #{RequestId} kind={Kind} flags={Flags} payloadBytes={PayloadLength}",
            tag,
            requestId,
            opMsg.Kind,
            opMsg.Flags,
            opMsg.DataSection.Length);
    }
}
