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
using System.IO.Compression;

namespace MongoSpyglass.Proxy;

public class MongoDbProxy : IHostedService
{
    private readonly AsyncRetryPolicy _retryPolicy;
    private readonly IProxySettingsProvider _settingsProvider;
    private readonly ILogger<MongoDbProxy> _logger;
    private readonly IEnumerable<ITrafficListener> _listeners;
    private readonly object _lifecycleLock = new();
    
    private IPEndPoint _mongoDbServer = new(IPAddress.Loopback, 27017);
    private int _port = 27018;
    private TcpListener? _listener;
    private CancellationTokenSource? _runCts;
    private Task? _acceptTask;

    private bool _compressedTrafficWarningLogged = false;

    public MongoDbProxy(IProxySettingsProvider settingsProvider, ILogger<MongoDbProxy> logger, IEnumerable<ITrafficListener> listeners)
    {
        _settingsProvider = settingsProvider;
        _logger = logger;
        _listeners = listeners;

        _retryPolicy = Policy
            .Handle<SocketException>()
            .WaitAndRetryAsync(3,
                attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt)),
                (exception, timeSpan, retryCount, context) =>
                {
                    _logger.LogError(exception, $"Retry {retryCount} after {timeSpan.Seconds} seconds delay");
                });

        _settingsProvider.OnSettingsChanged += HandleSettingsChanged;
    }

    private void HandleSettingsChanged()
    {
        _logger.LogInformation("Proxy settings changed, restarting listener...");
        _ = RestartProxyAsync();
    }

    private async Task RestartProxyAsync()
    {
        await StopProxyInternalAsync();
        StartProxyInternal();
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        StartProxyInternal();
        return Task.CompletedTask;
    }

    private void StartProxyInternal()
    {
        lock (_lifecycleLock)
        {
            if (_runCts != null) return; // Already running

            var settings = _settingsProvider.GetCurrentSettings();
            _mongoDbServer = settings.TargetServer;
            _port = settings.IncomingPort;
            var bindAddress = _settingsProvider.GetBindAddress();

            _runCts = new CancellationTokenSource();
            _listener = new TcpListener(bindAddress, _port);
            
            try 
            {
                _listener.Start();
                _logger.LogInformation($"Proxy started: listening on port {_port}, forwarding to {_mongoDbServer}");
                _acceptTask = Task.Run(() => AcceptConnectionsAsync(_runCts.Token), _runCts.Token);
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, $"Failed to start proxy listener on port {_port}");
                _runCts.Cancel();
                _runCts = null;
            }
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await StopProxyInternalAsync();
    }

    private async Task StopProxyInternalAsync()
    {
        CancellationTokenSource? cts;
        Task? acceptTask;

        lock (_lifecycleLock)
        {
            _logger.LogInformation("Stopping proxy listener...");
            _listener?.Stop();
            _listener = null;
            cts = _runCts;
            acceptTask = _acceptTask;
            _runCts = null;
            _acceptTask = null;
        }

        if (cts != null)
        {
            cts.Cancel();
            if (acceptTask != null)
            {
                try { await acceptTask; } catch { }
            }
            cts.Dispose();
        }
    }

    private async Task AcceptConnectionsAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception e)
            {
                if (!ct.IsCancellationRequested)
                {
                    _logger.LogError(e, "Error accepting client connection");
                }
                continue;
            }

            _ = Task.Run(() => ProxyConnection(client, ct), ct);
        }
    }

    private async Task ProxyConnection(TcpClient client, CancellationToken ct)
    {
        using var clientScope = client;
        var connectionId = Guid.NewGuid().ToString("N");
        _logger.LogDebug($"Accepted connection {connectionId} from {client.Client.RemoteEndPoint}");

        using var server = new TcpClient();
        try 
        {
            await server.ConnectAsync(_mongoDbServer.Address, _mongoDbServer.Port, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Failed to connect {connectionId} to target MongoDB server {_mongoDbServer}");
            return;
        }

        _logger.LogDebug($"Connected {connectionId} to MongoDB server {_mongoDbServer}");

        using var correlationBuffer = new CorrelationRingBuffer();
        using var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

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

        // Validate frame length: must be at least 16 bytes (MsgHeader minimum) and at most 48 MB (MongoDB's max)
        const int MinMessageSize = 16; // sizeof(MsgHeader)
        const int MaxMessageSize = 48 * 1024 * 1024; // 48 MB
        if (length < MinMessageSize || length > MaxMessageSize)
        {
            _logger.LogWarning($"Invalid frame length {length} (expected 16..{MaxMessageSize})");
            message = default;
            return false;
        }

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
        OpCode opCode = (OpCode)0;

        try
        {
            var bytes = (byte*)arena.Alloc((nuint)message.Length);
            message.CopyTo(new Span<byte>(bytes, (int)message.Length));

            // Defense-in-depth: ensure message is at least MsgHeader-sized before dereferencing
            int headerSize = Unsafe.SizeOf<MsgHeader>();
            if (message.Length < headerSize)
            {
                _logger.LogWarning($"Message too small ({message.Length} bytes) for MsgHeader ({headerSize} bytes)");
                tracker.Release();
                return;
            }

            var header = (MsgHeader*)bytes;
            int requestId = header->RequestID;
            int responseTo = header->ResponseTo;
            opCode = header->OpCode;

            var bodyPtr = bytes + headerSize;
            int bodyLength = (int)message.Length - headerSize;
            int totalMessageLength = (int)message.Length;

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

            // Handle OP_COMPRESSED decompression
            byte* decompressedBodyPtr = bodyPtr;
            int decompressedBodyLen = bodyLength;
            if (opCode == OpCode.OP_COMPRESSED && TryDecompressMessage(bodyPtr, bodyLength, arena, out byte* decompPtr, out int decompLen, out OpCode originalOpCode))
            {
                bodyPtr = decompPtr;
                bodyLength = decompLen;
                decompressedBodyPtr = decompPtr;
                decompressedBodyLen = decompLen;
                opCode = originalOpCode;
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
                            // Validate seqSize is positive and won't walk past buffer bounds
                            if (seqSize <= 0 || p + seqSize > metadataPtr + metadataLen)
                            {
                                break;
                            }
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

            // Validate duration: Stopwatch can drift or go backward in rare cases; clamp to valid range
            if (durationMs.HasValue && durationMs.Value < 0)
            {
                durationMs = 0;
            }

            // Calculate message size: header + body. For decompressed messages, use header + decompressed body length.
            int messageSize = (int)message.Length;
            if (decompressedBodyLen > 0 && decompressedBodyLen != bodyLength)
            {
                // Message was decompressed, recalculate size (header is 16 bytes)
                messageSize = 16 + decompressedBodyLen;
            }

            var observed = new ObservedMessage(tag, connectionId, requestId, responseTo, opCode, doc, bodyPtr, bodyLength, tracker, durationMs, messageSize, docCount);

            foreach (var listener in _listeners)
            {
                observed.AddRef();
                listener.OnMessage(in observed);
            }
            
            observed.Release(); 
        }
        catch (Exception e)
        {
            _logger.LogDebug(e, $"Error observing message (OpCode: {opCode}, Conn: {connectionId})");
            tracker.Release();
        }
    }

    private unsafe bool TryDecompressMessage(
        byte* compressedPtr,
        int compressedLen,
        ArenaAllocator arena,
        out byte* decompressedPtr,
        out int decompressedLen,
        out OpCode originalOpCode)
    {
        decompressedPtr = null;
        decompressedLen = 0;
        originalOpCode = 0;

        try
        {
            if (compressedLen < 9) return false; // min: 4 (opcode) + 4 (size) + 1 (compressor)

            // Read original opcode (little-endian int32)
            int origOpCodeInt = BinaryPrimitives.ReadInt32LittleEndian(new ReadOnlySpan<byte>(compressedPtr, 4));
            originalOpCode = (OpCode)origOpCodeInt;

            // Read uncompressed size (little-endian int32)
            int uncompressedSize = BinaryPrimitives.ReadInt32LittleEndian(new ReadOnlySpan<byte>(compressedPtr + 4, 4));
            if (uncompressedSize <= 0 || uncompressedSize > 48 * 1024 * 1024) return false;

            // Read compressor ID (1 byte)
            byte compressorId = *(compressedPtr + 8);

            // Compressed payload starts at offset 9
            byte* payloadPtr = compressedPtr + 9;
            int payloadLen = compressedLen - 9;
            if (payloadLen <= 0) return false;

            // Allocate space for decompressed data
            byte* outPtr = (byte*)arena.Alloc((nuint)uncompressedSize);

            // Decompress based on compressor ID
            Span<byte> compressedSpan = new(payloadPtr, payloadLen);
            Span<byte> decompressedSpan = new(outPtr, uncompressedSize);

            switch (compressorId)
            {
                case 0: // Snappy — requires external library
                    _logger.LogDebug("Snappy decompression requested but not implemented; message visibility degraded");
                    return false;

                case 1: // Zlib (using built-in DeflateStream)
                    {
                        try
                        {
                            using (var compressedStream = new MemoryStream(compressedSpan.ToArray()))
                            using (var decompressedStream = new MemoryStream(uncompressedSize))
                            using (var deflate = new DeflateStream(compressedStream, CompressionMode.Decompress, leaveOpen: false))
                            {
                                deflate.CopyTo(decompressedStream);
                                int zlibLen = (int)decompressedStream.Length;
                                if (zlibLen != uncompressedSize) return false;
                                decompressedStream.Position = 0;
                                decompressedStream.Read(decompressedSpan);
                                decompressedLen = zlibLen;
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug($"Zlib decompression failed: {ex.Message}");
                            return false;
                        }
                    }
                    break;

                case 2: // Zstd — requires external library
                    _logger.LogDebug("Zstd decompression requested but not implemented; message visibility degraded");
                    return false;

                default:
                    _logger.LogDebug($"Unknown compressor ID: {compressorId}");
                    return false;
            }

            decompressedPtr = outPtr;
            return decompressedLen > 0 && decompressedLen == uncompressedSize;
        }
        catch (Exception ex)
        {
            _logger.LogDebug($"Decompression failed: {ex.Message}");
            return false;
        }
    }

    private bool ShouldParseBody(OpCode opCode, int bodyLength)
    {
        // Don't parse high-frequency/low-value opcodes that usually don't contain metadata for us
        if (opCode == OpCode.OP_GET_MORE || opCode == OpCode.OP_KILL_CURSORS) return false;
        if ((int)opCode is 2001 or 2002 or 2006) return false; // Legacy UPDATE, INSERT, DELETE

        // Always parse OP_MSG because it contains the command metadata.
        // Small control messages (hello, ping) are cheap to parse and necessary for filtering.
        // Large data messages (find results) are only skipped if NO listener wants them (handled in ObserveMessage).
        return true;
    }
}
