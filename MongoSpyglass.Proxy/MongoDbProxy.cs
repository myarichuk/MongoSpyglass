using System.Net.Sockets;
using System.Net;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Polly.Retry;
using Polly;
using MongoSpyglass.Proxy.WireProtocol;
using MongoSpyglass.Proxy.WireProtocol.Raw;
using Simple.Arena;
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
        Task.WaitAll(
            Task.Factory.StartNew(() =>
            {
                while(!_cts.IsCancellationRequested)
                {
                    try
                    {
                        if (!ForwardTraffic(client, server, "to"))
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
            }, _cts.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default),
            Task.Factory.StartNew(() =>
            {
                while(!_cts.IsCancellationRequested)
                {
                    try
                    {
                        if (!ForwardTraffic(server, client, "from"))
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
            }, _cts.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default)
        );
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _cts.Cancel();
        _listener?.Stop();
        return Task.CompletedTask;
    }

    private unsafe bool ForwardTraffic(TcpClient source, TcpClient destination, string tag)
    {        
        var sourceStream = source.GetStream();
        var destStream = destination.GetStream();

        var now = DateTime.UtcNow;
        
        //while(!sourceStream.DataAvailable)
        //{
        //    if(DateTime.UtcNow - now > TimeSpan.FromMilliseconds(250)) //TODO: make this configurable
        //    { 
        //        return;
        //    }
        //    else
        //    {
        //        Thread.Sleep(50);
        //    }
        //}

        try
        {                
            //TODO: make this configurable
            //for now, 64 mbytes should be enough
            using var memoryAllocator = new GrowableArena();

            var msgHeader = new MsgHeader();
            if (!TryReadHeaderFromStream(sourceStream, ref msgHeader))
            {
                return false;
            }

            if (msgHeader.MessageLength < sizeof(MsgHeader))
            {
                throw new InvalidOperationException($"Invalid message length '{msgHeader.MessageLength}' for opcode '{msgHeader.OpCode}'.");
            }

            var buffer = memoryAllocator.Allocate<byte>(msgHeader.MessageLength - sizeof(MsgHeader));

            try
            {
                sourceStream.ReadExactly(buffer);
            }
            catch (EndOfStreamException)
            {
                return false; // EOF
            }

            using var memoryStream = new UnmanagedMemoryStream((byte*)buffer.ToIntPtr(), buffer.Length);
            switch (msgHeader.OpCode)
            {
                case OpCode.OP_QUERY:
                    var opQuery = OpQueryLoader.Instance.Load(memoryStream, memoryAllocator);
                    var typedOpQuery = MongoSpyglass.Proxy.WireProtocol.Typed.OpQuery.FromRaw(opQuery);
                    LogOpQuery(tag, msgHeader.RequestId, typedOpQuery);
                    break;
                case OpCode.OP_MSG:
                    var opMsg = OpMsgLoader.Instance.Load(memoryStream, memoryAllocator);
                    LogOpMsg(tag, msgHeader.RequestId, opMsg);
                    break;
                default:
                    _logger.LogDebug($"Unsupported opCode: {msgHeader.OpCode}, forwarding transparently.");
                    break;
            }

            var stuffToWrite = BuildWireMessage(memoryAllocator, msgHeader, buffer);

            destStream.Write(stuffToWrite);
            _logger.LogDebug($"Wrote {stuffToWrite.Length} bytes to {destination.Client.RemoteEndPoint}");
            return true;
        }
        catch (Exception e)
        {
            _logger.LogError(e, $"Error forwarding traffic from {source.Client.RemoteEndPoint} to {destination.Client.RemoteEndPoint}");
            throw; //for now, TODO: make better error handling
        }
    }

    private static unsafe Span<byte> BuildWireMessage(GrowableArena allocator, MsgHeader header, Span<byte> body)
    {
        var frame = allocator.Allocate<byte>(sizeof(MsgHeader) + body.Length);

        fixed (byte* pFrame = &MemoryMarshal.GetReference(frame))
        {
            var pHeader = (MsgHeader*)pFrame;
            *pHeader = header;
        }

        body.CopyTo(frame[sizeof(MsgHeader)..]);
        return frame;
    }

    private static unsafe bool TryReadHeaderFromStream(Stream stream, ref MsgHeader header)
    {
        Span<byte> buffer = stackalloc byte[sizeof(MsgHeader)];

        try
        {
            stream.ReadExactly(buffer);
        }
        catch (EndOfStreamException)
        {
            return false; // EOF
        }

        fixed (byte* pBuffer = &MemoryMarshal.GetReference(buffer))
        {
            var pHeader = (MsgHeader*)pBuffer;
            header = *pHeader;
        }

        return true;
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
