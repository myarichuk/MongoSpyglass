using Microsoft.Extensions.Hosting;
using MongoSpyglass.Proxy;
using MongoSpyglass.Service.Analyzers.Rete;
using MongoSpyglass.Service.Data;
using NRules;
using NRules.Fluent;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO.Hashing;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace MongoSpyglass.Service.Analyzers;

public class ReteAnalyzerEngine : BackgroundService, IAnalyzerPlugin
{
    private readonly Channel<object> _channel;
    private readonly NRules.ISession _session;
    private readonly object _syncLock = new();
    private readonly ConcurrentQueue<Insight> _insights = new();
    private readonly ConcurrentQueue<(string hash, string kind, string exampleJson, string ns, string command)> _queryExamplesToSave = new();
    private readonly TimeTick _tick = new();
    private readonly RavenStorageService _ravenService;
    private readonly SettingsService _settingsService;
    private SettingsSnapshotFact _settingsSnapshot;
    private CursorLeakAlertThresholdFact _leakAlertThreshold;
    private bool _hydrated = false;
    private bool _healthy = true;
    private DateTime _lastHydrateAttempt = DateTime.MinValue;
    private long _droppedFactCount = 0;

    public ReteAnalyzerEngine(RavenStorageService ravenService, SettingsService settingsService)
    {
        _ravenService = ravenService;
        _settingsService = settingsService;
        _settingsSnapshot = new SettingsSnapshotFact { SlowQueryThresholdMs = settingsService.Current.SlowQueryThresholdMs };
        _leakAlertThreshold = new CursorLeakAlertThresholdFact { IdleHoursThreshold = settingsService.Current.CursorLeakAlertThresholdHours };
        var repository = new RuleRepository();
        repository.Load(x => x.From(typeof(TrackRequestRule).Assembly));
        var factory = repository.Compile();
        _session = factory.CreateSession();
        _channel = Channel.CreateBounded<object>(new BoundedChannelOptions(2048)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true
        });

        ravenService.OnSessionChanged += (sessionId) => {
            _insights.Clear();
            lock (_syncLock)
            {
                // Clear facts in session
                var facts = _session.Query<object>().ToList();
                foreach (var f in facts) _session.Retract(f);
                _session.Insert(_tick);
                _hydrated = false;
            }
        };

