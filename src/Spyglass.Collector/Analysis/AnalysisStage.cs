using System.Collections.Concurrent;
using Spyglass.Collector.Api;
using Spyglass.Collector.Model;
using Spyglass.Collector.Pipeline;
using Spyglass.Collector.Storage;

namespace Spyglass.Collector.Analysis;

/// <summary>
/// Stage 2 (03 §1, §5, §6): one thread owns everything that crosses connections, so it needs no locks:
/// cursors (getMore arrives on any pooled connection), shape aggregates, the global rules and alerts,
/// live fan-out and the hand-off to storage. Each step is O(1) per op (the housekeeping passes are O(state), once a second or a minute).
/// <para>
/// With the join (02 §6) an op comes through up to twice: on arrival (wire analysis, aggregates, live feed:
/// visible at once, 03 §3.2) and, if it waited for context, when it is final (context applied, then stored).
/// Context for an op already stored goes to storage as an update.
/// </para>
/// </summary>
internal sealed class AnalysisStage
{
    private abstract record Item;
    private sealed record BatchItem(PartitionOutput Output) : Item;
    /// <summary>Work from another thread (the API) that must run where the state lives.</summary>
    private sealed record CommandItem(Action Run) : Item;

    /// <summary>Releases an item's ingest-queue charge (03 §1): held from ingest until this stage is done with it.</summary>
    private readonly Action<long> _release;

    private static readonly HashSet<string> CursorOpeners = ["find", "aggregate", "listCollections", "listIndexes"];
    private static readonly HashSet<string> Writes = ["insert", "update", "delete", "findAndModify", "findandmodify"];

    private readonly CollectorOptions _options;
    private readonly StorageWriter _writer;
    private readonly IAggregateStore _aggregates;
    private readonly IAlertStore _alertStore;
    private readonly IOperationStore _ops;
    private readonly LiveHub _live;
    private readonly ProducerRegistry _producers;
    /// <summary>Ops analysed but waiting for context (not stored yet): what <c>/ops/{id}</c> falls back to.</summary>
    private readonly ConcurrentDictionary<string, Operation> _waiting = new();
    /// <summary>Per exporter src: recent <c>cmd.ts − op.ts</c> samples, for the exporter-skew rule.</summary>
    private readonly Dictionary<string, SkewWindow> _skew = [];
    private int _skewPruneAt = 1024;
    private readonly HashSet<string> _touchedCallsites = [];
    private long _nextCallsiteTouchUs;
    private readonly WorkQueue<Item> _queue = new();
    /// <summary>Ops whose stream records are still to come, least recently active first (CI-22): a record moves its op to the end.</summary>
    private readonly Dictionary<(string, string, long, int), LinkedListNode<((string, string, long, int) Key, Operation Op)>> _streams = [];
    private readonly LinkedList<((string, string, long, int) Key, Operation Op)> _streamOrder = new();
    private const int MaxStreams = 10_000;
    private readonly Dictionary<(string?, string?), long> _collMaxReturned = [];
    private readonly Dictionary<(string?, string, string?), ErrorWindow> _errorWindows = [];
    private readonly Func<long> _nowUs;
    private Thread? _thread;
    private volatile CursorInfo[] _openCursors = [];
    private volatile CursorInfo[] _closedCursors = [];
    private volatile Dictionary<string, CursorShapeStats> _cursorStats = [];
    private long _cursorsPublished = -1, _nextRetentionUs, _nextShapePruneUs;
    private readonly List<string> _pendingAlertDeletes = [];
    private static readonly TimeSpan AlertRetention = TimeSpan.FromDays(30), ShapeRetention = TimeSpan.FromDays(30);

