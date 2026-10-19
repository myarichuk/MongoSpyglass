using System.Runtime.InteropServices;
using Spyglass.Collector.Model;

namespace Spyglass.Collector.Pipeline;

/// <summary>02 §6 outcomes, summed over partitions (03 §8 <c>/metrics</c>).</summary>
public sealed class JoinCounters
{
    /// <summary>Joined before the op was stored: in the same partition pass, or while the op waited.</summary>
    public long Joined;
    /// <summary>Merged into an op already handed to storage (02 §6.2, within 10 minutes).</summary>
    public long JoinedLate;
    /// <summary>02 §6.3: same key, different command or db; neither record joined.</summary>
    public long Conflicts;
    /// <summary>02 §3.3 / §6.5: the exporter could not build the key (<c>proxyId</c>, <c>member</c> or <c>serverConnectionId</c> null).</summary>
    public long OrphanNoKey;
    /// <summary>02 §6.5: no op within 30 s (or pushed out of the bounded waiting set).</summary>
    public long OrphanExpired;
    /// <summary>02 §6.2: the op was stored more than 10 minutes before its cmd arrived.</summary>
    public long LateDiscarded;
    /// <summary>02 §6.1: a second cmd for a key that already joined one.</summary>
    public long AlreadyJoined;
    /// <summary>02 §6.4: the same cmd again (src, key, ts).</summary>
    public long Duplicates;
    /// <summary>Ops finalized without context after waiting for it.</summary>
    public long WaitedWireOnly;
    /// <summary>Ops finalized early because the waiting set was full (03 §11: join state is bounded).</summary>
    public long EvictedOps;
    public long PendingOps, PendingCmds;
}

/// <summary>Context for an op that storage already has: applied as an update, in order, by the storage writer.</summary>
internal sealed record LateJoin(string OpId, CmdRecord Cmd, long OpTs, long OpEndUs);

/// <summary>A 02 §6.3 conflict: the tripwire for a key collision (or an exporter bug).</summary>
internal readonly record struct JoinConflict(CmdRecord Cmd, string OpCommand, string? OpDb);

/// <summary>
/// One partition's join state (03 §3.1). Single-threaded: the partition that owns <c>(proxyId, member,
/// serverConnectionId)</c> owns every key under it, so no locks.
/// <list type="bullet">
/// <item>ops waiting for their cmd (2 s, or 10 s on a connection with an exporter marker), already analysed and
/// live, not yet stored;</item>
/// <item>cmds waiting for their op (30 s);</item>
/// <item>a bounded index of finalized ops, so a late cmd merges into the stored op (10 minutes, bounded by memory
/// like dedup, 02 §6.4).</item>
/// </list>
/// Everything is a struct in a dictionary plus a FIFO of keys: nothing allocated per op beyond the entries.
/// </summary>
internal sealed class JoinEngine
{
    public static readonly long CmdWaitUs = 30_000_000, LateWindowUs = 600_000_000;

    private readonly struct Waiting<T>(T item, long arrivedUs, long expiresUs)
    {
        public readonly T Item = item;
        public readonly long ArrivedUs = arrivedUs, ExpiresUs = expiresUs;
    }

    private struct Finalized
    {
        public string OpId, Command;
        public string? Db;
        public long OpTs, OpEndUs, ArrivedUs;
        public bool Joined;
        public int Slot;
    }

    private readonly JoinCounters _counters;
    private readonly ProducerRegistry _producers;
    private readonly int _maxOps, _maxCmds;
    private readonly Dictionary<JoinKey, Waiting<Operation>> _ops = [];
    // one FIFO per wait length: each is then ordered by expiry
    private readonly Queue<(JoinKey, Operation)> _opsShort = new(), _opsLong = new();
    private readonly Dictionary<JoinKey, Waiting<CmdRecord>> _cmds = [];
    private readonly Queue<(JoinKey, CmdRecord)> _cmdOrder = new();
    private readonly Dictionary<JoinKey, Finalized> _done;
    private readonly JoinKey[] _doneRing;
    private int _doneNext;
    private bool _doneFull;

    public JoinEngine(JoinCounters counters, ProducerRegistry producers, int maxOps, int maxCmds, int finalizedCapacity)
    {
        _counters = counters;
        _producers = producers;
        _maxOps = maxOps;
        _maxCmds = maxCmds;
        _done = new Dictionary<JoinKey, Finalized>(Math.Min(finalizedCapacity, 1 << 16));
        _doneRing = new JoinKey[finalizedCapacity];
    }

