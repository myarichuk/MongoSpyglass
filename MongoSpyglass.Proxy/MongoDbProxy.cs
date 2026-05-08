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
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoSpyglass.Proxy.Profiling;
using System.Buffers.Binary;
using System.Threading.Channels;
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
    private readonly Channel<ObservedMessage> _observedChannel;
    private TcpListener? _listener;

    public MongoDbProxy(IPEndPoint mongoDbServer, int incomingPort, ILogger<MongoDbProxy> logger, IEnumerable<ITrafficListener> listeners)
    {
        _mongoDbServer = mongoDbServer;
        _port = incomingPort;
        _logger = logger;
        _listeners = listeners;

        var channelOptions = new BoundedChannelOptions(1024)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            AllowSynchronousContinuations = false
        };
        _observedChannel = Channel.CreateBounded<ObservedMessage>(channelOptions);

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
        _ = Task.Run(ConsumeObservedMessagesAsync, _cts.Token);

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

        var clientStream = client.GetStream();
        var serverStream = server.GetStream();

        var clientReader = PipeReader.Create(clientStream);
        var clientWriter = PipeWriter.Create(clientStream);
        var serverReader = PipeReader.Create(serverStream);
        var serverWriter = PipeWriter.Create(serverStream);

        var t1 = ProcessPipeAsync(clientReader, serverWriter, "to", correlationBuffer);
        var t2 = ProcessPipeAsync(serverReader, clientWriter, "from", correlationBuffer);

        await Task.WhenAll(t1, t2);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _cts.Cancel();
        _listener?.Stop();
        return Task.CompletedTask;
    }

    private async Task ProcessPipeAsync(PipeReader reader, PipeWriter writer, string tag, CorrelationRingBuffer correlationBuffer)
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                ReadResult result = await reader.ReadAsync(_cts.Token);
                ReadOnlySequence<byte> buffer = result.Buffer;

                while (TryReadMessage(ref buffer, out var message))
                {
                    // 1. Immediate Forward
                    foreach (var segment in message)
                    {
                        await writer.WriteAsync(segment, _cts.Token);
                    }
                    await writer.FlushAsync(_cts.Token);

                    // 2. Async Observability
                    ObserveMessage(tag, message, correlationBuffer);
                }

                reader.AdvanceTo(buffer.Start, buffer.End);

                if (result.IsCompleted) break;
            }
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

    private unsafe void ObserveMessage(string tag, ReadOnlySequence<byte> message, CorrelationRingBuffer correlationBuffer)
    {
        var arena = ArenaPool.Shared.Rent();
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

            double? durationMs = null;
            if (opCode == OpCode.OP_MSG || opCode == OpCode.OP_QUERY)
            {
                correlationBuffer.RecordRequest(requestId, new OperationMetrics { TimestampStart = System.Diagnostics.Stopwatch.GetTimestamp(), OpCode = opCode, RequestId = requestId });
            }
            else if (responseTo != 0 && correlationBuffer.TryGetRequest(responseTo, out var reqMetrics))
            {
                durationMs = (double)(System.Diagnostics.Stopwatch.GetTimestamp() - reqMetrics.TimestampStart) / System.Diagnostics.Stopwatch.Frequency * 1000;
            }

            // Simple scan for profiling
            var doc = Bson.ArenaBsonReader.ReadInPlace(bodyPtr, bodyLength, arena);
            var observed = new ObservedMessage(tag, requestId, opCode, doc, arena, durationMs);

            if (!_observedChannel.Writer.TryWrite(observed))
            {
                observed.Dispose(); // Drop if full
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error observing message");
            ArenaPool.Shared.Return(arena);
        }
    }

    private async Task ConsumeObservedMessagesAsync()
    {
        try
        {
            await foreach (var msg in _observedChannel.Reader.ReadAllAsync(_cts.Token))
            {
                using (msg)
                {
                    try
                    {
                        ProcessObservedMessage(msg);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error processing observed message");
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private void ProcessObservedMessage(ObservedMessage msg)
    {
        string cmdName = "unknown";
        string collection = "N/A";
        string payload = "{}";

        switch (msg.OpCode)
        {
            case OpCode.OP_QUERY:
                if (msg.Document.TryGetElementOffset("query", out var queryOffset))
                {
                    // For now just show "BSON" or similar to avoid heavy JSON conversion in this task
                    payload = "{ \"query\": \"...\" }"; 
                }
                if (msg.Document.TryGetElementOffset("collection", out var colOffset))
                {
                    collection = msg.Document.GetString(colOffset);
                }
                cmdName = "find";
                break;
            case OpCode.OP_MSG:
                // Scan for command name and collection
                // In OP_MSG, the first element of the first section is usually the command name
                if (msg.Document.KeysEnumerable.Any())
                {
                    var firstKey = msg.Document.KeysEnumerable.First();
                    cmdName = firstKey.ToString();
                    
                    if (msg.Document.TryGetElementOffset("collection", out var collOff))
                    {
                        collection = msg.Document.GetString(collOff);
                    }
                    else if (msg.Document.TryGetElementOffset("$db", out var dbOff))
                    {
                        collection = msg.Document.GetString(dbOff);
                    }
                }
                break;
        }

        foreach (var listener in _listeners)
        {
            listener.OnMessage(msg.Tag, msg.RequestId, msg.OpCode.ToString(), cmdName, collection, payload, msg.DurationMs);
        }
    }
}
