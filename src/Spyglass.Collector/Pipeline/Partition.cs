using System.Collections.Concurrent;
using MongoDB.Bson;
using Spyglass.Collector.Model;
using Spyglass.Collector.Shapes;

namespace Spyglass.Collector.Pipeline;

/// <summary>A per-connection rule hit (03 §5.2), handed with its op to the analysis stage that owns alerts.</summary>
public readonly record struct RuleHit(string Rule, string ScopeKey, string Summary);

/// <summary>What one partition pass hands the analysis stage: one hand-off per ingested batch, not per record.</summary>
internal sealed class PartitionOutput
{
    /// <summary>New ops in arrival order. <c>Final</c>: nothing more will join it (store it now).</summary>
    public readonly List<(Operation Op, List<RuleHit>? Hits, bool Final)> Ops = [];
    public List<StreamRecord>? Streams;
    /// <summary>Ops that were waiting for context: joined (<c>Ctx</c> set) or not.</summary>
    public List<(Operation Op, CmdRecord? Ctx)>? Finals;
    public List<LateJoin>? LateJoins;
    public List<JoinConflict>? Conflicts;

    public bool IsEmpty => Ops.Count == 0 && Streams == null && Finals == null && LateJoins == null && Conflicts == null;
    public void Final(Operation op, CmdRecord? ctx) => (Finals ??= []).Add((op, ctx));
    public void Late(LateJoin j) => (LateJoins ??= []).Add(j);
    public void Conflict(JoinConflict c) => (Conflicts ??= []).Add(c);
}

/// <summary>
/// Stage 1 (03 §1): one worker per partition of <c>(proxyId, member, serverConnectionId)</c>, so per-connection
/// order holds and per-connection state needs no locks. Here: dedup (02 §6.4), the join (02 §6), enrichment,
/// BSON decode, shape normalization (the CPU-heavy part, hence parallel), and the per-connection rules.
/// </summary>
internal sealed class Partition
{
    /// <summary>Commands the per-connection rules look at: data access, not handshakes, auth or cursor housekeeping.</summary>
    private static readonly HashSet<string> DataCommands = ["find", "aggregate", "count", "distinct", "update", "delete", "insert", "findAndModify", "findandmodify"];

    private sealed class ConnState
    {
        public string? LastShape;
        public long LastRespLastAbs;
        public int Run;
        public readonly Dictionary<string, (long FirstTs, int Count)> Values = [];
        /// <summary>Values in first-seen order: expiry pops from the front, O(1) per op.</summary>
        public readonly Queue<(string Hash, long FirstTs)> ValueOrder = new();
    }

    private readonly CollectorOptions _options;
    private readonly ConnectionRegistry _conns;
    private readonly ProducerRegistry _producers;
    /// <summary>One hand-off per ingested batch, not per record: a per-item blocking queue cost a lock round trip per record.</summary>
    private readonly Action<PartitionOutput> _toAnalysis;
    private readonly Action<Record> _release;
    private readonly Func<long> _nowUs;
    private readonly Func<long, bool> _exportersActive;
    private readonly JoinCounters _joinCounters;
    private readonly JoinEngine _join;
    /// <summary>A batch (<c>List&lt;Record&gt;</c>) or a barrier (<c>TaskCompletionSource</c>).</summary>
    private readonly BlockingCollection<object> _queue = new(new ConcurrentQueue<object>());
    private readonly Dedup _dedup = new(1 << 18);
    private readonly Dictionary<(string, long), ConnState> _state = [];
    private readonly Queue<(string, long)> _stateOrder = new();
    private const int MaxConnState = 100_000, MaxValues = 256;
    private Thread? _thread;

    public long Duplicates, DecodeFailures, Processed;
    /// <summary>Environment.TickCount64 at the loop's last turn (at least every 100 ms when healthy).</summary>
    public long LastBeat = Environment.TickCount64;
    public bool Alive => _thread?.IsAlive == true;

