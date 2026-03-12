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
        var listener = new TcpListener(IPAddress.Any, _port);

        listener.Start();
        _logger.LogInformation($"Started listening on incoming port {_port}");

        while (!_cts.IsCancellationRequested)
        {
            // Accept a client connection
            using var client = await listener.AcceptTcpClientAsync(_cts.Token);
            _logger.LogDebug($"Accepted connection from {client.Client.RemoteEndPoint}");

            // Connect to MongoDB server
            using var server = new TcpClient();
            await server.ConnectAsync(_mongoDbServer.Address, _mongoDbServer.Port, _cts.Token);
            _logger.LogDebug($"Connected to MongoDB server {_mongoDbServer}");

            // Start proxy            
            Task.WaitAll(
                Task.Factory.StartNew(() =>
                {
                    while(!_cts.IsCancellationRequested)
                    {
                        try
                        {
                            ForwardTraffic(client, server, "to");
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
                            ForwardTraffic(server, client, "from");
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
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _cts.Cancel();
        return Task.CompletedTask;
    }

    private unsafe void ForwardTraffic(TcpClient source, TcpClient destination, string tag)
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
                return;//throw new InvalidOperationException("Unable to read message header");
            }

            var buffer = memoryAllocator.Allocate<byte>(msgHeader.MessageLength - sizeof(MsgHeader));

            try
            {
                sourceStream.ReadExactly(buffer);
            }
            catch (EndOfStreamException)
            {
                return; // EOF
            }

            using var memoryStream = new UnmanagedMemoryStream((byte*)buffer.ToIntPtr(), buffer.Length);
            Span<byte> stuffToWrite = default;
            switch (msgHeader.OpCode)
            {
                case OpCode.OP_QUERY:
                    var opQuery = OpQueryLoader.Instance.Load(memoryStream, memoryAllocator);
                    var foo = MongoSpyglass.Proxy.WireProtocol.Typed.OpQuery.FromRaw(opQuery);
                    stuffToWrite = opQuery.ToBytes(memoryAllocator);
                    break;
                case OpCode.OP_MSG:
                    var opMsg = OpMsgLoader.Instance.Load(memoryStream, memoryAllocator);
                    stuffToWrite = opMsg.ToBytes(memoryAllocator);
                    break;
                default:
                    _logger.LogDebug($"Unsupported opCode: {msgHeader.OpCode}, forwarding transparently.");
                    // Reconstruct the message: header + body
                    stuffToWrite = memoryAllocator.Allocate<byte>(sizeof(MsgHeader) + buffer.Length);
                    fixed (byte* pStuff = &MemoryMarshal.GetReference(stuffToWrite))
                    {
                        var pHeader = (MsgHeader*)pStuff;
                        *pHeader = msgHeader;
                    }
                    buffer.CopyTo(stuffToWrite.Slice(sizeof(MsgHeader)));
                    break;
            }

            destStream.Write(stuffToWrite);
            _logger.LogDebug($"Wrote {stuffToWrite.Length} bytes to {destination.Client.RemoteEndPoint}");
        }
        catch (Exception e)
        {
            _logger.LogError(e, $"Error forwarding traffic from {source.Client.RemoteEndPoint} to {destination.Client.RemoteEndPoint}");
            throw; //for now, TODO: make better error handling
        }
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
}
