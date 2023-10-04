//using System.Net.Sockets;
//using System.Net;
//using System.Runtime.InteropServices;
//using Microsoft.Extensions.Hosting;
//using Microsoft.Extensions.Logging;
//using Polly.Retry;
//using Polly;
//using MongoDB.Bson.IO;
//using MongoDB.Bson.Serialization;
//using MongoDB.Bson;
//using System.Text;
//using MongoSpyglass.Proxy.WireProtocol;
//using MongoSpyglass.Proxy.WireProtocol.Raw;
//using Simple.Arena;
//// ReSharper disable ComplexConditionExpression

//// ReSharper disable ExceptionNotDocumentedOptional
//// ReSharper disable ExceptionNotDocumentedOptional
//// ReSharper disable ExceptionNotDocumented
//// ReSharper disable ExceptionNotDocumented

//namespace MongoSpyglass.Proxy;

//public class MongoDbProxy : IHostedService
//{
//    private readonly AsyncRetryPolicy _retryPolicy;

//    private readonly CancellationTokenSource _cts = new();
//    private readonly MessageParserRegistry _messageParserRegistry = new();

//    private readonly IPEndPoint _mongoDbServer;
//    private readonly int _port;
//    private readonly ILogger<MongoDbProxy> _logger;

//    public MongoDbProxy(IPEndPoint mongoDbServer, int incomingPort, ILogger<MongoDbProxy> logger)
//    {
//        _mongoDbServer = mongoDbServer;
//        _port = incomingPort;
//        _logger = logger;

//        _retryPolicy = Policy
//            .Handle<SocketException>()
//            .WaitAndRetryAsync(3, // retry 3 times
//                attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt)), // exponential back off
//                (exception, timeSpan, retryCount, context) =>
//                {
//                    _logger.LogError(exception, $"Retry {retryCount} after {timeSpan.Seconds} seconds delay due to '{context["Message"]}'");
//                });
//    }

//    public async Task StartAsync(CancellationToken cancellationToken)
//    {
//        var listener = new TcpListener(IPAddress.Any, _port);
        
//        listener.Start();
//        _logger.LogInformation($"Started listening on incoming port {_port}");

//        while (!_cts.IsCancellationRequested)
//        {
//            // Accept a client connection
//            var client = await listener.AcceptTcpClientAsync(_cts.Token);            
//            _logger.LogDebug($"Accepted connection from {client.Client.RemoteEndPoint}");

//            // Connect to MongoDB server
//            var server = new TcpClient();
//            await server.ConnectAsync(_mongoDbServer.Address, _mongoDbServer.Port, _cts.Token);
//            _logger.LogDebug($"Connected to MongoDB server {_mongoDbServer}");

//            // Start forwarding
//            var clientToServerForwarding = 
//                Task.Run(() => ForwardTraffic(client, server, "to"), _cts.Token);
//            var serverToClientForwarding = 
//                Task.Run(() => ForwardTraffic(server, client, "from"), _cts.Token);

//            await Task.WhenAll(clientToServerForwarding, serverToClientForwarding);
//        }
//    }

//    public Task StopAsync(CancellationToken cancellationToken)
//    {
//        _cts.Cancel();
//        return Task.CompletedTask;
//    }

//    private unsafe void ForwardTraffic(TcpClient source, TcpClient destination, string tag)
//    {
//        var sourceStream = source.GetStream();
//        var destStream = destination.GetStream();


//        while (!_cts.IsCancellationRequested)
//        {
//            try
//            {
//                //TODO: make this configurable
//                //for now, 64 mbytes should be enough
//                using var memoryAllocator = new Arena(1024 * 1024 * 64);

//                var msgHeader = new MsgHeader();
//                if (!TryReadHeaderFromStream(sourceStream, ref msgHeader))
//                {
//                    continue;
//                }

//                Span<byte> stuffToWrite = default;
//                switch (msgHeader.OpCode)
//                {
//                    case OpCode.OP_QUERY:
//                        if (!_messageParserRegistry.TryGet<OpQuery>(out var opQueryParser))
//                        {
//                            throw new InvalidOperationException("This is not supposed to happen and is likely a bug.");
//                        }

//                        if (!opQueryParser.TryParse(ref msgHeader, sourceStream, memoryAllocator, out var opQuery))
//                        {
//                            throw new InvalidOperationException("Failed to parse OP_MSG");
//                        }

//                        _logger.LogDebug($"Parsed OP_QUERY opCode, Query = {opQuery.Query}");
//                        stuffToWrite = opQueryParser.GetRawBytes(opQuery, memoryAllocator);
//                        break;
//                    case OpCode.OP_MSG:
//                        if (!_messageParserRegistry.TryGet<OpMsg>(out var opMsgParser))
//                        {
//                            throw new InvalidOperationException("This is not supposed to happen and is likely a bug.");
//                        }

//                        if (!opMsgParser.TryParse(ref msgHeader, sourceStream, memoryAllocator, out var opMsg))
//                        {
//                            throw new InvalidOperationException("Failed to parse OP_MSG");
//                        }
//                        stuffToWrite = opMsgParser.GetRawBytes(opMsg, memoryAllocator);
//                        break;
//                    default:
//                        _logger.LogWarning($"Unsupported opCode: {msgHeader.OpCode}");
//                        throw new NotSupportedException("");
//                }

//                destStream.Write(stuffToWrite);
//                _logger.LogDebug($"Wrote {stuffToWrite.Length} bytes to {destination.Client.RemoteEndPoint}");
//            }
//            catch (Exception e)
//            {
//                _logger.LogError(e, $"Error forwarding traffic from {source.Client.RemoteEndPoint} to {destination.Client.RemoteEndPoint}");
//            }
//        }
//    }

//    private static unsafe bool TryReadHeaderFromStream(Stream stream, ref MsgHeader header)
//    {
//        Span<byte> buffer = stackalloc byte[sizeof(MsgHeader)];  
            
//        var readBytes = stream.Read(buffer);

//        if (readBytes != sizeof(MsgHeader))
//        {
//            return false;
//        }

//        fixed (byte* pBuffer = &MemoryMarshal.GetReference(buffer))
//        {
//            var pHeader = (MsgHeader*)pBuffer;
//            header = *pHeader;
//        }

//        return true;
//    }
//}