    public int WaitingOps => _ops.Count;
    public int WaitingCmds => _cmds.Count;

    /// <summary>
    /// An op arrives (already enriched, not yet shared). Joins a waiting cmd if there is one. Returns true if the op
    /// is final now; false if it waits (<paramref name="waitUs"/> &gt; 0) and will come out of <see cref="Expire"/>
    /// or <see cref="OnCmd"/>.
    /// </summary>
    public bool OnOp(Operation op, long nowUs, long waitUs, bool longWait, PartitionOutput output)
    {
        if (op.Key is not { } key) return true; // no server connection id: nothing can name it (02 §3.3)
        if (_cmds.Remove(key, out var w))
        {
            Interlocked.Decrement(ref _counters.PendingCmds);
            if (Agree(w.Item, op.Op.Command, op.Op.Db))
            {
                op.Ctx = w.Item; // not yet shared with another thread
                Joined(w.Item, late: false);
            }
            else Conflict(w.Item, op.Op.Command, op.Op.Db, output);
            Remember(key, op, nowUs, joined: true);
            return true;
        }
        if (waitUs <= 0)
        {
            Remember(key, op, nowUs, joined: false);
            return true;
        }
        if (_ops.ContainsKey(key))
        {
            // the same key twice (a resend with another ts): the first one is the op; this one goes as is
            Remember(key, op, nowUs, joined: true);
            return true;
        }
        while (_ops.Count >= _maxOps && EvictOldestOp(output)) { }
        _ops[key] = new Waiting<Operation>(op, nowUs, nowUs + waitUs);
        (longWait ? _opsLong : _opsShort).Enqueue((key, op));
        Interlocked.Increment(ref _counters.PendingOps);
        return false;
    }

    /// <summary>A cmd with a full key arrives (orphans without one never get here).</summary>
    public void OnCmd(CmdRecord c, JoinKey key, long nowUs, PartitionOutput output)
    {
        if (_ops.Remove(key, out var w))
        {
            Interlocked.Decrement(ref _counters.PendingOps);
            var op = w.Item;
            if (Agree(c, op.Op.Command, op.Op.Db))
            {
                Joined(c, late: false);
                output.Final(op, c);
            }
            else
            {
                Conflict(c, op.Op.Command, op.Op.Db, output);
                output.Final(op, null); // 02 §6.3: kept unjoined
            }
            Remember(key, op, w.ArrivedUs, joined: true);
            return;
        }

        ref var f = ref CollectionsMarshal.GetValueRefOrNullRef(_done, key);
        if (!System.Runtime.CompilerServices.Unsafe.IsNullRef(ref f))
        {
            if (f.Joined) { Interlocked.Increment(ref _counters.AlreadyJoined); return; }
            f.Joined = true;
            if (!Agree(c, f.Command, f.Db)) { Conflict(c, f.Command, f.Db, output); return; }
            if (nowUs - f.ArrivedUs > LateWindowUs) { Interlocked.Increment(ref _counters.LateDiscarded); Orphan(c); return; }
            Joined(c, late: true);
            output.Late(new LateJoin(f.OpId, c, f.OpTs, f.OpEndUs));
            return;
        }

        if (_cmds.ContainsKey(key)) { Interlocked.Increment(ref _counters.AlreadyJoined); return; }
        while (_cmds.Count >= _maxCmds && _cmdOrder.TryDequeue(out var old))
            if (_cmds.Remove(old.Item1, out var ow) && ReferenceEquals(ow.Item, old.Item2)) ExpiredCmd(old.Item2);
        _cmds[key] = new Waiting<CmdRecord>(c, nowUs, nowUs + CmdWaitUs);
        _cmdOrder.Enqueue((key, c));
        Interlocked.Increment(ref _counters.PendingCmds);
    }

    /// <summary>Finalizes ops whose wait ran out (wire-only) and orphans cmds older than 30 s.</summary>
    public void Expire(long nowUs, PartitionOutput output)
    {
        ExpireOps(_opsShort, nowUs, output);
        ExpireOps(_opsLong, nowUs, output);
        while (_cmdOrder.TryPeek(out var head))
        {
            if (!_cmds.TryGetValue(head.Item1, out var w) || !ReferenceEquals(w.Item, head.Item2)) { _cmdOrder.Dequeue(); continue; }
            if (w.ExpiresUs > nowUs) break;
            _cmdOrder.Dequeue();
            _cmds.Remove(head.Item1);
            ExpiredCmd(head.Item2);
        }
    }