        settingsService.OnSettingsChanged += () => {
            _settingsSnapshot.SlowQueryThresholdMs = settingsService.Current.SlowQueryThresholdMs;
            _leakAlertThreshold.IdleHoursThreshold = settingsService.Current.CursorLeakAlertThresholdHours;
            lock (_syncLock)
            {
                _session.Update(_settingsSnapshot);
                _session.Update(_leakAlertThreshold);
            }
        };
    }

    public string Name => "Rete Analyzer Engine";
    public bool IsHealthy => _healthy;
    public long DroppedFactCount => _droppedFactCount;

    public IEnumerable<Insight> GetInsights() => _insights.ToArray();

    public void QueueQueryExampleForSave(string hash, string kind, string exampleJson, string ns, string command)
    {
        _queryExamplesToSave.Enqueue((hash, kind, exampleJson, ns, command));
    }

    public int ActiveCursorsCount
    {
        get
        {
            lock (_syncLock)
            {
                return _session.Query<CursorFact>().Count(x => !x.IsClosed);
            }
        }
    }

    public CursorStatsFact GetCursorStats()
    {
        lock (_syncLock)
        {
            return _session.Query<CursorStatsFact>().FirstOrDefault() ?? new CursorStatsFact();
        }
    }

    public void OnMessage(in ObservedMessage msg)
    {
        try
        {
            var timestamp = DateTime.UtcNow;

            if (msg.Tag == "to")
            {
                // Request message: extract command, collection, hashes, example
                if (!msg.Document.IsDefault && msg.Document.KeysEnumerable.Any())
                {
                    var command = msg.Document.KeysEnumerable.First().ToString();
                    var collection = ExtractCollection(msg.Document);

                    // Compute hashes
                    var (shapeHash, valueHash) = ComputeQueryHashes(msg.Document, collection, msg.Tracker);
                    var exampleJson = BuildExampleJson(msg.Document, maxBytes: 4096);

                    var fact = new RequestObservedFact
                    {
                        RequestId = msg.RequestId,
                        ConnectionId = msg.ConnectionId,
                        Timestamp = timestamp,
                        Command = command,
                        Collection = collection,
                        ShapeHash = shapeHash,
                        ValueHash = valueHash,
                        ExampleJson = exampleJson
                    };

                    TryWrite(fact);

                    // Check for getMore or killCursors
                    if (command == "getMore" && msg.Document.TryGetElementOffset("getMore", out var getMoreOffset))
                    {
                        try
                        {
                            var cursorId = msg.Document.GetInt64(getMoreOffset);
                            TryWrite(new GetMoreRequestedFact
                            {
                                RequestId = msg.RequestId,
                                ConnectionId = msg.ConnectionId,
                                CursorId = cursorId,
                                Timestamp = timestamp
                            });
                        }
                        catch { }
                    }
                    else if (command == "killCursors" && msg.Document.TryGetElementOffset("cursors", out var cursorsOffset))
                    {
                        try
                        {
                            var cursorsArray = msg.Document.GetArray(cursorsOffset, msg.Tracker.Arena);
                            foreach (var el in cursorsArray)
                            {
                                try
                                {
                                    var cursorId = el.Get<long>();
                                    TryWrite(new KillCursorsRequestedFact
                                    {
                                        CursorId = cursorId,
                                        ConnectionId = msg.ConnectionId,
                                        Timestamp = timestamp
                                    });
                                }
                                catch { }
                            }
                        }
                        catch { }
                    }
                }
            }
            else if (msg.Tag == "from")
            {
                // Response message: extract cursor, namespace, document count
                long? cursorId = null;
                string? ns = null;

                if (!msg.Document.IsDefault)
                {
                    try
                    {
                        if (msg.Document.TryGetElementOffset("cursor", out var cursorOffset))
                        {
                            var cursorDoc = msg.Document.GetDocument(cursorOffset, msg.Tracker.Arena);
                            if (cursorDoc.TryGetElementOffset("id", out var idOffset))
                            {
                                cursorId = cursorDoc.GetInt64(idOffset);
                            }
                            if (cursorDoc.TryGetElementOffset("ns", out var nsOffset))
                            {
                                ns = cursorDoc.GetString(nsOffset);
                            }
                        }
                    }
                    catch { }
                }

                var responseFact = new ResponseObservedFact
                {
                    RequestId = msg.ResponseTo,
                    ConnectionId = msg.ConnectionId,
                    Timestamp = timestamp,
                    DurationMs = msg.DurationMs ?? 0,
                    MessageSizeBytes = msg.MessageSizeBytes,
                    DocumentCount = msg.DocumentCount,
                    CursorId = cursorId,
                    Namespace = ns
                };

                TryWrite(responseFact);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error in OnMessage: {ex.Message}");
        }
    }

    private void TryWrite(object fact)
    {
        if (!_channel.Writer.TryWrite(fact))
        {
            Interlocked.Increment(ref _droppedFactCount);
        }
    }

    private string ExtractCollection(MongoSpyglass.Proxy.Bson.BlittableBsonDocument doc)
    {
        try
        {
            if (doc.TryGetElementOffset("collection", out var colOffset))
                return doc.GetString(colOffset);

            if (doc.KeysEnumerable.Any())
            {
                var firstKey = doc.KeysEnumerable.First().ToString();
                if (doc.TryGetElementOffset(firstKey.AsSpan(), out var offset))
                    return doc.GetString(offset);
            }

            if (doc.TryGetElementOffset("$db", out var dbOff))
                return doc.GetString(dbOff);
        }
        catch { }

        return "unknown";
    }

    private (string shapeHash, string valueHash) ComputeQueryHashes(MongoSpyglass.Proxy.Bson.BlittableBsonDocument doc, string collection, MongoSpyglass.Proxy.Memory.ArenaTracker? tracker)
    {
        try
        {
            var fieldNames = new List<string>();
            var fieldOperators = new List<string>();
            var fieldValues = new StringBuilder();

            // Try to extract filter from query field if present
            if (doc.KeysEnumerable.Any())
            {
                var firstKey = doc.KeysEnumerable.First().ToString();
                if (firstKey == "find" && doc.TryGetElementOffset("filter", out var filterOffset))
                {
                    if (tracker != null)
                    {
                        var filterDoc = doc.GetDocument("filter", tracker.Arena);
                        ExtractFilterInfo(filterDoc, fieldNames, fieldOperators, fieldValues);
                    }
                }
                else if (doc.TryGetElementOffset(firstKey, out var queryOffset))
                {
                    // For OP_QUERY style
                    ExtractFilterInfo(doc, fieldNames, fieldOperators, fieldValues);
                }
            }

            // Sort field names for consistent hashing
            fieldNames.Sort();

            // Compute shape hash: namespace + field names/operators
            var shapeBuilder = new StringBuilder();
            shapeBuilder.Append(collection);
            foreach (var name in fieldNames)
                shapeBuilder.Append(name);
            foreach (var op in fieldOperators)
                shapeBuilder.Append(op);

            var shapeBytes = Encoding.UTF8.GetBytes(shapeBuilder.ToString());
            var shapeHashBytes = new byte[16];
            XxHash128.TryHash(shapeBytes, shapeHashBytes, out _);
            var shapeHash = Convert.ToHexString(shapeHashBytes);

            // Compute value hash: shape + field values
            var valueBuilder = new StringBuilder();
            valueBuilder.Append(shapeBuilder.ToString());
            valueBuilder.Append(fieldValues.ToString());

            var valueBytes = Encoding.UTF8.GetBytes(valueBuilder.ToString());
            var valueHashBytes = new byte[16];
            XxHash128.TryHash(valueBytes, valueHashBytes, out _);
            var valueHash = Convert.ToHexString(valueHashBytes);

            return (shapeHash, valueHash);
        }
        catch
        {
            // Fallback: hash the entire collection name
            var fallbackBytes = Encoding.UTF8.GetBytes(collection);
            var hash = new byte[16];
            XxHash128.TryHash(fallbackBytes, hash, out _);
            var hashStr = Convert.ToHexString(hash);
            return (hashStr, hashStr);
        }
    }

    private void ExtractFilterInfo(MongoSpyglass.Proxy.Bson.BlittableBsonDocument doc, List<string> fieldNames, List<string> fieldOperators, StringBuilder fieldValues)
    {
        try
        {
            foreach (var key in doc.KeysEnumerable)
            {
                var keyStr = key.ToString();
                fieldNames.Add(keyStr);

                if (doc.TryGetElementOffset(keyStr, out var offset))
                {
                    try
                    {
                        // Try to extract string value for hashing
                        fieldValues.Append(doc.GetString(offset));
                    }
                    catch { }
                }
            }
        }
        catch { }
    }

    private string BuildExampleJson(MongoSpyglass.Proxy.Bson.BlittableBsonDocument doc, int maxBytes)
    {
        try
        {
            var json = new StringBuilder("{");
            bool first = true;
            int keysAdded = 0;

            foreach (var key in doc.KeysEnumerable.Take(10))
            {
                if (json.Length >= maxBytes - 10) break;

                if (!first) json.Append(",");
                first = false;
                keysAdded++;

                try
                {
                    var keyStr = key.ToString();
                    json.Append("\"").Append(EscapeJsonString(keyStr)).Append("\":");

                    if (doc.TryGetElementOffset(keyStr, out var offset))
                    {
                        json.Append(SerializeValueAtOffset(doc, offset, maxBytes - json.Length));
                    }
                    else
                    {
                        json.Append("null");
                    }
                }
                catch { }
            }

            if (keysAdded == 10) json.Append(",\"...\":\"truncated\"");
            json.Append("}");

            var result = json.ToString();
            if (result.Length > maxBytes)
                return result.Substring(0, maxBytes - 3) + "...";
            return result;
        }
        catch
        {
            return "{}";
        }
    }

    private string SerializeValueAtOffset(MongoSpyglass.Proxy.Bson.BlittableBsonDocument doc, int offset, int maxBytes)
    {
        if (maxBytes < 5) return "...";

        try
        {
            // Try to serialize as string first (most common case)
            try { return "\"" + EscapeJsonString(doc.GetString(offset)) + "\""; }
            catch { }

            // Try to serialize as number
            try { return doc.GetInt32(offset).ToString(); }
            catch { }

            try { return doc.GetInt64(offset).ToString(); }
            catch { }

            try { return doc.GetDouble(offset).ToString("G17"); }
            catch { }

            try { return doc.GetBoolean(offset).ToString().ToLower(); }
            catch { }

            // Default for complex types
            return "{...}";
        }
        catch
        {
            return "null";
        }
    }

    private string EscapeJsonString(string str)
    {
        return str.Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
    }

    public void OnConnectionClosed(string connectionId)
    {
        lock (_syncLock)
        {
            _session.Insert(new ConnectionClosedFact { ConnectionId = connectionId });
            _session.Fire();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        lock (_syncLock)
        {
            _session.Insert(_tick);
        }

        DateTime lastTick = DateTime.UtcNow;
        DateTime lastSave = DateTime.UtcNow;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!_hydrated)
                {
                    // Apply backoff if hydration keeps failing
                    if ((DateTime.UtcNow - _lastHydrateAttempt).TotalSeconds < 1)
                    {
                        await Task.Delay(1000, stoppingToken);
                        continue;
                    }

                    _lastHydrateAttempt = DateTime.UtcNow;
                    await HydrateAsync();
                    _hydrated = true;
                }

                bool hasItems = false;

                // Drain the channel and insert into session
                while (_channel.Reader.TryRead(out var factObj))
                {
                    lock (_syncLock)
                    {
                        _session.Insert(factObj);
                    }
                    hasItems = true;
                }

                var now = DateTime.UtcNow;

                // Periodic tick for TTL
                if ((now - lastTick).TotalSeconds >= 1)
                {
                    lock (_syncLock)
                    {
                        _tick.CurrentTime = now;
                        _session.Update(_tick);
                    }
                    hasItems = true;
                    lastTick = now;
                }

                // Periodic save
                if ((now - lastSave).TotalMinutes >= 1)
                {
                    await SaveStateAsync();
                    await SaveQueryExamplesAsync();
                    lastSave = now;
                }

                if (!hasItems && _channel.Reader.Count == 0)
                {
                    try
                    {
                        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                        cts.CancelAfter(TimeSpan.FromSeconds(1));
                        await _channel.Reader.WaitToReadAsync(cts.Token);
                    }
                    catch (OperationCanceledException) { }
                    continue;
                }

                if (hasItems)
                {
                    lock (_syncLock)
                    {
                        _session.Fire();

                        // Harvest insights produced by rules
                        var insights = _session.Query<Insight>().ToList();
                        foreach (var insight in insights)
                        {
                            _insights.Enqueue(insight);
                            _session.Retract(insight);

                            // Limit insights
                            while (_insights.Count > 100) _insights.TryDequeue(out _);
                        }

                        // Harvest query examples to save
                        var examples = _session.Query<QueryExampleToSaveFact>().ToList();
                        foreach (var example in examples)
                        {
                            _queryExamplesToSave.Enqueue((
                                example.Hash,
                                example.Kind,
                                example.ExampleJson,
                                example.Namespace,
                                example.Command
                            ));
                            _session.Retract(example);
                        }
                    }
                }

                _healthy = true;
            }
            catch (Exception ex)
            {
                _healthy = false;
                System.Diagnostics.Debug.WriteLine($"Error in ReteAnalyzerEngine loop: {ex.Message}");
                await Task.Delay(1000, stoppingToken); // Back off before retrying
            }
        }
    }

    private async Task HydrateAsync()
    {
        var sessionId = _ravenService.ActiveSessionId;
        if (string.IsNullOrEmpty(sessionId))
        {
            lock (_syncLock)
            {
                _session.Insert(new CursorStatsFact());
                _session.Insert(_settingsSnapshot);
                _session.Insert(_leakAlertThreshold);
            }
            return;
        }

        var stats = await _ravenService.GetLatestCursorStatsAsync(sessionId);
        var cursors = await _ravenService.GetActiveCursorsAsync(sessionId);

        lock (_syncLock)
        {
            _session.Insert(new SessionFact { Id = sessionId });
            _session.Insert(stats ?? new CursorStatsFact { SessionId = sessionId });
            _session.Insert(_settingsSnapshot);
            _session.Insert(_leakAlertThreshold);

            foreach (var c in cursors)
            {
                _session.Insert(c);
            }
            
            _session.Fire();
        }
    }

    private async Task SaveStateAsync()
    {
        var sessionId = _ravenService.ActiveSessionId;
        if (string.IsNullOrEmpty(sessionId)) return;

        CursorStatsFact stats;
        List<CursorFact> activeCursors;

        lock (_syncLock)
        {
            stats = _session.Query<CursorStatsFact>().FirstOrDefault() ?? new CursorStatsFact { SessionId = sessionId };
            activeCursors = _session.Query<CursorFact>().Where(x => !x.IsClosed).ToList();
        }

        await _ravenService.StoreCursorStatsAsync(stats);

        foreach (var c in activeCursors)
        {
            c.SessionId = sessionId;
            await _ravenService.StoreCursorAsync(c);
        }
    }

    private async Task SaveQueryExamplesAsync()
    {
        // Drain all queued examples and save them
        while (_queryExamplesToSave.TryDequeue(out var example))
        {
            try
            {
                await _ravenService.SaveQueryExampleAsync(
                    example.hash,
                    example.kind,
                    example.exampleJson,
                    example.ns,
                    example.command
                );
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error saving query example: {ex.Message}");
            }
        }
    }
}
