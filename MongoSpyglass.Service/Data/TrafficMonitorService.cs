using System.Collections.Concurrent;
using System.Threading.Channels;
using System.Buffers.Binary;
using MongoSpyglass.Proxy;
using MongoSpyglass.Proxy.WireProtocol;
using MongoSpyglass.Proxy.Bson;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.IO;

namespace MongoSpyglass.Service.Data;

public record DecryptedTraffic
{
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public string Tag { get; init; } = string.Empty;
    public int RequestId { get; init; }
    public string OpCode { get; init; } = string.Empty;
    public string Command { get; init; } = string.Empty;
    public string Collection { get; init; } = string.Empty;
    public byte[]? RawBson { get; init; }
    public double? DurationMs { get; init; }
    public int SizeBytes { get; init; }
    public int DocumentCount { get; init; }

    public string PayloadJson 
    {
        get 
        {
            if (RawBson == null || RawBson.Length == 0) return "{}";
            try 
            {
                if (OpCode == "OP_MSG")
                {
                    return ParseOpMsg(RawBson);
                }

                var doc = BsonSerializer.Deserialize<BsonDocument>(RawBson);
                return doc.ToJson(new JsonWriterSettings { Indent = true });
            } 
            catch 
            {
                return "{ \"error\": \"failed to parse bson\" }";
            }
        }
    }

    private static string ParseOpMsg(byte[] bytes)
    {
        var result = new BsonDocument();
        ReadOnlyMemory<byte> memory = bytes;
        
        if (memory.Length < 4) return "{}";
        
        int flagBits = BinaryPrimitives.ReadInt32LittleEndian(memory.Span);
        bool checksumPresent = (flagBits & 1) != 0;
        int dataLen = memory.Length - (checksumPresent ? 4 : 0);
        
        int pos = 4;
        while (pos < dataLen)
        {
            byte kind = memory.Span[pos++];
            if (kind == 0) // Body
            {
                int docLen = BinaryPrimitives.ReadInt32LittleEndian(memory.Span.Slice(pos));
                var doc = BsonSerializer.Deserialize<BsonDocument>(memory.Slice(pos, docLen).ToArray());
                foreach (var el in doc) result[el.Name] = el.Value;
                pos += docLen;
            }
            else if (kind == 1) // Sequence
            {
                int seqSize = BinaryPrimitives.ReadInt32LittleEndian(memory.Span.Slice(pos));
                int seqEnd = pos + seqSize;
                pos += 4;
                
                // Read identifier
                int identStart = pos;
                while (pos < seqEnd && memory.Span[pos] != 0) pos++;
                string identifier = System.Text.Encoding.UTF8.GetString(memory.Span.Slice(identStart, pos - identStart).ToArray());
                pos++; // null
                
                var array = new BsonArray();
                while (pos < seqEnd)
                {
                    int docLen = BinaryPrimitives.ReadInt32LittleEndian(memory.Span.Slice(pos));
                    array.Add(BsonSerializer.Deserialize<BsonDocument>(memory.Slice(pos, docLen).ToArray()));
                    pos += docLen;
                }
                result[identifier] = array;
            }
            else break;
        }

        return result.ToJson(new JsonWriterSettings { Indent = true });
    }
}

public class TrafficMonitorService : ITrafficListener, IDisposable
{
    private const int MaxItems = 1000;
    private readonly DecryptedTraffic[] _circularBuffer = new DecryptedTraffic[MaxItems];
    private int _head = 0;
    private int _count = 0;
    private readonly ReaderWriterLockSlim _lock = new();
    private readonly ConcurrentDictionary<int, (MongoOperation Op, byte[]? Bson)> _pendingRequests = new();
    
    private readonly Channel<ObservedMessage> _incomingChannel;
    private readonly CancellationTokenSource _cts = new();
    private readonly System.Timers.Timer _throughputTimer;
    private readonly RavenStorageService _ravenService;
    private readonly NotificationHubService _notificationHub;

    public event Action? OnTrafficReceived;

    public bool ShowHello { get; set; } = false;
    public int ThroughputOpsPerSec { get; private set; }

    public TrafficMonitorService(RavenStorageService ravenService, NotificationHubService notificationHub)
    {
        _ravenService = ravenService;
        _notificationHub = notificationHub;
        _incomingChannel = Channel.CreateBounded<ObservedMessage>(new BoundedChannelOptions(2048)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true
        });

