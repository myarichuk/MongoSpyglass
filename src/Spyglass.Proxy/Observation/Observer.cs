using System.Text;
using System.Text.Json;
using System.Threading.Channels;
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
    private readonly Redactor _redactor;
    private readonly CursorRegistry _cursors;
    private readonly CollectorSender? _sender;
    private readonly string _src;

    internal readonly CommandMetrics Commands;
    private readonly Counter _noSlot, _pendingEvicted, _exportDropped, _tapFault, _observerFault;

    /// <summary>Test hook: every record line, after serialization (headless mode included).</summary>
    internal Action<byte[]>? RecordTap { get; set; }

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
        m.ObservationDropped.WithLabels("queue_full"); // pre-create: the pool bound covers the queue (01 §4)

        _pool = new CapturePool(_config.Capture.CommandBytes, _config.Capture.ReplyBytes, _config.Limits.ObserveQueueBytes,
            m.ObserveQueueBytes.WithLabels());
        _redactor = new Redactor(_config.Capture);
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
            _workers[i] = new Worker(this);
    }

    public bool BuildsRecords => _sender != null || RecordTap != null;
    internal CapturePool Pool => _pool;
    internal CursorRegistry Cursors => _cursors;

    public void Start()
    {
        foreach (var w in _workers) w.Start();
        _sender?.Start();
    }

    // ---- IConnectionObserver ---------------------------------------------------------------

    public IFrameTap CreateTap(ConnectionContext conn, bool isRequest) =>
        new CaptureTap(conn, isRequest, _pool, this, _noSlot, _tapFault);

    public void Opened(ConnectionContext conn) => WorkerFor(conn).Post(new ObserverItem(null, conn, ObserverEvent.Opened));

    public void Closed(ConnectionContext conn, CloseReason reason, string? detail) =>
        WorkerFor(conn).Post(new ObserverItem(null, conn, ObserverEvent.Closed, reason, detail));

    public void Publish(FrameCapture slot) => WorkerFor(slot.Conn).Post(new ObserverItem(slot, slot.Conn, ObserverEvent.Frame));

    private Worker WorkerFor(ConnectionContext c) => _workers[(int)(c.ConnSeq % _workers.Length)];

    // ---- records ---------------------------------------------------------------------------

    private void Emit(byte[] line)
    {
        RecordTap?.Invoke(line);
        _sender?.Enqueue(line);
    }

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
        return rw.End();
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
        return rw.End();
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
        public readonly Queue<int> PendingOrder = new();
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
        private readonly Channel<ObserverItem> _channel = Channel.CreateUnbounded<ObserverItem>(new UnboundedChannelOptions { SingleReader = true });
        private readonly Dictionary<long, ObservedConnection> _conns = new();
        private readonly RecordWriter _rw = new();
        private Task _loop = Task.CompletedTask;

        public Worker(Observer o) => _o = o;

        public Task Completion => _loop;

        public void Start() => _loop = Task.Factory.StartNew(LoopAsync, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();

        public void Post(in ObserverItem item)
        {
            if (!_channel.Writer.TryWrite(item) && item.Frame != null)
                _o._pool.Return(item.Frame); // completed: shutting down
        }

        public void Complete() => _channel.Writer.TryComplete();

        private async Task LoopAsync()
        {
            var reader = _channel.Reader;
            while (await reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (reader.TryRead(out var item))
                {
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
            }
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
            var req = RequestParser.Parse(f, _o._redactor, _o._config.Compression);
            if (req.IsHello && req.Client != null && conn.Client == null)
                conn.Client = req.Client;

            if (req.MoreToCome)
            {
                // unacknowledged write (w:0): complete now, no reply will come (01 §7)
                EmitOpenIfNeeded(conn);
                CompleteOp(conn, req, null, status: "unacknowledged");
                return;
            }

            if (conn.Pending.Count >= _o._config.Limits.MaxPendingPerConnection)
            {
                while (conn.PendingOrder.TryDequeue(out var oldest))
                    if (conn.Pending.Remove(oldest))
                        break;
                _o._pendingEvicted.Inc();
            }
            conn.Pending[req.RequestId] = req;
            conn.PendingOrder.Enqueue(req.RequestId);
            if (conn.PendingOrder.Count > conn.Pending.Count * 2 + 16)
                CompactOrder(conn);
        }

        private static void CompactOrder(ObservedConnection conn)
        {
            var keep = conn.PendingOrder.Where(conn.Pending.ContainsKey).ToArray();
            conn.PendingOrder.Clear();
            foreach (var k in keep) conn.PendingOrder.Enqueue(k);
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
                return;
            }

            if (!conn.Pending.Remove(f.ResponseTo, out var req))
            {
                _o._server.Metrics.UncorrelatedReplies.WithLabels().Inc();
                return;
            }

            if (moreToCome)
                conn.Streams[f.RequestId] = new StreamState { Request = req, Seq = 1 };

            EmitOpenIfNeeded(conn);
            CompleteOp(conn, req, f, status: null, stream: moreToCome);
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
                CompleteOp(conn, req, null, status: "connection_closed");

            if (!_o.BuildsRecords) return;
            var j = _rw.Begin("conn", Clock.NowUnixMicros(), _o._src);
            WriteConnFields(j, conn, "close");
            j.WriteString("closeReason", reason.Wire());
            if (detail != null && reason == CloseReason.InvalidFrame)
                j.WriteString("invalidHeader", detail);
            j.WriteNumber("bytesIn", Interlocked.Read(ref ctx.BytesFromClient));
            j.WriteNumber("bytesOut", Interlocked.Read(ref ctx.BytesFromServer));
            _o.Emit(_rw.End());
        }

        private void EmitOpenIfNeeded(ObservedConnection conn)
        {
            if (conn.OpenEmitted) return;
            conn.OpenEmitted = true;
            if (!_o.BuildsRecords) return;
            var j = _rw.Begin("conn", Clock.NowUnixMicros(), _o._src);
            WriteConnFields(j, conn, "open");
            _o.Emit(_rw.End());
        }

        private void WriteConnFields(Utf8JsonWriter j, ObservedConnection conn, string ev)
        {
            var ctx = conn.Ctx;
            j.WriteString("event", ev);
            j.WriteString("proxyId", _o._src);
            j.WriteString("member", ctx.Member.Advertised);
            j.WriteNumber("connSeq", ctx.ConnSeq);
            j.WriteNumberOrNull("serverConnectionId", ctx.ServerConnectionId);
            j.WriteString("client", ctx.ClientEndpoint);
            j.WriteBoolean("tls", ctx.ClientTls);
            j.WriteStringOrNull("app", conn.Client?.Application);
            j.WriteStringOrNull("driver", conn.Client?.Driver);
            j.WriteStringOrNull("driverVersion", conn.Client?.DriverVersion);
            j.WriteStringOrNull("exporter", conn.Client?.Exporter);
            j.WriteStringOrNull("os", conn.Client?.OsType);
            j.WriteStringOrNull("platform", conn.Client?.Platform);
        }

        private void CompleteOp(ObservedConnection conn, RequestInfo req, FrameCapture? reply, string? status, bool stream = false)
        {
            var facts = reply?.Facts;
            status ??= facts?.Ok == 0 ? "error" : "ok";
            var db = req.Db ?? "";
            var app = conn.App;
            var member = conn.Ctx.Member.Advertised;
            var cm = _o.Commands;

            // ---- metrics (always, headless included) ----
            if (reply != null)
            {
                var seconds = Clock.Micros(req.TLast, reply.TFirst) / 1e6;
                cm.Duration.WithLabels(req.Command, db, cm.Collection(cm.Duration, req.Command, db, req.Coll, app, status), app, status).Observe(Math.Max(0, seconds));
                var coll = cm.Collection(cm.ReplyBytes, req.Command, db, req.Coll, app);
                cm.ReplyBytes.WithLabels(req.Command, db, coll, app).Observe(reply.FrameLength);
                if (facts!.NReturned is { } nr)
                    cm.ReturnedDocuments.WithLabels(req.Command, db, cm.Collection(cm.ReturnedDocuments, req.Command, db, req.Coll, app), app).Observe(nr);
                if (status == "error")
                {
                    var codeName = facts.CodeNameLength >= 0 ? Encoding.UTF8.GetString(facts.CodeName, 0, facts.CodeNameLength) : facts.Code?.ToString() ?? "";
                    cm.Errors.WithLabels(req.Command, db, cm.Collection(cm.Errors, req.Command, db, req.Coll, app, codeName), app, codeName).Inc();
                }
                TrackCursors(member, req, facts);
            }

            if (!_o.BuildsRecords) return;

            // ---- op record (02 §4.2) ----
            var ts = Clock.ToUnixMicros(req.TFirst);
            var j = _rw.Begin("op", ts, _o._src);
            j.WriteString("proxyId", _o._src);
            j.WriteString("member", member);
            j.WriteNumberOrNull("serverConnectionId", conn.Ctx.ServerConnectionId);
            j.WriteNumber("requestId", req.RequestId);
            j.WriteNumber("connSeq", conn.Ctx.ConnSeq);
            j.WriteString("opCode", OpCodes.Name(req.OpCode));
            j.WriteString("command", req.Command);
            j.WriteStringOrNull("db", req.Db);
            j.WriteStringOrNull("coll", req.Coll);
            j.WriteBoolean("ack", !req.MoreToCome);
            j.WriteBoolean("stream", stream);
            j.WriteNumber("replyCount", reply != null ? 1 : 0);
            j.WriteString("status", status);
            j.WriteNumber("reqBytes", req.FrameLength);
            j.WriteNumber("respBytes", reply?.FrameLength ?? 0);
            j.WriteStartObject("t");
            j.WriteNumber("reqFirst_us", 0);
            j.WriteNumber("reqLast_us", Clock.Micros(req.TFirst, req.TLast));
            if (reply != null)
            {
                j.WriteNumber("respFirst_us", Clock.Micros(req.TFirst, reply.TFirst));
                j.WriteNumber("respLast_us", Clock.Micros(req.TFirst, reply.TLast));
            }
            j.WriteEndObject();
            if (reply != null)
                j.WriteNumber("serverLatency_us", Math.Max(0, Clock.Micros(req.TLast, reply.TFirst)));
            else
                j.WriteNull("serverLatency_us");

            if (facts != null)
                WriteReply(j, facts, req.IsRedactedCommand);
            else
                j.WriteNull("reply");

            j.WriteStringOrNull("lsid", req.Lsid);
            j.WriteNumberOrNull("txnNumber", req.TxnNumber);
            j.WriteStringOrNull("comment", req.Comment);
            if (req.RedactedCommand != null)
                j.WriteBase64String("cmd", req.RedactedCommand);
            j.WriteBoolean("cmdTruncated", req.CommandTruncated);
            if (req.SeqDocs is { Count: > 0 })
            {
                j.WriteStartObject("seqDocs");
                foreach (var (id, n) in req.SeqDocs) j.WriteNumber(id, n);
                j.WriteEndObject();
            }
            if (req.Compressor != null)
                j.WriteString("compressor", req.Compressor);
            _o.Emit(_rw.End());
        }

        private void OnStreamReply(ObservedConnection conn, StreamState s, FrameCapture f)
        {
            var member = conn.Ctx.Member.Advertised;
            var req = s.Request;
            var cm = _o.Commands;
            var app = conn.App;
            cm.ReplyBytes.WithLabels(req.Command, req.Db ?? "", cm.Collection(cm.ReplyBytes, req.Command, req.Db ?? "", req.Coll, app), app).Observe(f.FrameLength);
            TrackCursors(member, req, f.Facts);

            if (!_o.BuildsRecords) return;
            var j = _rw.Begin("stream", Clock.ToUnixMicros(f.TFirst), _o._src);
            j.WriteString("proxyId", _o._src);
            j.WriteString("member", member);
            j.WriteNumberOrNull("serverConnectionId", conn.Ctx.ServerConnectionId);
            j.WriteNumber("requestId", req.RequestId);
            j.WriteNumber("seq", s.Seq);
            j.WriteNumber("respBytes", f.FrameLength);
            j.WriteStartObject("reply");
            if (f.Facts.CursorId is { } id) j.WriteString("cursorId", id.ToString());
            j.WriteNumberOrNull("nReturned", f.Facts.NReturned);
            j.WriteNumberOrNull("ok", f.Facts.Ok);
            j.WriteEndObject();
            _o.Emit(_rw.End());
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
                    if (req.KillCursorIds != null)
                        foreach (var id in req.KillCursorIds) reg.Closed(member, id);
                    return;
                default:
                    if (facts.CursorId is { } c and not 0)
                    {
                        string? coll = req.Coll;
                        if (facts.NsLength > 0)
                        {
                            var ns = Encoding.UTF8.GetString(facts.Ns, 0, facts.NsLength);
                            var dot = ns.IndexOf('.');
                            if (dot > 0) coll = ns[(dot + 1)..];
                        }
                        reg.Opened(member, c, req.Db ?? "", coll);
                    }
                    return;
            }
        }

        private static void WriteReply(Utf8JsonWriter j, ReplyFacts f, bool authCommand)
        {
            j.WriteStartObject("reply");
            j.WriteNumberOrNull("ok", f.Ok);
            j.WriteNumberOrNull("code", f.Code);
            j.WriteStringOrNull("codeName", f.CodeNameLength >= 0 ? Encoding.UTF8.GetString(f.CodeName, 0, f.CodeNameLength) : null);
            if (authCommand)
            {
                // 01 §9.1: replies to redacted commands are reduced to ok/code/codeName
                j.WriteEndObject();
                return;
            }
            j.WriteStringOrNull("errmsg", f.ErrmsgLength >= 0 ? Encoding.UTF8.GetString(f.Errmsg, 0, f.ErrmsgLength) : null);
            if (f.CursorId is { } id) j.WriteString("cursorId", id.ToString());
            else j.WriteNull("cursorId");
            j.WriteNumberOrNull("nReturned", f.NReturned);
            j.WriteNumberOrNull("n", f.N);
            j.WriteNumberOrNull("nModified", f.NModified);
            j.WriteNumber("writeErrors", f.WriteErrors);
            j.WriteBoolean("writeConcernError", f.WriteConcernError);
            j.WriteNumber("upserted", f.Upserted);
            if (f.CursorsKilledCount + f.CursorsNotFoundCount > 0)
            {
                j.WriteNumber("cursorsKilled", f.CursorsKilledCount);
                j.WriteNumber("cursorsNotFound", f.CursorsNotFoundCount);
            }
            j.WriteBoolean("parsed", f.Parsed);
            j.WriteEndObject();
        }
    }
}
