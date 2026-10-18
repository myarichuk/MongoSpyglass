using System.Collections.Concurrent;
using Spyglass.Collector.Api;
using Spyglass.Collector.Model;
using Spyglass.Collector.Pipeline;
using Spyglass.Collector.Storage;

namespace Spyglass.Collector.Analysis;

/// <summary>
/// Stage 2 (03 §1, §5, §6): one thread owns everything that crosses connections, so it needs no locks:
/// cursors (getMore arrives on any pooled connection), shape aggregates, the global rules and alerts,
/// live fan-out and the hand-off to storage. Each step is O(1) per op.
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
    private readonly Action<Record> _release;

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
    private readonly HashSet<string> _touchedCallsites = [];
    private long _nextCallsiteTouchUs;
    private readonly BlockingCollection<Item> _queue = new(new ConcurrentQueue<Item>());
    private readonly Dictionary<(string, string, long, int), Operation> _streams = [];
    private readonly Queue<(string, string, long, int)> _streamOrder = new();
    private readonly Dictionary<(string?, string?), long> _collMaxReturned = [];
    private readonly Dictionary<(string?, string, string?), (long Minute, long Count, long Errors, string? LastFailure)> _errorWindows = [];
    private readonly Func<long> _nowUs;
    private Thread? _thread;
    private volatile CursorInfo[] _openCursors = [];
    private volatile CursorInfo[] _closedCursors = [];
    private volatile Dictionary<string, CursorShapeStats> _cursorStats = [];

    public readonly AlertBook Alerts = new();
    public readonly CursorRegistry Cursors = new();
    public readonly AggregateBook Aggregates = new();
    public long Processed;

    public AnalysisStage(CollectorOptions options, StorageWriter writer, IAggregateStore aggregates, IAlertStore alertStore,
        IOperationStore ops, LiveHub live, ProducerRegistry producers, Action<Record> release, Func<long> nowUs)
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
        foreach (var a in alertStore.All()) Alerts.Restore(a);
        foreach (var s in aggregates.Shapes()) Aggregates.Restore(s);
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
            if (_queue.TryTake(out var item, 100))
            {
                if (item is BatchItem { Output: var b })
                {
                    foreach (var (op, hits, final) in b.Ops)
                    {
                        try
                        {
                            OnOp(op, hits);
                            if (final) Finalize(op);
                            else _waiting[op.Id] = op;
                        }
                        catch (Exception e) { Console.Error.WriteLine($"analysis: {e.GetType().Name}: {e.Message}"); }
                        finally { _release(op.Op); }
                    }
                    if (b.Streams != null)
                        foreach (var s in b.Streams)
                        {
                            try { OnStream(s); }
                            catch (Exception e) { Console.Error.WriteLine($"analysis: {e.GetType().Name}: {e.Message}"); }
                            finally { _release(s); }
                        }
                    try { OnJoins(b); }
                    catch (Exception e) { Console.Error.WriteLine($"analysis: {e.GetType().Name}: {e.Message}"); }
                    Interlocked.Add(ref Processed, b.Ops.Count);
                }
                else if (item is CommandItem(var run))
                {
                    run();
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
                Housekeeping(_nowUs());
                nextHousekeeping = now + 1000;
                _tickRequested = false;
            }
        }
        Flush();
    }

    private void OnOp(Operation op, List<RuleHit>? connectionHits)
    {
        var r = op.Op;

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
            foreach (var id in op.KillCursorIds) Cursors.Close(r.ProxyId, r.Member, id, "killed", r.Ts);

        if (r.Stream)
        {
            var key = (r.ProxyId, r.Member, r.ServerConnectionId ?? -1, r.RequestId); // stream records carry no connSeq
            _streams[key] = op;
            _streamOrder.Enqueue(key);
            if (_streamOrder.Count > 10_000) _streams.Remove(_streamOrder.Dequeue());
        }

        Aggregates.Add(op);

        // ---- global rules (03 §5.2, wire-only) ----
        var nr = r.Reply?.NReturned ?? 0;
        LargeCollection(r.Db, r.Coll, nr);

        if (r.ServerLatencyUs > _options.SlowShapeUs && !op.AwaitsByDesign)
            Hit("slow-shape", op.ShapeHash, Scope(op, shape: true), op,
                $"{r.Command} {op.Ns} took {r.ServerLatencyUs / 1000.0:F1} ms on the server (> {_options.SlowShapeUs / 1000} ms)");
        if (r.Command != "getMore" && (r.RespBytes > 1 << 20 || nr > 1000))
            Hit("large-result", op.ShapeHash, Scope(op, shape: true), op,
                $"{r.Command} {op.Ns} returned {nr} documents / {r.RespBytes / 1024} KiB in one reply");
        if (op.InCardinality > 1000)
            Hit("unbounded-in", op.ShapeHash, Scope(op, shape: true), op, $"$in with {op.InCardinality} values on {op.Ns}");
        if (op.Unbounded && _collMaxReturned.GetValueOrDefault((r.Db, r.Coll)) > 1000)
            Hit("unbounded-scan", $"{op.App}|{op.ShapeHash}", Scope(op, shape: true, app: true), op,
                $"{r.Command} on {op.Ns} with no filter and no limit, on a collection that has returned > 1000 documents");
        if (!r.Ack && Writes.Contains(r.Command))
            Hit("unacknowledged-writes", $"{op.App}|{r.Coll}", Scope(op, app: true, coll: true), op,
                $"unacknowledged (w:0) {r.Command} on {op.Ns}: failures are invisible to the application");
        ErrorBurst(op);

        if (connectionHits != null)
            foreach (var h in connectionHits)
            {
                Hit(h.Rule, h.ScopeKey, Scope(op, shape: true, app: true), op, h.Summary);
            }

        if (op.Ctx != null) Context(op);
        if (_live.Subscribers > 0) // nobody watching: no JSON per op
            _live.Publish(new LiveEvent("op.created", Json.Write(w => Json.OpSummary(w, op)), op));
    }

    /// <summary>Nothing more will join this op: store it.</summary>
    private void Finalize(Operation op)
    {
        op.Stored = true;
        _writer.Enqueue(op);
    }

    private void OnJoins(PartitionOutput b)
    {
        if (b.Finals != null)
            foreach (var (op, ctx) in b.Finals)
            {
                _waiting.TryRemove(op.Id, out _);
                if (ctx != null)
                {
                    Volatile.Write(ref op.Ctx, ctx);
                    Context(op);
                    if (_live.Subscribers > 0) // 03 §3.2: the live row is patched in place
                        _live.Publish(new LiveEvent("op.updated", Json.Write(w => Json.OpSummary(w, op)), op));
                }
                Finalize(op);
            }
        if (b.LateJoins != null)
            foreach (var j in b.LateJoins)
            {
                _writer.Enqueue(new OpUpdate(j.OpId, Ctx: j.Cmd)); // the writer publishes op.updated once it is applied
                Skew(j.Cmd, j.OpTs, j.OpId);
                if (j.Cmd.Callsite is { } cs) _touchedCallsites.Add(cs);
            }
        if (b.Conflicts != null)
            foreach (var c in b.Conflicts)
                Hit("exporter-skew", c.Cmd.Src, new Dictionary<string, string?> { ["src"] = c.Cmd.Src }, null,
                    $"join conflict: exporter {c.Cmd.Src} reported {c.Cmd.Command} on {c.Cmd.Db} for request {c.Cmd.RequestId}, " +
                    $"the proxy saw {c.OpCommand} on {c.OpDb} (02 §6.3)", tsOverride: c.Cmd.Ts);
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
        if (!_skew.TryGetValue(c.Src, out var w)) _skew[c.Src] = w = new SkewWindow();
        if (w.Add(c.Ts - opTs) is not { } median) return;
        if (_producers.Get(c.Src) is { } p) p.SkewUs = median;
        if (Math.Abs(median) > 1_000_000)
            Hit("exporter-skew", c.Src, new Dictionary<string, string?> { ["src"] = c.Src }, null,
                $"exporter {c.Src} clock is {median / 1000.0:F0} ms {(median > 0 ? "ahead of" : "behind")} the proxy's (median of the last {SkewWindow.Size} joins)",
                tsOverride: c.Ts);
    }

    private sealed class SkewWindow
    {
        public const int Size = 64;
        private readonly long[] _samples = new long[Size];
        private readonly long[] _sorted = new long[Size];
        private int _n;

        /// <summary>Adds a sample; every 16th returns the median of the window.</summary>
        public long? Add(long v)
        {
            _samples[_n++ % Size] = v;
            if (_n % 16 != 0) return null;
            var count = Math.Min(_n, Size);
            Array.Copy(_samples, _sorted, count);
            Array.Sort(_sorted, 0, count);
            return _sorted[count / 2];
        }
    }

    private void LargeCollection(string? db, string? coll, long returned)
    {
        if (returned > _collMaxReturned.GetValueOrDefault((db, coll)))
            _collMaxReturned[(db, coll)] = returned;
    }

    private void ErrorBurst(Operation op)
    {
        var r = op.Op;
        var key = (op.App, r.Command, r.Coll);
        var minute = r.Ts / 60_000_000;
        var (m, n, e, last) = _errorWindows.GetValueOrDefault(key);
        if (m != minute) (m, n, e, last) = (minute, 0, 0, null);
        n++;
        if (op.Failed) { e++; last = op.FailureName; }
        _errorWindows[key] = (m, n, e, last);
        // > 5 % with ≥ 20 ops; evaluated on each failure, and when the window reaches the volume floor
        // (the failures may all have come first)
        if ((op.Failed || n == 20) && n >= 20 && e * 20 > n)
        {
            var codeName = op.Failed ? op.FailureName : last;
            var scope = Scope(op, app: true, coll: true);
            scope["command"] = r.Command;
            scope["codeName"] = codeName;
            Hit("error-burst", $"{op.App}|{r.Command}|{r.Coll}|{codeName}", scope, op,
                $"{e} of {n} {r.Command} ops on {op.Ns} failed this minute ({codeName})");
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

    private void Hit(string rule, string scopeKey, Dictionary<string, string?> scope, Operation? op, string summary, long? tsOverride = null)
    {
        var ts = tsOverride ?? op?.Ts ?? _nowUs();
        if (Alerts.Hit(rule, scopeKey, scope, summary, op?.Id, ts) is not { } change) return;
        if (op != null)
        {
            op.Alerts ??= [];
            if (!op.Alerts.Contains(rule)) op.Alerts.Add(rule);
        }
        _live.Publish(new LiveEvent(change.Opened ? "alert.opened" : "alert.updated", Json.Write(w => Json.Alert(w, change.Alert)), null));
        _dirtyAlerts.Add(change.Alert);
    }

    private readonly HashSet<Alert> _dirtyAlerts = [];

    private void OnStream(StreamRecord s)
    {
        if (s.CursorId is { } cid && cid != 0)
            Cursors.Touched(s.Src, s.Member, cid, s.Ts, s.NReturned ?? 0);
        if (!_streams.TryGetValue((s.Src, s.Member, s.ServerConnectionId ?? -1, s.RequestId), out var op))
            return;
        op.ReplyCount++;
        if (s.CursorId == 0 && op.Op.Reply?.CursorId is { } opened and not 0)
            Cursors.Close(s.Src, s.Member, opened, "exhausted", s.Ts);
        if (op.Stored) _writer.Enqueue(new OpUpdate(op.Id, op.ReplyCount, op.Alerts?.ToList()));
        if (_live.Subscribers > 0)
            _live.Publish(new LiveEvent("op.updated", Json.Write(w => Json.OpSummary(w, op)), op));
    }

    private void Flush()
    {
        var (deltas, shapes) = Aggregates.TakeDirty();
        if (deltas.Count > 0) _aggregates.Merge(deltas);
        if (shapes.Count > 0) _aggregates.UpsertShapes(shapes);
        if (_dirtyAlerts.Count > 0)
        {
            _alertStore.Upsert(_dirtyAlerts.Select(a => a.Snapshot()).ToList());
            _dirtyAlerts.Clear();
        }
        Alerts.Publish();
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
        foreach (var (shape, p95, basis) in Aggregates.CloseBefore(nowUs / 60_000_000 - 1))
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
            _live.Publish(new LiveEvent("alert.updated", Json.Write(w => Json.Alert(w, a)), null));
        }
        Alerts.Expire(nowUs, TimeSpan.FromDays(30));
        Flush();
        _openCursors = [.. Cursors.Open];
        _closedCursors = [.. Cursors.Closed];
        _cursorStats = Cursors.ByShape.ToDictionary(kv => kv.Key, kv => kv.Value);
    }
}