    public readonly AlertBook Alerts = new();
    public readonly CursorRegistry Cursors;
    public readonly AggregateBook Aggregates;
    public long Processed;
    /// <summary>Exceptions caught and isolated per item (CA-8) and the last one's text.</summary>
    public long Errors;
    public volatile string? LastError;
    /// <summary>Environment.TickCount64 of the first failed aggregate/alert store write since the last good one; 0 while they succeed (CI-1).</summary>
    public long StoreFailingSince;
    public volatile string? LastStoreError;
    public long ShapesTracked, ShapesEvicted;
    /// <summary>Environment.TickCount64 at the loop's last turn (at least every 100 ms when healthy).</summary>
    public long LastBeat = Environment.TickCount64;
    public bool Alive => _thread?.IsAlive == true;

    public AnalysisStage(CollectorOptions options, StorageWriter writer, IAggregateStore aggregates, IAlertStore alertStore,
        IOperationStore ops, LiveHub live, ProducerRegistry producers, Action<long> release, Func<long> nowUs)
    {
        _producers = producers;
        _options = options;
        _writer = writer;
        _aggregates = aggregates;
        _alertStore = alertStore;
        _ops = ops;
        _live = live;
        _release = release;
        _nowUs = nowUs;
        Cursors = new CursorRegistry(nowUs);
        Aggregates = new AggregateBook(options.OpRetention);
        foreach (var a in alertStore.All()) Alerts.Restore(a);
        // only what is in use: shapes unused for the retention are not loaded (they are deleted in housekeeping), and the catalog is capped
        var unusedBefore = nowUs() - (long)ShapeRetention.TotalMicroseconds;
        foreach (var s in aggregates.Shapes().Where(s => s.LastSeen >= unusedBefore).OrderByDescending(s => s.LastSeen).Take(AggregateBook.MaxShapes))
            Aggregates.Restore(s);
        Alerts.Publish();
    }

    public CursorInfo[] OpenCursors => _openCursors;
    public CursorInfo[] ClosedCursors => _closedCursors;
    public IReadOnlyDictionary<string, CursorShapeStats> CursorStats => _cursorStats;