    /// <summary>Shutdown: every waiting op is stored as it is.</summary>
    public void FlushAll(PartitionOutput output)
    {
        foreach (var (key, w) in _ops.ToList()) FinalizeWaiting(key, w, output);
        _opsShort.Clear();
        _opsLong.Clear();
    }

    private void ExpireOps(Queue<(JoinKey, Operation)> q, long nowUs, PartitionOutput output)
    {
        while (q.TryPeek(out var head))
        {
            if (!_ops.TryGetValue(head.Item1, out var w) || !ReferenceEquals(w.Item, head.Item2)) { q.Dequeue(); continue; }
            if (w.ExpiresUs > nowUs) break;
            q.Dequeue();
            FinalizeWaiting(head.Item1, w, output);
            Interlocked.Increment(ref _counters.WaitedWireOnly);
        }
    }

    /// <summary>The waiting set is full: the op closest to its deadline goes now, wire-only (counted).</summary>
    private bool EvictOldestOp(PartitionOutput output)
    {
        Skip(_opsShort);
        Skip(_opsLong);
        var q = _opsShort.Count == 0 ? _opsLong
            : _opsLong.Count == 0 ? _opsShort
            : _ops[_opsShort.Peek().Item1].ExpiresUs <= _ops[_opsLong.Peek().Item1].ExpiresUs ? _opsShort : _opsLong;
        if (!q.TryDequeue(out var head)) return false;
        FinalizeWaiting(head.Item1, _ops[head.Item1], output);
        Interlocked.Increment(ref _counters.EvictedOps);
        return true;

        void Skip(Queue<(JoinKey, Operation)> x)
        {
            while (x.TryPeek(out var h) && (!_ops.TryGetValue(h.Item1, out var w) || !ReferenceEquals(w.Item, h.Item2))) x.Dequeue();
        }
    }

    private void FinalizeWaiting(JoinKey key, Waiting<Operation> w, PartitionOutput output)
    {
        _ops.Remove(key);
        Interlocked.Decrement(ref _counters.PendingOps);
        output.Final(w.Item, null);
        Remember(key, w.Item, w.ArrivedUs, joined: false);
    }

    private void ExpiredCmd(CmdRecord c)
    {
        Interlocked.Decrement(ref _counters.PendingCmds);
        Interlocked.Increment(ref _counters.OrphanExpired);
        Orphan(c);
    }

    private void Orphan(CmdRecord c)
    {
        if (_producers.Get(c.Src) is { } p) Interlocked.Increment(ref p.Orphans);
    }

    private void Joined(CmdRecord c, bool late)
    {
        if (late) Interlocked.Increment(ref _counters.JoinedLate);
        else Interlocked.Increment(ref _counters.Joined);
        if (_producers.Get(c.Src) is { } p) Interlocked.Increment(ref p.Joined);
    }

    private void Conflict(CmdRecord c, string command, string? db, PartitionOutput output)
    {
        Interlocked.Increment(ref _counters.Conflicts);
        if (_producers.Get(c.Src) is { } p) Interlocked.Increment(ref p.Conflicts);
        output.Conflict(new JoinConflict(c, command, db));
    }

    /// <summary>02 §6.3: a match needs the same command and db.</summary>
    private static bool Agree(CmdRecord c, string command, string? db) => c.Command == command && c.Db == db;

    /// <summary>Into the finalized index: a ring of keys bounds it, the oldest falls out first. The late window runs from the op's arrival.</summary>
    private void Remember(JoinKey key, Operation op, long arrivedUs, bool joined)
    {
        var slot = _doneNext;
        if (_doneFull)
        {
            var old = _doneRing[slot];
            if (_done.TryGetValue(old, out var e) && e.Slot == slot) _done.Remove(old);
        }
        _doneRing[slot] = key;
        if (++_doneNext == _doneRing.Length) { _doneNext = 0; _doneFull = true; }
        _done[key] = new Finalized
        {
            OpId = op.Id, Command = op.Op.Command, Db = op.Op.Db, OpTs = op.Op.Ts, OpEndUs = op.Op.RespLastUs ?? op.Op.ReqLastUs,
            ArrivedUs = arrivedUs, Joined = joined, Slot = slot,
        };
    }
}