    public Partition(CollectorOptions options, ConnectionRegistry conns, ProducerRegistry producers, JoinCounters joinCounters,
        Action<PartitionOutput> toAnalysis, Action<Record> release, Func<long> nowUs, Func<long, bool> exportersActive)
    {
        _options = options;
        _conns = conns;
        _producers = producers;
        _joinCounters = joinCounters;
        _toAnalysis = toAnalysis;
        _release = release;
        _nowUs = nowUs;
        _exportersActive = exportersActive;
        _join = new JoinEngine(joinCounters, producers, Math.Max(1, options.JoinMaxWaitingOps / options.Workers),
            Math.Max(1, options.JoinMaxWaitingCmds / options.Workers), options.JoinFinalizedCapacity);
    }

    internal JoinEngine Join => _join;

    public void Start(int index)
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = $"collector-partition-{index}" };
        _thread.Start();
    }

    /// <summary>This partition's share of one ingested batch, in arrival order.</summary>
    public void Post(List<Record> batch) => _queue.Add(batch);

    /// <summary>Completes once everything posted before it is processed and handed on, and expiry has run at the current clock.</summary>
    public Task Barrier()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Add(tcs);
        return tcs.Task;
    }

    public void Complete() => _queue.CompleteAdding();

    public void JoinThread() => _thread?.Join();

    private void Loop()
    {
        while (!_queue.IsCompleted)
        {
            // wake at least every 100 ms: waiting ops and cmds expire on the clock, not on traffic
            _queue.TryTake(out var item, 100);
            Volatile.Write(ref LastBeat, Environment.TickCount64);
            var output = new PartitionOutput();
            var now = _nowUs();
            // expiry first: whatever ran out by now is final before records arriving now are matched against it
            _join.Expire(now, output);
            if (item is List<Record> batch)
            {
                foreach (var r in batch)
                {
                    var forwarded = false; // a forwarded record keeps its queue charge until the analysis stage is done with it
                    try
                    {
                        switch (r)
                        {
                            case OpRecord op:
                                forwarded = OnOp(op, now, output);
                                break;
                            case CmdRecord c:
                                OnCmd(c, now, output);
                                break;
                            case StreamRecord s:
                                if (!_dedup.Seen(Dedup.Key(s.Src, "stream", s.Member, s.ServerConnectionId ?? -1, s.RequestId, s.Ts)))
                                {
                                    (output.Streams ??= []).Add(s);
                                    forwarded = true;
                                }
                                break;
                        }
                    }
                    catch (Exception e)
                    {
                        Console.Error.WriteLine($"partition: {e.GetType().Name}: {e.Message}");
                    }
                    finally
                    {
                        if (!forwarded) _release(r);
                    }
                }
                Interlocked.Add(ref Processed, batch.Count);
            }
            // ops first: a stream record never precedes its op in a proxy's output, so this keeps causality
            if (!output.IsEmpty) _toAnalysis(output);
            (item as TaskCompletionSource)?.TrySetResult();
        }
        var last = new PartitionOutput();
        _join.FlushAll(last);
        if (!last.IsEmpty) _toAnalysis(last);
    }

    private void OnCmd(CmdRecord c, long now, PartitionOutput output)
    {
        var key = c.Key!.Value; // keyless cmds are orphans at ingest and never routed here
        if (_dedup.Seen(Dedup.Key(c.Src, "cmd", key.Member, key.ServerConnectionId, key.RequestId, c.Ts)))
        {
            Interlocked.Increment(ref _joinCounters.Duplicates);
            return;
        }
        _join.OnCmd(c, key, now, output);
    }

    /// <summary>Returns whether the op went to the analysis stage (it then keeps its queue charge until analysed).</summary>
    private bool OnOp(OpRecord r, long now, PartitionOutput output)
    {
        if (Enrich(r) is not { } enriched) return false;
        var (op, hits) = enriched;
        // 02 §6.2: wait for context while some exporter is reporting at all; with none, nothing can join in the
        // wait, and a cmd that comes later still merges into the stored op. Exporters never report the
        // handshake and monitoring hellos (02 §3.3).
        long wait = 0;
        var longWait = false;
        if (_exportersActive(now) && !IsHello(r.Command))
        {
            longWait = _conns.Get(r.Src, r.ConnSeq)?.Exporter != null;
            wait = longWait ? _options.JoinWaitExporterUs : _options.JoinWaitUs;
        }
        var final = _join.OnOp(op, now, wait, longWait, output);
        output.Ops.Add((op, hits, final));
        return true;
    }

    private static bool IsHello(string command) => command is "hello" or "isMaster" or "ismaster";

    private (Operation, List<RuleHit>?)? Enrich(OpRecord r)
    {
        if (_dedup.Seen(Dedup.Key(r.Src, "op", r.Member, r.ServerConnectionId ?? -r.ConnSeq, r.RequestId, r.Ts)))
        {
            Interlocked.Increment(ref Duplicates);
            return null;
        }
        var op = new Operation { Id = Ulid.New(r.Ts / 1000), Op = r, ReplyCount = r.HasReply ? 1 : 0 };
        if (_conns.Get(r.Src, r.ConnSeq) is { } conn)
        {
            op.App = conn.App ?? conn.Driver;
            op.Driver = conn.Driver;
        }

        BsonDocument? cmd = null;
        if (r.Cmd != null)
        {
            try { cmd = FastBson.Decode(r.Cmd); }
            catch (FormatException) { Interlocked.Increment(ref DecodeFailures); }
        }
        if (cmd != null)
        {
            if (r.Command == "getMore" && cmd.TryGetValue("getMore", out var gm) && gm.IsNumeric) op.GetMoreCursorId = gm.ToInt64();
            if (r.Command == "killCursors" && cmd.TryGetValue("cursors", out var ids) && ids is BsonArray a)
                op.KillCursorIds = a.Where(v => v.IsNumeric).Select(v => v.ToInt64()).ToList();
        }

        var valuesPreserved = _producers.Get(r.Src)?.ValuesPreserved ?? true;
        var shape = ShapeNormalizer.Normalize(r.Command, r.Db, r.Coll, cmd, r.CmdTruncated, valuesPreserved);
        op.ShapeHash = shape.ShapeHash;
        op.ValueHash = shape.ValueHash;
        op.ShapeJson = shape.ShapeJson;
        op.InCardinality = shape.InCardinality;
        op.Unbounded = cmd != null && IsUnbounded(r.Command, cmd);
        op.AwaitsByDesign = AwaitsByDesign(r.Command, cmd);

        return (op, DataCommands.Contains(r.Command) ? ConnectionRules(op) : null);
    }

    internal static bool AwaitsByDesign(string command, BsonDocument? cmd)
    {
        if (command is "hello" or "isMaster" or "ismaster") return true;
        if (cmd == null) return false;
        if (cmd.TryGetValue("tailable", out var t) && t.ToBoolean() || cmd.TryGetValue("awaitData", out var a) && a.ToBoolean()) return true;
        return command == "aggregate" && cmd.TryGetValue("pipeline", out var p) && p is BsonArray { Count: > 0 } stages &&
               stages[0] is BsonDocument { ElementCount: > 0 } first && first.GetElement(0).Name == "$changeStream";
    }

    /// <summary>03 §5.2 unbounded-scan input: no filter and no limit, or an aggregate without a leading $match/$limit.</summary>
    internal static bool IsUnbounded(string command, BsonDocument cmd)
    {
        static bool Empty(BsonDocument c, string f) => !c.TryGetValue(f, out var v) || v is BsonDocument { ElementCount: 0 };
        static bool NoLimit(BsonDocument c) => !c.TryGetValue("limit", out var l) || (l.IsNumeric && l.ToInt64() == 0);
        return command switch
        {
            "find" => Empty(cmd, "filter") && NoLimit(cmd),
            "count" => Empty(cmd, "query") && NoLimit(cmd),
            "aggregate" => cmd.TryGetValue("pipeline", out var p) && p is BsonArray stages &&
                           (stages.Count == 0 || stages[0] is not BsonDocument { ElementCount: > 0 } first ||
                            first.GetElement(0).Name is not ("$match" or "$limit" or "$collStats" or "$indexStats" or "$currentOp")),
            _ => false,
        };
    }

    /// <summary>n-plus-one-suspected and repeated-query (suspected) on one connection (03 §5.2).</summary>
    private List<RuleHit>? ConnectionRules(Operation op)
    {
        var r = op.Op;
        var key = (r.Src, r.ConnSeq);
        if (!_state.TryGetValue(key, out var st))
        {
            if (_state.Count >= MaxConnState)
                _state.Remove(_stateOrder.Dequeue());
            _state[key] = st = new ConnState();
            _stateOrder.Enqueue(key);
        }
        List<RuleHit>? hits = null;

        // tight loop: same shape, each starting within G of the previous reply's last byte
        if (st.LastShape == op.ShapeHash && r.Ts - st.LastRespLastAbs <= _options.NPlusOneGapUs) st.Run++;
        else st.Run = 1;
        st.LastShape = op.ShapeHash;
        st.LastRespLastAbs = r.Ts + (r.RespLastUs ?? r.ReqLastUs);
        if (st.Run == _options.NPlusOneCount)
            (hits ??= []).Add(new RuleHit("n-plus-one-suspected", $"{op.App}|{op.ShapeHash}",
                $"{st.Run}+ {r.Command} ops on {op.Ns} of one shape back to back on one connection (suspected: no unit-of-work context)"));

        // the same values ≥ M times within 1 s on one connection
        if (op.ValueHash is { } vh)
        {
            // expire from the front: entries older than 1 s, and never more than MaxValues per connection
            while (st.ValueOrder.TryPeek(out var oldest) && (r.Ts - oldest.FirstTs > 1_000_000 || st.ValueOrder.Count >= MaxValues))
            {
                st.ValueOrder.Dequeue();
                if (st.Values.TryGetValue(oldest.Hash, out var cur) && cur.FirstTs == oldest.FirstTs) st.Values.Remove(oldest.Hash);
            }
            var (first, n) = st.Values.TryGetValue(vh, out var e) && r.Ts - e.FirstTs <= 1_000_000 ? e : (r.Ts, 0);
            if (n == 0) st.ValueOrder.Enqueue((vh, first));
            st.Values[vh] = (first, ++n);
            // scoped by shape, not by value: one alert per repeated value floods the list (03 §5.2 note)
            if (n == _options.RepeatedQueryCount)
                (hits ??= []).Add(new RuleHit("repeated-query", $"{op.App}|{op.ShapeHash}",
                    $"the same {r.Command} on {op.Ns} with the same values {n}× within 1 s on one connection (suspected)"));
        }
        return hits;
    }
}