        Task.Run(ProcessMessagesAsync);

        _throughputTimer = new System.Timers.Timer(1000);
        _throughputTimer.Elapsed += (s, e) => CalculateThroughput();
        _throughputTimer.Start();

        // Preload data from resumed session
        Task.Run(async () => {
            var activeSessionId = await GetActiveSessionIdAsync();
            if (activeSessionId != null)
            {
                var history = await _ravenService.GetLatestOperationsAsync(activeSessionId, 1000);
                _lock.EnterWriteLock();
                try {
                    // History is desc, we want to add in chronological order
                    foreach (var item in history.AsEnumerable().Reverse())
                    {
                        var entry = new DecryptedTraffic
                        {
                            Timestamp = item.Op.Timestamp,
                            Tag = "to", // Assumption for preloaded request view
                            RequestId = item.Op.RequestId,
                            OpCode = item.Op.Command == "find" ? "OP_QUERY" : "OP_MSG",
                            Command = item.Op.Command,
                            Collection = item.Op.Collection,
                            RawBson = item.Bson,
                            DurationMs = item.Op.DurationMs,
                            SizeBytes = item.Op.SizeBytes,
                            DocumentCount = item.Op.DocumentCount
                        };

                        _circularBuffer[_head] = entry;
                        _head = (_head + 1) % MaxItems;
                        if (_count < MaxItems) _count++;
                    }
                } finally { _lock.ExitWriteLock(); }
                OnTrafficReceived?.Invoke();
            }
        });
    }

    private async Task<string?> GetActiveSessionIdAsync()
    {
        var sessions = await _ravenService.GetSessionsAsync();
        return sessions.FirstOrDefault(x => x.IsActive)?.Id;
    }

    private void CalculateThroughput()
    {
        _lock.EnterReadLock();
        try
        {
            var now = DateTime.Now;
            var lastSec = now.AddSeconds(-1);
            int ops = 0;
            for (int i = 0; i < _count; i++)
            {
                int index = (_head - 1 - i + MaxItems) % MaxItems;
                if (_circularBuffer[index].Timestamp >= lastSec)
                {
                    ops++;
                }
                else
                {
                    break;
                }
            }
            ThroughputOpsPerSec = ops;
        }
        finally
        {
            _lock.ExitReadLock();
        }
        
        OnTrafficReceived?.Invoke();
    }

    public IEnumerable<DecryptedTraffic> Traffic
    {
        get
        {
            _lock.EnterReadLock();
            try
            {
                var result = new List<DecryptedTraffic>(_count);
                for (int i = 0; i < _count; i++)
                {
                    int index = (_head - 1 - i + MaxItems) % MaxItems;
                    result.Add(_circularBuffer[index]);
                }
                return result;
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }
    }

    public void OnMessage(in ObservedMessage msg)
    {
        msg.AddRef();
        if (!_incomingChannel.Writer.TryWrite(msg))
        {
            msg.Release();
        }
    }

    private async Task ProcessMessagesAsync()
    {
        try
        {
            await foreach (var msg in _incomingChannel.Reader.ReadAllAsync(_cts.Token))
            {
                try
                {
                    if (msg.Tag == "from")
                    {
                        if (_pendingRequests.TryRemove(msg.ResponseTo, out var pending))
                        {
                            var duration = (DateTime.Now - pending.Op.Timestamp).TotalMilliseconds;
                            var finalOp = pending.Op with 
                            { 
                                DurationMs = duration,
                                SizeBytes = pending.Op.SizeBytes + msg.MessageSizeBytes,
                                DocumentCount = msg.DocumentCount
                            };

                            // Persist COMPLETED operation to RavenDB
                            _ = _ravenService.StoreOperationAsync(finalOp, pending.Bson);

                            _lock.EnterWriteLock();
                            try
                            {
                                // Find in circular buffer to update duration/size/docCount
                                for (int i = 0; i < _count; i++)
                                {
                                    int index = (_head - 1 - i + MaxItems) % MaxItems;
                                    if (_circularBuffer[index].RequestId == msg.ResponseTo)
                                    {
                                        _circularBuffer[index] = _circularBuffer[index] with 
                                        { 
                                            DurationMs = duration,
                                            SizeBytes = finalOp.SizeBytes,
                                            DocumentCount = finalOp.DocumentCount
                                        };
                                        break;
                                    }
                                }
                            }
                            finally
                            {
                                _lock.ExitWriteLock();
                            }
                        }
                        OnTrafficReceived?.Invoke();
                        continue;
                    }

                    string cmdName = "unknown";
                    string collection = "N/A";

                    if (!msg.Document.IsDefault)
                    {
                        switch (msg.OpCode)
                        {
                            case OpCode.OP_QUERY:
                                if (msg.Document.TryGetElementOffset("collection", out var colOffset))
                                {
                                    collection = msg.Document.GetString(colOffset);
                                }
                                cmdName = "find";
                                break;
                            case OpCode.OP_MSG:
                                if (msg.Document.KeysEnumerable.Any())
                                {
                                    var firstKey = msg.Document.KeysEnumerable.First();
                                    cmdName = firstKey.ToString();
                                    
                                    if (msg.Document.TryGetElementOffset("collection", out var collOff))
                                    {
                                        collection = msg.Document.GetString(collOff);
                                    }
                                    else if (msg.Document.TryGetElementOffset(cmdName.AsSpan(), out var valOff))
                                    {
                                         try {
                                            // Many commands have { "cmd": "collection" }
                                            collection = msg.Document.GetString(valOff);
                                         } catch { 
                                            // If it's not a string (e.g. { "ping": 1 }), use $db or keep N/A
                                            if (msg.Document.TryGetElementOffset("$db", out var dbOff))
                                            {
                                                collection = $"$db:{msg.Document.GetString(dbOff)}";
                                            }
                                         }
                                    }
                                }
                                else
                                {
                                    cmdName = $"unknown_empty_bson_{(int)msg.OpCode}";
                                }
                                break;
                            default:
                                cmdName = $"unknown_unhandled_op_{(int)msg.OpCode}";
                                break;
                        }
                    }
                    else
                    {
                        cmdName = msg.OpCode switch
                        {
                            OpCode.OP_GET_MORE => "getMore",
                            OpCode.OP_KILL_CURSORS => "killCursors",
                            (OpCode)2001 => "update",
                            (OpCode)2002 => "insert",
                            (OpCode)2006 => "delete",
                            _ => $"unknown_failed_bson_{(int)msg.OpCode}"
                        };
                    }

                    // Noise filtering
                    if (!ShowHello && IsNoisyCommand(cmdName))
                    {
                        continue;
                    }

                    var entry = new DecryptedTraffic
                    {
                        Tag = msg.Tag,
                        RequestId = msg.RequestId,
                        OpCode = msg.OpCode.ToString(),
                        Command = cmdName,
                        Collection = collection,
                        RawBson = msg.FullBody.ToArray(),
                        DurationMs = msg.DurationMs,
                        SizeBytes = msg.MessageSizeBytes,
                        DocumentCount = msg.DocumentCount
                    };

                    var op = new MongoOperation
                    {
                        RequestId = msg.RequestId,
                        Collection = collection,
                        Command = cmdName,
                        DurationMs = msg.DurationMs,
                        SizeBytes = msg.MessageSizeBytes,
                        DocumentCount = msg.DocumentCount,
                        Timestamp = entry.Timestamp
                    };

                    // Store in pending for correlation
                    _pendingRequests[msg.RequestId] = (op, entry.RawBson);

                    _lock.EnterWriteLock();
                    try
                    {
                        _circularBuffer[_head] = entry;
                        _head = (_head + 1) % MaxItems;
                        if (_count < MaxItems)
                        {
                            _count++;
                        }
                    }
                    finally
                    {
                        _lock.ExitWriteLock();
                    }

                    _notificationHub.Refresh();
                    OnTrafficReceived?.Invoke();
                }
                finally
                {
                    msg.Release();
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private bool IsNoisyCommand(string cmd)
    {
        return cmd == "hello" || cmd == "isMaster" || cmd == "ismaster" || cmd == "ping" || 
               cmd == "buildinfo" || cmd == "buildInfo" || cmd == "whatsmyuri" || cmd == "listDatabases";
    }

    public void Dispose()
    {
        _cts.Cancel();
        _throughputTimer.Dispose();
        _lock.Dispose();
    }
}
