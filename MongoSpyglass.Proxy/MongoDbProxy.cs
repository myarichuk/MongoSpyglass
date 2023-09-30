using System.Net.Sockets;
using System.Net;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Polly.Retry;
using Polly;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;
using MongoDB.Bson;
using System.Text;

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
            var client = await listener.AcceptTcpClientAsync(_cts.Token);            
            _logger.LogDebug($"Accepted connection from {client.Client.RemoteEndPoint}");

            // Connect to MongoDB server
            var server = new TcpClient();
            await server.ConnectAsync(_mongoDbServer.Address, _mongoDbServer.Port, _cts.Token);
            _logger.LogDebug($"Connected to MongoDB server {_mongoDbServer}");

            // Start forwarding
            _ = ForwardTraffic(client, server);
            _ = ForwardTraffic(server, client);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _cts.Cancel();
        return Task.CompletedTask;
    }

    private async Task ForwardTraffic(TcpClient source, TcpClient destination)
    {
        var sourceStream = source.GetStream();
        var destStream = destination.GetStream();

        var buffer = new byte[4096];

        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var bytesRead = await sourceStream.ReadAsync(buffer, 0, buffer.Length, _cts.Token);
                if (bytesRead == 0)
                {
                    _logger.LogDebug($"Read 0 bytes from {source.Client.RemoteEndPoint}, closing connection");
                    break;
                }

                HandleMongoDbMessages(buffer, bytesRead);

                await _retryPolicy.ExecuteAsync(
                    () => destStream.WriteAsync(buffer, 0, bytesRead, _cts.Token))
                        .ConfigureAwait(false);

                _logger.LogDebug($"Wrote {bytesRead} bytes to {destination.Client.RemoteEndPoint}");
            }
            catch (Exception e)
            {
                _logger.LogError(e, $"Error forwarding traffic from {source.Client.RemoteEndPoint} to {destination.Client.RemoteEndPoint}");
            }
        }
    }

    private void HandleMongoDbMessages(byte[] buffer, int bytesRead)
    {
        using MemoryStream bufferStream = new();
        bufferStream.Write(buffer, 0, bytesRead);

        while (bufferStream.Length >= 4)
        {
            bufferStream.Position = 0;
            byte[] lengthBytes = new byte[4];
            bufferStream.Read(lengthBytes, 0, 4);
            int messageLength = BitConverter.ToInt32(lengthBytes);

            if (bufferStream.Length >= messageLength)
            {
                byte[] messageBytes = new byte[messageLength];
                bufferStream.Position = 0;
                bufferStream.Read(messageBytes, 0, messageLength);

                int requestId = BitConverter.ToInt32(messageBytes[4..8]);
                int responseTo = BitConverter.ToInt32(messageBytes[8..12]);
                int opCode = BitConverter.ToInt32(messageBytes[12..16]);

                _logger.LogDebug($"Message Length: {messageLength}, Request ID: {requestId}, Response To: {responseTo}, OpCode: {opCode}");

                var messageStream = new MemoryStream(messageBytes);
                ProcessMongoDbMessage(messageStream, opCode);

                var remainingBytes = new byte[bufferStream.Length - messageLength];
                bufferStream.Position = messageLength;
                bufferStream.Read(remainingBytes, 0, (int)(bufferStream.Length - messageLength));
                bufferStream.SetLength(0);
                bufferStream.Write(remainingBytes, 0, remainingBytes.Length);
            }
            else
            {
                break;
            }
        }
    }

    private void ProcessMongoDbMessage(MemoryStream messageStream, int opCode)
    {
        if (opCode == 2004) // OP_QUERY
        {
            DecodeOpQuery(messageStream);
        }
        else if (opCode == 2013) // OP_MSG
        {
            DecodeOpMsg(messageStream);
        }
        else
        {
            _logger.LogWarning($"Unknown OpCode: {opCode}");
        }
    }

    private void DecodeOpQuery(MemoryStream messageStream)
    {
        messageStream.Seek(16, SeekOrigin.Begin);
        var flags = ReadInt32(messageStream);
        var fullCollectionName = ReadCString(messageStream);
        var numberToSkip = ReadInt32(messageStream);
        var numberToReturn = ReadInt32(messageStream);

        var document = DeserializeBsonFromStream(messageStream);
        _logger.LogInformation($"Decoded OP_QUERY: {document.ToJson()}");
    }

    private void DecodeOpMsg(MemoryStream messageStream)
    {
        messageStream.Seek(16, SeekOrigin.Begin); // Skip past the message header

        while (messageStream.Position < messageStream.Length)
        {
            byte kindByte = (byte)messageStream.ReadByte();
        
            _logger.LogDebug($"Kind byte: {kindByte}");

            if (kindByte == 0) // kind 0 represents a single BSON document
            {
                try
                {
                    var document = DeserializeBsonFromStream(messageStream);
                    _logger.LogInformation($"Decoded OP_MSG section with kind 0: {document.ToJson()}");
                }
                catch (Exception ex)
                {
                    _logger.LogError($"An error occurred while deserializing BSON: {ex.Message}");
                }
            }
            else
            {
                _logger.LogWarning($"Unsupported OP_MSG section kind: {kindByte}");
            }
        }
    }

    private static BsonDocument DeserializeBsonFromStream(Stream stream)
    {
        var bsonReader = new BsonBinaryReader(stream);
        var document = BsonSerializer.Deserialize<BsonDocument>(bsonReader);
        return document;
    }

    private static int ReadInt32(Stream stream)
    {
        byte[] bytes = new byte[4];
        stream.Read(bytes, 0, 4);
        return BitConverter.ToInt32(bytes);
    }

    private static string ReadCString(Stream stream)
    {
        List<byte> stringBytes = new List<byte>();
        int b;
        while ((b = stream.ReadByte()) > 0)
        {
            stringBytes.Add((byte)b);
        }
        return Encoding.UTF8.GetString(stringBytes.ToArray());
    }

}