/// <summary>
/// 02 §6.4 duplicate suppression by <c>(src, type, joinKey, ts)</c>, bounded by count rather than the full
/// 10-minute window: 64-bit key hashes in a ring of 2^18 per partition (~8 MB). At the 03 §11 rate split
/// over two partitions that is ~10 s; below ~440 records/s per partition it is the whole window.
/// Producers resend within seconds (02 §5: one retry after 1 s).
/// </summary>
internal sealed class Dedup(int capacity)
{
    private readonly HashSet<ulong> _set = new(capacity);
    private readonly ulong[] _ring = new ulong[capacity];
    private int _next;
    private bool _full;

    public static ulong Key(string src, string type, string member, long sid, int requestId, long ts)
    {
        var h = new HashCode();
        h.Add(src); h.Add(type); h.Add(member); h.Add(sid); h.Add(requestId); h.Add(ts);
        var lo = (uint)h.ToHashCode();
        var h2 = new HashCode();
        h2.Add(ts); h2.Add(requestId); h2.Add(sid); h2.Add(member); h2.Add(type); h2.Add(src);
        return ((ulong)(uint)h2.ToHashCode() << 32) | lo;
    }

    /// <summary>True if the key was seen; otherwise remembers it.</summary>
    public bool Seen(ulong key)
    {
        if (!_set.Add(key)) return true;
        if (_full) _set.Remove(_ring[_next]);
        _ring[_next] = key;
        if (++_next == _ring.Length) { _next = 0; _full = true; }
        return false;
    }
}