    public void Start()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "collector-analysis" };
        _thread.Start();
    }

    /// <summary>One partition pass: new ops (in order), stream records, then join outcomes.</summary>
    public void Post(PartitionOutput output) => _queue.Add(new BatchItem(output));

    /// <summary>A single final op (tests).</summary>
    internal void Post(Operation op, List<RuleHit>? hits)
    {
        var o = new PartitionOutput();
        o.Ops.Add((op, hits, true));
        Post(o);
    }

    /// <summary>An op that is analysed and live but not stored yet (it is waiting for context).</summary>
    public Operation? Waiting(string id) => _waiting.GetValueOrDefault(id);

    /// <summary>Runs <paramref name="f"/> on the analysis thread (the state is single-threaded) and returns its result.</summary>
    public Task<T> Invoke<T>(Func<T> f)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Add(new CommandItem(() =>
        {
            try { tcs.SetResult(f()); }
            catch (Exception e) { tcs.SetException(e); }
        }));
        return tcs.Task;
    }
    public void Complete() => _queue.CompleteAdding();
    public void Join() => _thread?.Join();

    /// <summary>Runs pending wall-clock work now (tests).</summary>
    internal void Tick() => _tickRequested = true;
    private volatile bool _tickRequested;

    private void Loop()
    {
        var nextFlush = Environment.TickCount64;
        var nextHousekeeping = Environment.TickCount64;
        while (!_queue.IsCompleted)
        {
            var took = _queue.TryTake(out var item, 100);
            Volatile.Write(ref LastBeat, Environment.TickCount64);
            if (took)
            {
                if (item is BatchItem { Output: var b })
                {
                    long released = 0; // once per batch: the queue counter is a cache line ingest writes too
                    foreach (var (op, hits, final) in b.Ops)
                    {
                        // storage does not depend on analysis (CA-8): an op whose analysis throws is still stored (or still waits for its
                        // context) and keeps its shape in the catalog
                        try { OnOp(op, hits); }
                        catch (Exception e)
                        {
                            Fault(e);
                            try { if (!Aggregates.Shapes.ContainsKey(op.ShapeHash)) Count(op, _nowUs()); }
                            catch (Exception e2) { Fault(e2); }
                        }
                        try
                        {
                            if (final) Finalize(op);
                            else _waiting[op.Id] = op;
                        }
                        catch (Exception e) { Fault(e); }
                        released += op.Op.Size;
                    }
                    if (b.Streams != null)
                        foreach (var s in b.Streams)
                        {
                            try { OnStream(s); }
                            catch (Exception e) { Fault(e); }
                            finally { released += s.Size; }
                        }
                    try { OnJoins(b); }
                    catch (Exception e) { Fault(e); }
                    Interlocked.Add(ref Processed, b.Ops.Count);
                    _release(released);
                }
                else if (item is CommandItem(var run))
                {
                    try { run(); }
                    catch (Exception e) { Fault(e); }
                    Flush();
                }
            }
            var now = Environment.TickCount64;
            if (now >= nextFlush || _tickRequested)
            {
                Flush();
                nextFlush = now + 250;
            }
            if (now >= nextHousekeeping || _tickRequested)
            {
                try { Housekeeping(_nowUs()); }
                catch (Exception e) { Fault(e); }
                nextHousekeeping = now + 1000;
                _tickRequested = false;
            }
        }
        Flush();
    }

    /// <summary>Counts an isolated exception (CA-8). stderr gets the first few and then every thousandth: a hostile input must not flood it.</summary>
    private void Fault(Exception e)
    {
        var n = Interlocked.Increment(ref Errors);
        LastError = $"{e.GetType().Name}: {e.Message}";
        if (n <= 10 || n % 1000 == 0) Console.Error.WriteLine($"analysis: {LastError} ({n} so far)");
    }

    /// <summary>The op into the aggregates. A shape the catalog dropped (its cap) and the partition will not send again comes back from the store.</summary>
    private void Count(Operation op, long nowUs)
    {
        if (op.ShapeJson == null && !Aggregates.Shapes.ContainsKey(op.ShapeHash) && _aggregates.Shape(op.ShapeHash) is { } known)
            Aggregates.Restore(known);
        Aggregates.Add(op, nowUs);
    }

    private void OnOp(Operation op, List<RuleHit>? connectionHits)
    {
        var r = op.Op;
        var nowUs = _nowUs();

        // ---- cursors (03 §5.4) ----
        if (r.Command == "getMore" && op.GetMoreCursorId is { } gm)
        {
            var c = Cursors.Get(r.ProxyId, r.Member, gm);
            if (c != null)
            {
                op.ShapeHash = c.ShapeHash;   // 03 §4.3: getMores group with the query that opened the cursor
                op.ShapeJson = Aggregates.Shapes.TryGetValue(c.ShapeHash, out var si) ? si.ShapeJson : op.ShapeJson;
                op.App ??= c.App;
                op.AwaitsByDesign |= c.AwaitsByDesign;
            }
            if (r.Reply is { } f)
            {
                if (f.Ok == 0 && (f.Code == 43 || f.CodeName == "CursorNotFound"))
                {
                    if (Cursors.Close(r.ProxyId, r.Member, gm, "server-expired", r.Ts) is { } closed)
                        Abandoned(closed, op, "the server expired the cursor (CursorNotFound)");
                }
                else
                {
                    var c2 = f.CursorId == 0
                        ? Cursors.Close(r.ProxyId, r.Member, gm, "exhausted", r.Ts + (r.RespLastUs ?? 0), returned: f.NReturned ?? 0, finalGetMore: true)
                        : Cursors.Touched(r.ProxyId, r.Member, gm, r.Ts, f.NReturned ?? 0);
                    // drivers cap getMore batches (Node: 1000), so a collection's size shows in cursor totals, not single replies
                    if (c2 != null) LargeCollection(c2.Db, c2.Coll, c2.Documents);
                }
            }
        }
        else if (CursorOpeners.Contains(r.Command) && r.Reply?.CursorId is { } cid and not 0)
            Cursors.Opened(r.ProxyId, r.Member, cid, op.Id, op.ShapeHash, op.App, r.Db, r.Coll, r.Ts, r.Reply.NReturned ?? 0, op.AwaitsByDesign);
        else if (r.Command == "killCursors" && op.KillCursorIds != null && r.Status == "ok")
        {
            // 03 §5.4: closed by the reply. It carries counts, not ids: when the server found none of them (cursorsNotFound only) it had
            // already dropped them, and an abandoned cursor must not read as killed; any other mix is read as killed
            var gone = r.Reply is { CursorsNotFound: > 0, CursorsKilled: 0 };
            foreach (var id in op.KillCursorIds)
                if (!gone) Cursors.Close(r.ProxyId, r.Member, id, "killed", r.Ts);
                else if (Cursors.Close(r.ProxyId, r.Member, id, "server-expired", r.Ts) is { } closed)
                    Abandoned(closed, op, "the server had already dropped the cursor (killCursors: cursorsNotFound)");
        }

        if (r.Stream) TrackStream((r.ProxyId, r.Member, r.RouteSid, r.RequestId), op);

        Count(op, nowUs);

        // ---- global rules (03 §5.2, wire-only) ----
        var nr = r.Reply?.NReturned ?? 0;
        LargeCollection(r.Db, r.Coll, nr);

        // the scope and the summary are built only when the alert is new or its summary is due (CA-18): a slow shape at a high rate
        // would otherwise allocate a dictionary and a few strings per op to update two counters
        if (r.ServerLatencyUs > _options.SlowShapeUs && !op.AwaitsByDesign)
            Hit("slow-shape", op.ShapeHash, op, (op, _options.SlowShapeUs), static s => Scope(s.op, shape: true),
                static s => $"{s.op.Op.Command} {s.op.Ns} took {s.op.Op.ServerLatencyUs / 1000.0:F1} ms on the server (> {s.Item2 / 1000} ms)");
        if (r.Command != "getMore" && (r.RespBytes > 1 << 20 || nr > 1000))
            Hit("large-result", op.ShapeHash, op, op, static o => Scope(o, shape: true),
                static o => $"{o.Op.Command} {o.Ns} returned {o.Op.Reply?.NReturned ?? 0} documents / {o.Op.RespBytes / 1024} KiB in one reply");
        if (op.InCardinality > 1000)
            Hit("unbounded-in", op.ShapeHash, op, op, static o => Scope(o, shape: true), static o => $"$in with {o.InCardinality} values on {o.Ns}");
        if (op.Unbounded && _collMaxReturned.GetValueOrDefault((r.Db, r.Coll)) > 1000)
            Hit("unbounded-scan", $"{op.App}|{op.ShapeHash}", op, op, static o => Scope(o, shape: true, app: true),
                static o => $"{o.Op.Command} on {o.Ns} with no filter and no limit, on a collection that has returned > 1000 documents");
        if (!r.Ack && Writes.Contains(r.Command))
            Hit("unacknowledged-writes", $"{op.App}|{r.Coll}", op, op, static o => Scope(o, app: true, coll: true),
                static o => $"unacknowledged (w:0) {o.Op.Command} on {o.Ns}: failures are invisible to the application");
        ErrorBurst(op);

        if (connectionHits != null)
            foreach (var h in connectionHits)
            {
                Hit(h.Rule, h.ScopeKey, Scope(op, shape: true, app: true), op, h.Summary);
            }

        if (op.Ctx != null) Context(op);
        _live.Publish("op.created", op); // JSON only for a subscriber that takes it
    }

    /// <summary>Nothing more will join this op: store it.</summary>
    private void Finalize(Operation op)
    {
        op.Stored = true;
        _writer.Enqueue(op);
    }

    /// <summary>One try per item (CA-8): a failing final must not take the batch's other finals, late joins and conflicts with it.</summary>
    private void OnJoins(PartitionOutput b)
    {
        if (b.Finals != null)
            foreach (var (op, ctx) in b.Finals)
            {
                _waiting.TryRemove(op.Id, out _);
                try
                {
                    if (ctx != null)
                    {
                        Volatile.Write(ref op.Ctx, ctx);
                        Context(op);
                        _live.Publish("op.updated", op); // 03 §3.2: the live row is patched in place
                    }
                }
                catch (Exception e) { Fault(e); }
                try { Finalize(op); }
                catch (Exception e) { Fault(e); }
            }
        if (b.LateJoins != null)
            foreach (var j in b.LateJoins)
            {
                try
                {
                    _writer.Enqueue(new OpUpdate(j.OpId, Ctx: j.Cmd)); // the writer publishes op.updated once it is applied
                    Skew(j.Cmd, j.OpTs, j.OpId);
                    if (j.Cmd.Callsite is { } cs) _touchedCallsites.Add(cs);
                }
                catch (Exception e) { Fault(e); }
            }
        if (b.Conflicts != null)
            foreach (var c in b.Conflicts)
            {
                try
                {
                    Hit("exporter-skew", c.Cmd.Src, new Dictionary<string, string?> { ["src"] = c.Cmd.Src }, null,
                        $"join conflict: exporter {c.Cmd.Src} reported {c.Cmd.Command} on {c.Cmd.Db} for request {c.Cmd.RequestId}, " +
                        $"the proxy saw {c.OpCommand} on {c.OpDb} (02 §6.3)", tsOverride: c.Cmd.Ts);
                }
                catch (Exception e) { Fault(e); }
            }
    }

    /// <summary>A joined op (on arrival or after waiting): the context-side checks M2 has (03 §5.2 exporter-skew).</summary>
    private void Context(Operation op)
    {
        var c = op.Ctx!;
        Skew(c, op.Ts, op.Id);
        if (c.Callsite is { } cs) _touchedCallsites.Add(cs);
    }

    /// <summary>
    /// 03 §5.2 exporter-skew: the exporter stamps a command when the driver starts it, the proxy when its first
    /// byte arrives, so on one clock <c>cmd.ts − op.ts</c> is minus the client→proxy latency: well under a
    /// millisecond on a LAN. The median over the last 64 joins against ±1 s.
    /// </summary>
    private void Skew(CmdRecord c, long opTs, string opId)
    {
        if (!_skew.TryGetValue(c.Src, out var w))
        {
            if (_skew.Count >= _skewPruneAt) // windows of producers the registry has expired (idle, or over its cap)
            {
                foreach (var k in _skew.Keys.Where(k => _producers.Get(k) == null).ToList()) _skew.Remove(k);
                _skewPruneAt = Math.Max(1024, _skew.Count * 2);
            }
            _skew[c.Src] = w = new SkewWindow();
        }
        if (w.Add(c.Ts - opTs) is not { } median) return;
        if (_producers.Get(c.Src) is { } p) p.SkewUs = median;
        if (Math.Abs(median) > 1_000_000)
            Hit("exporter-skew", c.Src, new Dictionary<string, string?> { ["src"] = c.Src }, null,
                $"exporter {c.Src} clock is {median / 1000.0:F0} ms {(median > 0 ? "ahead of" : "behind")} the proxy's (median of the last {SkewWindow.Size} joins)",
                tsOverride: c.Ts);
    }

    internal sealed class SkewWindow
    {
        public const int Size = 64;
        private readonly long[] _samples = new long[Size];
        private readonly long[] _sorted = new long[Size];
        private int _next, _count, _sinceMedian; // a ring index and a count: a running total would overflow after 2^31 joins (CA-1)

        /// <summary>Adds a sample; every 16th returns the median of the window.</summary>
        public long? Add(long v)
        {
            _samples[_next] = v;
            _next = (_next + 1) % Size;
            if (_count < Size) _count++;
            if (++_sinceMedian < 16) return null;
            _sinceMedian = 0;
            Array.Copy(_samples, _sorted, _count);
            Array.Sort(_sorted, 0, _count);
            return _sorted[_count / 2];
        }
    }

    private void LargeCollection(string? db, string? coll, long returned)
    {
        if (returned > _collMaxReturned.GetValueOrDefault((db, coll)))
            _collMaxReturned[(db, coll)] = returned;
    }

    /// <summary>Two event-time minutes of one <c>(app, command, coll)</c>: ops of two partitions interleave around a minute boundary, so one tumbling slot would reset on every switch (CA-19).</summary>
    private sealed class ErrorWindow
    {
        public struct Slot { public long Minute; public long Count, Errors; public string? LastFailure; }
        public Slot A = new() { Minute = -1 }, B = new() { Minute = -1 };
        /// <summary>The alert scope key of the last failure name seen: built once per name, not per failing op.</summary>
        public string? KeyFor, Key;
    }

    private void ErrorBurst(Operation op)
    {
        var r = op.Op;
        var k = (op.App, r.Command, r.Coll);
        if (!_errorWindows.TryGetValue(k, out var w)) _errorWindows[k] = w = new ErrorWindow();
        var minute = r.Ts / 60_000_000;
        ref var slot = ref (w.A.Minute == minute ? ref w.A : ref w.B.Minute == minute ? ref w.B : ref (w.A.Minute <= w.B.Minute ? ref w.A : ref w.B));
        if (slot.Minute != minute)
        {
            if (minute < slot.Minute) return; // older than both windows: its bucket is gone
            slot = new ErrorWindow.Slot { Minute = minute };
        }
        slot.Count++;
        if (op.Failed) { slot.Errors++; slot.LastFailure = op.FailureName; }
        // > 5 % with ≥ 20 ops; evaluated on each failure, and when the window reaches the volume floor
        // (the failures may all have come first)
        if ((op.Failed || slot.Count == 20) && slot.Count >= 20 && slot.Errors * 20 > slot.Count)
        {
            var codeName = op.Failed ? op.FailureName : slot.LastFailure;
            if (w.Key == null || w.KeyFor != codeName) { w.KeyFor = codeName; w.Key = $"{op.App}|{r.Command}|{r.Coll}|{codeName}"; }
            Hit("error-burst", w.Key, op, (op, slot.Errors, slot.Count, codeName),
                static s => { var d = Scope(s.op, app: true, coll: true); d["command"] = s.op.Op.Command; d["codeName"] = s.codeName; return d; },
                static s => $"{s.Errors} of {s.Count} {s.op.Op.Command} ops on {s.op.Ns} failed this minute ({s.codeName})");
        }
        if (_errorWindows.Count > 50_000) _errorWindows.Clear();
    }

    private void Abandoned(CursorInfo c, Operation? op, string why) =>
        Hit("cursor-abandoned", $"{c.App}|{c.ShapeHash}",
            new Dictionary<string, string?> { ["app"] = c.App, ["shapeHash"] = c.ShapeHash, ["coll"] = c.Coll },
            op, $"cursor on {c.Db}.{c.Coll} abandoned after {c.Documents} documents: {why}", tsOverride: c.ClosedTs);

    private static Dictionary<string, string?> Scope(Operation op, bool shape = false, bool app = false, bool coll = false)
    {
        var s = new Dictionary<string, string?>();
        if (shape) s["shapeHash"] = op.ShapeHash;
        if (app) s["app"] = op.App;
        if (coll) s["coll"] = op.Op.Coll;
        return s;
    }

    private void Hit(string rule, string scopeKey, Dictionary<string, string?> scope, Operation? op, string summary, long? tsOverride = null) =>
        Hit(rule, scopeKey, op, (scope, summary), static s => s.scope, static s => s.summary, tsOverride);

    /// <summary>
    /// An occurrence of <c>(rule, scope)</c>. <paramref name="scope"/> and <paramref name="summary"/> run only for a new alert, or for an
    /// open one whose summary is more than a second old: they are not built per occurrence (CA-18).
    /// </summary>
    private void Hit<T>(string rule, string scopeKey, Operation? op, T state, Func<T, Dictionary<string, string?>> scope, Func<T, string> summary, long? tsOverride = null)
    {
        var nowUs = _nowUs();
        var ts = tsOverride ?? op?.Ts ?? nowUs;
        var existing = Alerts.Find(rule, scopeKey);
        var text = existing is { Open: true } && nowUs - existing.SummaryAtUs < 1_000_000 ? existing.Summary : summary(state);
        if (Alerts.Hit(rule, scopeKey, existing?.Scope ?? scope(state), text, op?.Id, ts, nowUs) is not { } change) return;
        if (op != null && op.Alerts?.Contains(rule) != true)
            op.Alerts = op.Alerts == null ? [rule] : [.. op.Alerts, rule]; // copy on write: /ops/{id} reads a waiting op from an HTTP thread
        _live.Publish(change.Opened ? "alert.opened" : "alert.updated", null, change.Alert, static a => Json.Write(w => Json.Alert(w, a)));
        _dirtyAlerts.Add(change.Alert);
    }

    private readonly HashSet<Alert> _dirtyAlerts = [];

    private void TrackStream((string, string, long, int) key, Operation op)
    {
        if (_streams.Remove(key, out var old)) _streamOrder.Remove(old);
        _streams[key] = _streamOrder.AddLast((key, op));
        if (_streamOrder.Count > MaxStreams && _streamOrder.First is { } oldest) // the one idle the longest, not the oldest by opening
        {
            _streams.Remove(oldest.Value.Key);
            _streamOrder.RemoveFirst();
        }
    }

    private void OnStream(StreamRecord s)
    {
        var key = (s.Src, s.Member, s.RouteSid, s.RequestId);
        _streams.TryGetValue(key, out var node);
        var op = node?.Value.Op;
        var returned = s.NReturned ?? 0;
        CursorInfo? c = null;
        // an exhaust stream's batches are not getMores (none was sent); the last one (cursorId 0) closes the cursor and counts
        if (s.CursorId is { } cid && cid != 0)
            c = Cursors.Touched(s.Src, s.Member, cid, s.Ts, returned, getMore: false);
        else if (s.CursorId == 0 && op?.Op.Reply?.CursorId is { } opened and not 0)
            c = Cursors.Close(s.Src, s.Member, opened, "exhausted", s.Ts, returned: returned);
        if (c != null) LargeCollection(c.Db, c.Coll, c.Documents);
        if (op == null) return;
        if (s.CursorId == 0) { _streams.Remove(key); _streamOrder.Remove(node!); } // the stream ended (CI-22)
        else { _streamOrder.Remove(node!); _streamOrder.AddLast(node!); }
        op.ReplyCount++;
        if (op.Stored) _writer.Enqueue(new OpUpdate(op.Id, op.ReplyCount, op.Alerts?.ToList()));
        _live.Publish("op.updated", op);
    }

    /// <summary>
    /// Aggregates and alerts to the store. A failing store must not end the thread (CI-1): what could not be written stays dirty and
    /// goes with the next flush, and the failure shows in /healthz next to the writer's.
    /// </summary>
    private void Flush()
    {
        var (deltas, shapes) = Aggregates.TakeDirty();
        Exception? failed = null;
        if (deltas.Count > 0)
            try { _aggregates.Merge(deltas); }
            catch (Exception e) { failed = e; Aggregates.Requeue(deltas, null); }
        if (shapes.Count > 0)
            try { _aggregates.UpsertShapes(shapes); }
            catch (Exception e) { failed = e; Aggregates.Requeue(null, shapes); }
        if (_dirtyAlerts.Count > 0)
            try
            {
                _alertStore.Upsert(_dirtyAlerts.Select(a => a.Snapshot()).ToList());
                _dirtyAlerts.Clear();
            }
            catch (Exception e) { failed = e; }
        Alerts.Publish();
        StoreResult(failed);
    }

    private void StoreResult(Exception? failed)
    {
        if (failed == null)
        {
            Volatile.Write(ref StoreFailingSince, 0);
            return;
        }
        LastStoreError = $"{failed.GetType().Name}: {failed.Message}";
        if (Volatile.Read(ref StoreFailingSince) == 0)
        {
            Volatile.Write(ref StoreFailingSince, Environment.TickCount64);
            Console.Error.WriteLine($"analysis: store write failed, will retry: {LastStoreError}");
        }
    }

    internal void Housekeeping(long nowUs)
    {
        if (_touchedCallsites.Count > 0 && nowUs >= _nextCallsiteTouchUs) // 03 §7: callsites live while ops use them
        {
            _writer.Touch([.. _touchedCallsites]);
            _touchedCallsites.Clear();
            _nextCallsiteTouchUs = nowUs + 3_600_000_000;
        }
        Flush(); // the store must hold every delta before a minute closes
        foreach (var (shape, p95, basis) in Aggregates.CloseIdle(nowUs))
        {
            Aggregates.Shapes.TryGetValue(shape, out var si);
            Hit("slow-shape", shape, new Dictionary<string, string?> { ["shapeHash"] = shape }, null,
                $"p95 of {si?.Command} {si?.Db}.{si?.Coll} rose to {p95 / 1000:F1} ms, ≥ 3× the previous hour's {basis / 1000:F1} ms");
        }
        foreach (var c in Cursors.Expire(nowUs, _options.CursorTimeout))
            Abandoned(c, null, $"no getMore for {_options.CursorTimeout.TotalMinutes:F0} min (the server's cursor timeout)");
        foreach (var a in Alerts.ResolveQuiet(nowUs))
        {
            _dirtyAlerts.Add(a);
            _live.Publish("alert.updated", null, a, static x => Json.Write(w => Json.Alert(w, x)));
        }
        if (nowUs >= _nextRetentionUs)
        {
            _nextRetentionUs = nowUs + 60_000_000;
            Retention(nowUs);
        }
        Flush();
        Volatile.Write(ref ShapesTracked, Aggregates.Shapes.Count);
        Volatile.Write(ref ShapesEvicted, Aggregates.Evicted);
        // readers get snapshots (their own copies: the registry keeps mutating its cursors, 03 §5.4), rebuilt only when something changed
        if (Cursors.Version != _cursorsPublished)
        {
            _cursorsPublished = Cursors.Version;
            _openCursors = [.. Cursors.Open.Select(c => c.Snapshot())];
            _closedCursors = [.. Cursors.Closed.Select(c => c.Snapshot())];
            _cursorStats = Cursors.ByShape.ToDictionary(kv => kv.Key, kv => kv.Value.Snapshot());
        }
    }

    /// <summary>
    /// Once a minute (03 §5: bounded state, 03 §7 retention): resolved alerts 30 d after resolve and shapes unused for 30 d leave
    /// memory and the store; the catalog is capped, and per-shape state goes with its shape.
    /// </summary>
    private void Retention(long nowUs)
    {
        if (Alerts.Expire(nowUs, AlertRetention) is { } expired) _pendingAlertDeletes.AddRange(expired);
        if (_pendingAlertDeletes.Count > 0)
            try
            {
                _alertStore.Delete(_pendingAlertDeletes);
                _pendingAlertDeletes.Clear();
            }
            catch (Exception e) { StoreResult(e); }
        Aggregates.Expire(nowUs, ShapeRetention);
        Cursors.RetainShapes(Aggregates.Shapes);
        if (nowUs >= _nextShapePruneUs) // a scan of the shapes table: hourly
        {
            try
            {
                _aggregates.PruneShapes(nowUs - (long)ShapeRetention.TotalMicroseconds);
                _nextShapePruneUs = nowUs + 3_600_000_000;
            }
            catch (Exception e) { StoreResult(e); }
        }
        if (_collMaxReturned.Count > 100_000) _collMaxReturned.Clear();
    }
}
