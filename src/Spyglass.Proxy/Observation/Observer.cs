using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using Spyglass.Proxy.Config;
using Spyglass.Proxy.Diagnostics;
using Spyglass.Proxy.Export;
using Spyglass.Proxy.Forwarding;
using Spyglass.Proxy.Metrics;
using Spyglass.Proxy.Wire;

namespace Spyglass.Proxy.Observation;

/// <summary>
/// The observation side (01 §4, §7, §9, §11, §12): capture pool and taps, workers with per-connection
/// affinity, request/response correlation, redaction, command metrics and wire records.
/// Nothing here runs on, or is awaited by, a forwarding pump.
/// </summary>
internal sealed class Observer : IConnectionObserver, IAsyncDisposable
{
    private readonly ProxyServer _server;
    private readonly ProxyConfig _config;
    private readonly CapturePool _pool;
    private readonly Worker[] _workers;
    private readonly CursorRegistry _cursors;
    private readonly CollectorSender? _sender;
    private readonly string _src;

    internal readonly CommandMetrics Commands;
    private readonly Counter _noSlot, _pendingEvicted, _exportDropped, _tapFault, _observerFault, _uncorrelated;

    /// <summary>Test hook: every record line, after serialization (headless mode included).</summary>
    internal RecordSink? RecordTap { get; set; }

