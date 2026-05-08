using System.Collections.Concurrent;
using System.Threading.Channels;
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
    public byte[]? RawBson { get; init; } // REPLACED: PayloadJson
    public double? DurationMs { get; init; }
    public int SizeBytes { get; init; }
    public int DocumentCount { get; init; }
}

public class TrafficMonitorService : ITrafficListener, IDisposable
{
    private const int MaxItems = 1000;
    private readonly DecryptedTraffic[] _circularBuffer = new DecryptedTraffic[MaxItems];
    private int _head = 0;
    private int _count = 0;
    private readonly ReaderWriterLockSlim _lock = new();
    private readonly ConcurrentDictionary<int, (DateTime Start, int Size)> _pendingRequests = new();
    
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
                        if (_pendingRequests.TryRemove(msg.ResponseTo, out var req))
                        {
                            var duration = (DateTime.Now - req.Start).TotalMilliseconds;
                            _lock.EnterWriteLock();
                            try
                            {
                                // Find in circular buffer to update duration/size
                                for (int i = 0; i < _count; i++)
                                {
                                    int index = (_head - 1 - i + MaxItems) % MaxItems;
                                    if (_circularBuffer[index].RequestId == msg.ResponseTo)
                                    {
                                        _circularBuffer[index] = _circularBuffer[index] with 
                                        { 
                                            DurationMs = duration,
                                            SizeBytes = _circularBuffer[index].SizeBytes + msg.MessageSizeBytes
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

                    _pendingRequests[msg.RequestId] = (DateTime.Now, msg.MessageSizeBytes);

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
                                    else if (msg.Document.TryGetElementOffset("$db", out var dbOff))
                                    {
                                        collection = msg.Document.GetString(dbOff);
                                    }
                                    else if (msg.Document.ContainsKey(cmdName))
                                    {
                                         try {
                                            collection = msg.Document.GetString(cmdName.AsSpan());
                                         } catch { }
                                    }
                                }
                                break;
                        }

                        // Noise filtering
                        if (!ShowHello && IsNoisyCommand(cmdName))
                        {
                            continue;
                        }
                    }

                    var entry = new DecryptedTraffic
                    {
                        Tag = msg.Tag,
                        RequestId = msg.RequestId,
                        OpCode = msg.OpCode.ToString(),
                        Command = cmdName,
                        Collection = collection,
                        RawBson = msg.Document.AsReadOnlySpan().ToArray(),
                        DurationMs = msg.DurationMs,
                        SizeBytes = msg.MessageSizeBytes,
                        DocumentCount = msg.DocumentCount
                    };

                    // Persist to RavenDB
                    _ = _ravenService.StoreOperationAsync(new MongoOperation
                    {
                        RequestId = msg.RequestId,
                        Collection = collection,
                        Command = cmdName,
                        DurationMs = msg.DurationMs,
                        SizeBytes = msg.MessageSizeBytes,
                        DocumentCount = msg.DocumentCount,
                        Timestamp = entry.Timestamp
                    }, entry.RawBson);

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
