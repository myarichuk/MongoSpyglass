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
    private readonly System.Timers.Timer _uiUpdateTimer;
    private readonly RavenStorageService _ravenService;
    private readonly NotificationHubService _notificationHub;
    private bool _needsUiUpdate = false;

    public event Action? OnTrafficReceived;

    public bool ShowHello { get; set; } = false;
    public int ThroughputOpsPerSec { get; private set; }
    public double? AverageLatencyMs { get; private set; }

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
        _throughputTimer.Elapsed += (s, e) => {
            CalculateThroughput();
            CalculateAvgLatency();
        };
        _throughputTimer.Start();

        _uiUpdateTimer = new System.Timers.Timer(100); // 10 FPS max for UI updates
        _uiUpdateTimer.Elapsed += (s, e) => {
            if (_needsUiUpdate)
            {
                _needsUiUpdate = false;
                OnTrafficReceived?.Invoke();
            }
        };
        _uiUpdateTimer.Start();

        _ravenService.OnSessionChanged += (sessionId) => {
            _lock.EnterWriteLock();
            try {
                Array.Clear(_circularBuffer, 0, _circularBuffer.Length);
                _head = 0;
                _count = 0;
                _pendingRequests.Clear();
            } finally { _lock.ExitWriteLock(); }
            RequestUiUpdate();
        };

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
                            OpCode = item.Op.OpCode,
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
                RequestUiUpdate();
            }
        });
    }

    private void RequestUiUpdate() => _needsUiUpdate = true;

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
        
        RequestUiUpdate();
    }

    public IReadOnlyList<DecryptedTraffic> Traffic
    {
        get
        {
            _lock.EnterReadLock();
            try
            {
                // Return a snapshot to avoid multi-threaded access issues in the UI
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
                            var responseBson = msg.FullBody.ToArray();
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
                                // Find in circular buffer to update duration/size/docCount/response
                                for (int i = 0; i < _count; i++)
                                {
                                    int index = (_head - 1 - i + MaxItems) % MaxItems;
                                    if (_circularBuffer[index].RequestId == msg.ResponseTo)
                                    {
                                        _circularBuffer[index].DurationMs = duration;
                                        _circularBuffer[index].SizeBytes = finalOp.SizeBytes;
                                        _circularBuffer[index].DocumentCount = finalOp.DocumentCount;
                                        _circularBuffer[index].ResponseBson = responseBson;
                                        _circularBuffer[index].ResponseOpCode = msg.OpCode.ToString();
                                        break;
                                    }
                                }
                            }
                            finally
                            {
                                _lock.ExitWriteLock();
                            }
                        }
                        RequestUiUpdate();
                        continue;
                    }

                    string cmdName = "unknown";
                    string collection = "N/A";

                    byte[] fullBody = msg.FullBody.ToArray();

                    if (!msg.Document.IsDefault)
                    {
                        switch (msg.OpCode)
                        {
                            case OpCode.OP_QUERY:
                                cmdName = "find";
                                collection = ExtractCollectionFromLegacy(fullBody);
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
                                            collection = msg.Document.GetString(valOff);
                                         } catch { 
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

                        if (msg.OpCode == OpCode.OP_GET_MORE || (int)msg.OpCode is 2001 or 2002 or 2006)
                        {
                            collection = ExtractCollectionFromLegacy(fullBody);
                        }
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
                        RawBson = fullBody,
                        DurationMs = msg.DurationMs,
                        SizeBytes = msg.MessageSizeBytes,
                        DocumentCount = msg.DocumentCount
                    };

                    var op = new MongoOperation
                    {
                        Id = "MongoOperations/" + Guid.NewGuid().ToString(),
                        RequestId = msg.RequestId,
                        OpCode = msg.OpCode.ToString(),
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
                    RequestUiUpdate();
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

    private string ExtractCollectionFromLegacy(byte[] body)
    {
        if (body.Length < 5) return "N/A";
        int pos = 4; // Skip flags
        int start = pos;
        while (pos < body.Length && body[pos] != 0) pos++;
        if (pos == start) return "N/A";
        return System.Text.Encoding.UTF8.GetString(body, start, pos - start);
    }

    private void CalculateAvgLatency()
    {
        _lock.EnterReadLock();
        try
        {
            var latencies = new List<double>();
            for (int i = 0; i < _count; i++)
            {
                var duration = _circularBuffer[i].DurationMs;
                if (duration.HasValue)
                {
                    latencies.Add(duration.Value);
                }
            }
            AverageLatencyMs = latencies.Count > 0 ? latencies.Average() : null;
        }
        finally
        {
            _lock.ExitReadLock();
        }
        RequestUiUpdate();
    }

    public void Dispose()
    {
        _cts.Cancel();
        _throughputTimer.Dispose();
        _uiUpdateTimer.Dispose();
        _lock.Dispose();
    }
}