    public Observer(ProxyServer server)
    {
        _server = server;
        _config = server.Config;
        _src = server.ProxyId.ToString();
        var m = server.Metrics;
        _noSlot = m.ObservationDropped.WithLabels("no_slot");
        _pendingEvicted = m.ObservationDropped.WithLabels("pending_evicted");
        _exportDropped = m.ObservationDropped.WithLabels("export_dropped");
        _tapFault = m.ObservationDropped.WithLabels("tap_fault");
        _observerFault = m.ObservationDropped.WithLabels("observer_fault");
        _uncorrelated = m.UncorrelatedReplies.WithLabels();
        m.ObservationDropped.WithLabels("queue_full"); // pre-create: the pool bound covers the queue (01 §4)

        _pool = new CapturePool(_config.Capture.CommandBytes, _config.Capture.ReplyBytes, _config.Limits.ObserveQueueBytes);
        var queueGauge = m.ObserveQueueBytes.WithLabels();
        m.BeforeScrape += () => queueGauge.Set(_pool.ChargedBytes);
        Commands = new CommandMetrics(m.Registry, _config.Metrics.MaxSeries);
        _cursors = new CursorRegistry(Commands);

        if (_config.Collector.Endpoint != null)
        {
            _sender = new CollectorSender(_config.Collector, _src, _exportDropped)
            {
                Periodic60s = () => [ProxyRecord()],
                Stats10s = StatsRecord,
            };
        }

        _workers = new Worker[Math.Max(1, Environment.ProcessorCount / 2)];
        for (var i = 0; i < _workers.Length; i++)
            _workers[i] = new Worker(this, m.ObserverBusy.WithLabels(i.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    }

    public bool BuildsRecords => _sender != null || RecordTap != null;
    internal CapturePool Pool => _pool;
    internal CollectorSender? Sender => _sender;
    internal CursorRegistry Cursors => _cursors;

    public void Start()
    {
        foreach (var w in _workers) w.Start();
        _sender?.Start();
    }

    // ---- IConnectionObserver ---------------------------------------------------------------

    public IFrameTap CreateTap(ConnectionContext conn, bool isRequest) =>
        _pool.Disabled // limits.observeQueueBytes=0: forwarding only, no per-frame work at all
            ? NullTap.Instance
            : new CaptureTap(conn, isRequest, _pool, this, _noSlot, _tapFault);

    public void Opened(ConnectionContext conn) => WorkerFor(conn).Post(new ObserverItem(null, conn, ObserverEvent.Opened));

    public void Closed(ConnectionContext conn, CloseReason reason, string? detail) =>
        WorkerFor(conn).Post(new ObserverItem(null, conn, ObserverEvent.Closed, reason, detail));

    public void Publish(FrameCapture slot) => WorkerFor(slot.Conn).Post(new ObserverItem(slot, slot.Conn, ObserverEvent.Frame));

    private Worker WorkerFor(ConnectionContext c) => _workers[(int)(c.ConnSeq % _workers.Length)];

    // ---- records ---------------------------------------------------------------------------

    private byte[] ProxyRecord()
    {
        var rw = new RecordWriter();
        var j = rw.Begin("proxy", Clock.NowUnixMicros(), _src);
        j.WriteString("proxyId", _src);
        j.WriteString("version", typeof(Observer).Assembly.GetName().Version?.ToString(3) ?? "0");
        j.WriteNumber("schema", ProxyServer.Schema);
        var t = _server.Topology;
        j.WriteString("topology", t.Kind switch
        {
            TopologyKind.ReplicaSet => "replicaSet",
            TopologyKind.Sharded => "sharded",
            _ => "standalone"
        });
        j.WriteStringOrNull("setName", t.SetName);
        j.WriteStartArray("members");
        foreach (var mem in t.Members)
        {
            j.WriteStartObject();
            j.WriteString("member", mem.Advertised);
            j.WriteString("upstream", mem.Upstream.ToString());
            j.WriteEndObject();
        }
        j.WriteEndArray();
        j.WriteStartObject("config");
        j.WriteString("compression", _config.Compression.ToString().ToLowerInvariant());
        j.WriteString("values", _config.Capture.Values.ToString().ToLowerInvariant());
        j.WriteNumber("commandBytes", _config.Capture.CommandBytes);
        j.WriteEndObject();
        return rw.End().ToArray();
    }

    private byte[] StatsRecord()
    {
        var rw = new RecordWriter();
        var j = rw.Begin("stats", Clock.NowUnixMicros(), _src);
        j.WriteStartObject("dropped");
        j.WriteNumber("no_slot", _noSlot.Value);
        j.WriteNumber("queue_full", 0);
        j.WriteNumber("pending_evicted", _pendingEvicted.Value);
        j.WriteNumber("export", _exportDropped.Value);
        j.WriteEndObject();
        j.WriteStartObject("sent");
        j.WriteNumber("records", Interlocked.Read(ref _sender!.SentRecords));
        j.WriteNumber("batches", Interlocked.Read(ref _sender.SentBatches));
        j.WriteEndObject();
        return rw.End().ToArray();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var w in _workers) w.Complete();
        foreach (var w in _workers) await w.Completion.ConfigureAwait(false);
        if (_sender != null) await _sender.DisposeAsync().ConfigureAwait(false);
    }

    // ---- worker ----------------------------------------------------------------------------

    private sealed class ObservedConnection(ConnectionContext ctx)
    {
        public readonly ConnectionContext Ctx = ctx;
        public readonly Dictionary<int, RequestInfo> Pending = new();
        /// <summary>moreToCome streams: next expected responseTo → stream state.</summary>
        public readonly Dictionary<int, StreamState> Streams = new();
        public ClientMetadata? Client;
        public bool OpenEmitted;
        public string App => Client?.Application ?? Client?.Driver ?? "";
    }

    private sealed class StreamState
    {
        public required RequestInfo Request;
        public int Seq;
    }

    private sealed class Worker
    {
        private readonly Observer _o;
        // Pumps post with one CAS and no lock (an unbounded Channel<T> takes its sync lock on every write, and the
        // pumps contended with the worker on it), and almost never wake the worker: a wake is a futex call on the
        // pump thread, and below saturation the worker is asleep at nearly every post. Observation is not latency
        // sensitive (records are batched for collector.flushIntervalMs anyway), so the worker sleeps on a short
        // timer and a pump wakes it only every WakeEvery posts, which bounds the queue (and the pool charge) in a burst.
        private const int MinSleepMs = 1, MaxSleepMs = 10, WakeEvery = 64;
        private readonly ConcurrentQueue<ObserverItem> _queue = new();
        private readonly SemaphoreSlim _wake = new(0);
        private int _sleeping;
        private int _sleepMs = MinSleepMs;
        private int _posts; // racy on purpose: a lost increment only shifts when a wake happens
        private volatile bool _completed;
        private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Dictionary<long, ObservedConnection> _conns = new();
        private readonly RecordWriter _rw = new();
        private readonly RecordBuffer? _out;
        // Per worker: a Regex shared between threads re-creates its runner (and its backtracking stacks)
        // whenever two matches overlap; a private instance reuses one runner forever.
        private readonly RequestParser _parser;
        // Metric children resolved once per label set; the families' params-array/string.Join lookup is not
        // something to pay per op.
        private readonly Dictionary<(string, string, string?, string, string), Histogram> _duration = new();
        private readonly Dictionary<(string, string, string?, string), (Histogram Bytes, Histogram Docs)> _sizes = new();
        private readonly Dictionary<(string, string, string?, string, string), Counter> _errors = new();
        private readonly Counter _busy;

        public Worker(Observer o, Counter busy)
        {
            _o = o;
            _busy = busy;
            _out = o._sender?.CreateBuffer();
            _parser = new RequestParser(new Redactor(o._config.Capture), o._config.Compression);
        }

        private Histogram Duration(string cmd, string db, string? coll, string app, string status)
        {
            if (_duration.TryGetValue((cmd, db, coll, app, status), out var h)) return h;
            var cm = _o.Commands;
            h = cm.Duration.WithLabels(cmd, db, cm.Collection(cm.Duration, cmd, db, coll, app, status), app, status);
            return _duration[(cmd, db, coll, app, status)] = h;
        }

        private (Histogram Bytes, Histogram Docs) Sizes(string cmd, string db, string? coll, string app)
        {
            if (_sizes.TryGetValue((cmd, db, coll, app), out var s)) return s;
            var cm = _o.Commands;
            s = (cm.ReplyBytes.WithLabels(cmd, db, cm.Collection(cm.ReplyBytes, cmd, db, coll, app), app),
                 cm.ReturnedDocuments.WithLabels(cmd, db, cm.Collection(cm.ReturnedDocuments, cmd, db, coll, app), app));
            return _sizes[(cmd, db, coll, app)] = s;
        }

        private Counter Error(string cmd, string db, string? coll, string app, string codeName)
        {
            if (_errors.TryGetValue((cmd, db, coll, app, codeName), out var c)) return c;
            var cm = _o.Commands;
            c = cm.Errors.WithLabels(cmd, db, cm.Collection(cm.Errors, cmd, db, coll, app, codeName), app, codeName);
            return _errors[(cmd, db, coll, app, codeName)] = c;
        }

        public Task Completion => _done.Task;

        private void Emit()
        {
            var line = _rw.End();
            _o.RecordTap?.Invoke(line);
            _out?.Append(line);
        }

        public void Start() => new Thread(Loop) { IsBackground = true, Name = "spyglass-observer" }.Start();

        public void Post(in ObserverItem item)
        {
            if (_completed)
            {
                if (item.Frame != null) _o._pool.Return(item.Frame); // shutting down
                return;
            }
            _queue.Enqueue(item); // interlocked: a full fence before the read below
            if ((++_posts & (WakeEvery - 1)) == 0 && Volatile.Read(ref _sleeping) == 1 && Interlocked.Exchange(ref _sleeping, 0) == 1)
                _wake.Release();
        }

        public void Complete()
        {
            _completed = true;
            if (Interlocked.Exchange(ref _sleeping, 0) == 1)
                _wake.Release();
        }

        /// <summary>
        /// Sleeps until the timer or a wake. Whoever lowers the flag (a poster, <see cref="Complete"/>, or this
        /// thread on timeout) owns it; a release that lands after a timeout only makes the next sleep short.
        /// </summary>
        private void Sleep(bool hadWork)
        {
            _sleepMs = hadWork ? MinSleepMs : Math.Min(MaxSleepMs, _sleepMs * 2);
            Interlocked.Exchange(ref _sleeping, 1);
            if (_completed && Interlocked.Exchange(ref _sleeping, 0) == 1)
                return;
            if (!_wake.Wait(_sleepMs))
                Interlocked.Exchange(ref _sleeping, 0);
        }

        private long _busyCarry; // sub-µs remainder, in Stopwatch ticks: drains of one op are a few µs

        private void AddBusy(long ticks)
        {
            ticks += _busyCarry;
            var us = ticks * 1_000_000 / Stopwatch.Frequency;
            _busyCarry = ticks - us * Stopwatch.Frequency / 1_000_000;
            _busy.Add(us);
        }

        private void Loop()
        {
            try
            {
                while (true)
                {
                    var hadWork = Drain();
                    if (_completed)
                    {
                        Drain(); // posts that raced with Complete
                        return;
                    }
                    Sleep(hadWork);
                }
            }
            finally
            {
                _done.TrySetResult();
            }
        }

        private bool Drain()
        {
            if (_queue.IsEmpty) return false;
            var t0 = Stopwatch.GetTimestamp();
            var n = 0;
            while (_queue.TryDequeue(out var item))
            {
                if (++n % 1024 == 0) // a queue that never drains still reports
                {
                    var t = Stopwatch.GetTimestamp();
                    AddBusy(t - t0);
                    t0 = t;
                }
                try
                {
                    Handle(item);
                }
                catch (Exception e)
                {
                    _o._observerFault.Inc();
                    Log.Warn($"observer: {e.GetType().Name}: {e.Message}");
                }
                finally
                {
                    if (item.Frame != null)
                        _o._pool.Return(item.Frame);
                }
            }
            AddBusy(Stopwatch.GetTimestamp() - t0);
            return true;
        }

        private void Handle(in ObserverItem item)
        {
            switch (item.Event)
            {
                case ObserverEvent.Opened:
                    _conns[item.Conn.ConnSeq] = new ObservedConnection(item.Conn);
                    return;
                case ObserverEvent.Closed:
                    OnClosed(item.Conn, item.Reason, item.Detail);
                    return;
            }

            if (!_conns.TryGetValue(item.Conn.ConnSeq, out var conn))
                return; // frames of a connection we never saw open (cannot happen; ordering is per worker)
            var f = item.Frame!;
            if (f.IsRequest) OnRequest(conn, f);
            else OnReply(conn, f);
        }

        private void OnRequest(ObservedConnection conn, FrameCapture f)
        {
            var req = _parser.Parse(f, records: _o.BuildsRecords);
            if (req.IsHello && req.Client != null && conn.Client == null)
                conn.Client = req.Client;

            if (req.MoreToCome)
            {
                // unacknowledged write (w:0): complete now, no reply will come (01 §7)
                EmitOpenIfNeeded(conn);
                CompleteOp(conn, req, null, status: "unacknowledged");
                _parser.Return(req);
                return;
            }

            if (conn.Pending.Count >= _o._config.Limits.MaxPendingPerConnection && !conn.Pending.ContainsKey(req.RequestId))
            {
                // Only reachable when a client pipelines past the bound (drivers don't): O(n) scan is fine here
                // and keeps the common path free of any ordering structure.
                var oldest = conn.Pending.MinBy(kv => kv.Value.TFirst).Key;
                conn.Pending.Remove(oldest, out var evicted);
                _parser.Return(evicted!);
                _o._pendingEvicted.Inc();
            }
            if (conn.Pending.Remove(req.RequestId, out var reused)) // a client reusing an id it never got a reply for
                _parser.Return(reused);
            conn.Pending[req.RequestId] = req;
        }

        private void OnReply(ObservedConnection conn, FrameCapture f)
        {
            var moreToCome = (f.Flags & MsgFlags.MoreToCome) != 0 && f.OpCode == OpCodes.Msg;

            if (conn.Streams.Remove(f.ResponseTo, out var stream))
            {
                stream.Seq++;
                if (moreToCome)
                    conn.Streams[f.RequestId] = stream;
                OnStreamReply(conn, stream, f);
                if (!moreToCome)
                    _parser.Return(stream.Request); // the stream ended
                return;
            }

            if (!conn.Pending.Remove(f.ResponseTo, out var req))
            {
                _o._uncorrelated.Inc();
                return;
            }

            if (moreToCome)
                conn.Streams[f.RequestId] = new StreamState { Request = req, Seq = 1 };

            EmitOpenIfNeeded(conn);
            CompleteOp(conn, req, f, status: null, stream: moreToCome);
            if (!moreToCome)
                _parser.Return(req); // else the stream holds it
        }

        private void OnClosed(ConnectionContext ctx, CloseReason reason, string? detail)
        {
            if (!_conns.Remove(ctx.ConnSeq, out var conn))
            {
                // upstream_connect_failed: the connection was never opened for observation
                conn = new ObservedConnection(ctx);
            }
            EmitOpenIfNeeded(conn);
            foreach (var req in conn.Pending.Values.OrderBy(r => r.TFirst))
            {
                CompleteOp(conn, req, null, status: "connection_closed");
                _parser.Return(req);
            }
            conn.Pending.Clear();
            foreach (var st in conn.Streams.Values)
                _parser.Return(st.Request);
            conn.Streams.Clear();

            if (!_o.BuildsRecords) return;
            var j = _rw.Begin("conn", Clock.NowUnixMicros(), _o._src);
            WriteConnFields(j, conn, "close");
            j.WriteString(J.closeReason, reason.Wire());
            if (detail != null && reason == CloseReason.InvalidFrame)
                j.WriteString(J.invalidHeader, detail);
            j.WriteNumber(J.bytesIn, Volatile.Read(ref ctx.BytesFromClient));
            j.WriteNumber(J.bytesOut, Volatile.Read(ref ctx.BytesFromServer));
            Emit();
        }

        private void EmitOpenIfNeeded(ObservedConnection conn)
        {
            if (conn.OpenEmitted) return;
            conn.OpenEmitted = true;
            if (!_o.BuildsRecords) return;
            var j = _rw.Begin("conn", Clock.NowUnixMicros(), _o._src);
            WriteConnFields(j, conn, "open");
            Emit();
        }

        private void WriteConnFields(Utf8JsonWriter j, ObservedConnection conn, string ev)
        {
            var ctx = conn.Ctx;
            j.WriteString(J.@event, ev);
            j.WriteString(J.proxyId, _o._src);
            j.WriteString(J.member, ctx.Member.Advertised);
            j.WriteNumber(J.connSeq, ctx.ConnSeq);
            j.WriteNumberOrNull(J.serverConnectionId, ctx.ServerConnectionId);
            j.WriteString(J.client, ctx.ClientEndpoint);
            j.WriteBoolean(J.tls, ctx.ClientTls);
            j.WriteStringOrNull(J.app, conn.Client?.Application);
            j.WriteStringOrNull(J.driver, conn.Client?.Driver);
            j.WriteStringOrNull(J.driverVersion, conn.Client?.DriverVersion);
            j.WriteStringOrNull(J.exporter, conn.Client?.Exporter);
            j.WriteStringOrNull(J.os, conn.Client?.OsType);
            j.WriteStringOrNull(J.platform, conn.Client?.Platform);
        }

        private void CompleteOp(ObservedConnection conn, RequestInfo req, FrameCapture? reply, string? status, bool stream = false)
        {
            var facts = reply?.Facts;
            status ??= facts?.Ok == 0 ? "error" : "ok";
            var db = req.Db ?? "";
            var app = conn.App;
            var member = conn.Ctx.Member.Advertised;

            // ---- metrics (always, headless included) ----
            if (reply != null)
            {
                var seconds = Clock.Micros(req.TLast, reply.TFirst) / 1e6;
                Duration(req.Command, db, req.Coll, app, status).Observe(Math.Max(0, seconds));
                var sizes = Sizes(req.Command, db, req.Coll, app);
                sizes.Bytes.Observe(reply.FrameLength);
                if (facts!.NReturned is { } nr)
                    sizes.Docs.Observe(nr);
                if (status == "error")
                {
                    var codeName = facts.CodeNameLength >= 0 ? _parser.Names.Get(facts.CodeName.AsSpan(0, facts.CodeNameLength)) : facts.Code?.ToString() ?? "";
                    Error(req.Command, db, req.Coll, app, codeName).Inc();
                }
                TrackCursors(member, req, facts);
            }

            if (!_o.BuildsRecords) return;

            // ---- op record (02 §4.2) ----
            var ts = Clock.ToUnixMicros(req.TFirst);
            var j = _rw.Begin("op", ts, _o._src);
            j.WriteString(J.proxyId, _o._src);
            j.WriteString(J.member, member);
            j.WriteNumberOrNull(J.serverConnectionId, conn.Ctx.ServerConnectionId);
            j.WriteNumber(J.requestId, req.RequestId);
            j.WriteNumber(J.connSeq, conn.Ctx.ConnSeq);
            j.WriteString(J.opCode, OpCodes.Name(req.OpCode));
            j.WriteString(J.command, req.Command);
            j.WriteStringOrNull(J.db, req.Db);
            j.WriteStringOrNull(J.coll, req.Coll);
            j.WriteBoolean(J.ack, !req.MoreToCome);
            j.WriteBoolean(J.stream, stream);
            j.WriteNumber(J.replyCount, reply != null ? 1 : 0);
            j.WriteString(J.status, status);
            j.WriteNumber(J.reqBytes, req.FrameLength);
            j.WriteNumber(J.respBytes, reply?.FrameLength ?? 0);
            j.WriteStartObject(J.t);
            j.WriteNumber(J.reqFirst_us, 0);
            j.WriteNumber(J.reqLast_us, Clock.Micros(req.TFirst, req.TLast));
            if (reply != null)
            {
                j.WriteNumber(J.respFirst_us, Clock.Micros(req.TFirst, reply.TFirst));
                j.WriteNumber(J.respLast_us, Clock.Micros(req.TFirst, reply.TLast));
            }
            j.WriteEndObject();
            if (reply != null)
                j.WriteNumber(J.serverLatency_us, Math.Max(0, Clock.Micros(req.TLast, reply.TFirst)));
            else
                j.WriteNull(J.serverLatency_us);

            if (facts != null)
                WriteReply(j, facts, req.IsRedactedCommand);
            else
                j.WriteNull(J.reply);

            j.WriteStringOrNull(J.lsid, req.Lsid);
            j.WriteNumberOrNull(J.txnNumber, req.TxnNumber);
            j.WriteStringOrNull(J.comment, req.Comment);
            if (req.HasRedactedCommand)
                j.WriteBase64String(J.cmd, req.RedactedCommand);
            j.WriteBoolean(J.cmdTruncated, req.CommandTruncated);
            if (req.SeqDocs.Count > 0)
            {
                j.WriteStartObject(J.seqDocs);
                foreach (var (id, n) in req.SeqDocs) j.WriteNumber(id, n);
                j.WriteEndObject();
            }
            if (req.Compressor != null)
                j.WriteString(J.compressor, req.Compressor);
            Emit();
        }

        private void OnStreamReply(ObservedConnection conn, StreamState s, FrameCapture f)
        {
            var member = conn.Ctx.Member.Advertised;
            var req = s.Request;
            var app = conn.App;
            Sizes(req.Command, req.Db ?? "", req.Coll, app).Bytes.Observe(f.FrameLength);
            TrackCursors(member, req, f.Facts);

            if (!_o.BuildsRecords) return;
            var j = _rw.Begin("stream", Clock.ToUnixMicros(f.TFirst), _o._src);
            j.WriteString(J.proxyId, _o._src);
            j.WriteString(J.member, member);
            j.WriteNumberOrNull(J.serverConnectionId, conn.Ctx.ServerConnectionId);
            j.WriteNumber(J.requestId, req.RequestId);
            j.WriteNumber(J.seq, s.Seq);
            j.WriteNumber(J.respBytes, f.FrameLength);
            j.WriteStartObject(J.reply);
            if (f.Facts.CursorId is { } id) WriteInt64String(j, J.cursorId, id);
            j.WriteNumberOrNull(J.nReturned, f.Facts.NReturned);
            j.WriteNumberOrNull(J.ok, f.Facts.Ok);
            j.WriteEndObject();
            Emit();
        }

        private void TrackCursors(string member, RequestInfo req, ReplyFacts facts)
        {
            var reg = _o._cursors;
            switch (req.Command)
            {
                case "getMore":
                    if (req.GetMoreCursorId is not { } gm) return;
                    if (facts.Ok == 0 && facts.Code == 43) reg.Closed(member, gm);       // CursorNotFound
                    else if (facts.CursorId == 0) reg.Closed(member, gm);
                    else reg.Touched(member, gm);
                    return;
                case "killCursors":
                    foreach (var id in req.KillCursorIds) reg.Closed(member, id);
                    return;
                default:
                    if (facts.CursorId is { } c and not 0)
                    {
                        string? coll = req.Coll;
                        if (facts.NsLength > 0)
                        {
                            var ns = facts.Ns.AsSpan(0, facts.NsLength);
                            var dot = ns.IndexOf((byte)'.');
                            if (dot > 0) coll = _parser.Names.Get(ns[(dot + 1)..]);
                        }
                        reg.Opened(member, c, req.Db ?? "", coll);
                    }
                    return;
            }
        }

        /// <summary>Cursor ids are decimal strings in records (02 §4: they may exceed 2^53-1).</summary>
        private static void WriteInt64String(Utf8JsonWriter j, JsonEncodedText name, long v)
        {
            Span<byte> digits = stackalloc byte[20];
            v.TryFormat(digits, out var n, provider: System.Globalization.CultureInfo.InvariantCulture);
            j.WriteString(name, digits[..n]);
        }

        private static void WriteReply(Utf8JsonWriter j, ReplyFacts f, bool authCommand)
        {
            j.WriteStartObject(J.reply);
            j.WriteNumberOrNull(J.ok, f.Ok);
            j.WriteNumberOrNull(J.code, f.Code);
            if (f.CodeNameLength >= 0) j.WriteString(J.codeName, f.CodeName.AsSpan(0, f.CodeNameLength));
            else j.WriteNull(J.codeName);
            if (authCommand)
            {
                // 01 §9.1: replies to redacted commands are reduced to ok/code/codeName
                j.WriteEndObject();
                return;
            }
            if (f.ErrmsgLength >= 0) j.WriteString(J.errmsg, f.Errmsg.AsSpan(0, f.ErrmsgLength));
            else j.WriteNull(J.errmsg);
            if (f.CursorId is { } id) WriteInt64String(j, J.cursorId, id);
            else j.WriteNull(J.cursorId);
            j.WriteNumberOrNull(J.nReturned, f.NReturned);
            j.WriteNumberOrNull(J.n, f.N);
            j.WriteNumberOrNull(J.nModified, f.NModified);
            j.WriteNumber(J.writeErrors, f.WriteErrors);
            j.WriteBoolean(J.writeConcernError, f.WriteConcernError);
            j.WriteNumber(J.upserted, f.Upserted);
            if (f.CursorsKilledCount + f.CursorsNotFoundCount > 0)
            {
                j.WriteNumber(J.cursorsKilled, f.CursorsKilledCount);
                j.WriteNumber(J.cursorsNotFound, f.CursorsNotFoundCount);
            }
            j.WriteBoolean(J.parsed, f.Parsed);
            j.WriteEndObject();
        }
    }
}
