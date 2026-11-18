using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SQLitePCL;
using Spyglass.Collector.Analysis;
using Spyglass.Collector.Ingest;
using Spyglass.Collector.Model;

namespace Spyglass.Collector.Storage;

/// <summary>
/// 03 §7 v1 store: one SQLite file in WAL mode. Operations are stored as their NDJSON line plus the
/// collector's enrichment, and re-read through the same <see cref="RecordParser"/>, so a stored op cannot
/// drift from the wire format. Ids are ULIDs: time order is primary-key order, and from/to filters become
/// key ranges. One write connection (behind a lock: the storage writer and the analysis flush both write),
/// pooled read connections for the API.
/// </summary>
public sealed class SqliteStore : IOperationStore, IAggregateStore, IAlertStore, ICallsiteStore, IDisposable
{
    private const int PruneChunk = 5000;

    private readonly string _readCs;
    private readonly SqliteConnection _write;
    private readonly object _writeLock = new();
    private long _count, _bytes;
    /// <summary>Per-row overhead beyond the raw line: columns and the secondary index entries.</summary>
    private const int RowOverhead = 256;
    /// <summary>03 §7: the WAL is truncated back to this after a checkpoint; and past it, retention truncates it explicitly and counts it toward the size bound.</summary>
    private const long WalLimit = 64L * 1024 * 1024;
    private readonly string _walFile;
    /// <summary>Inside <see cref="Transact"/>: Append/Update/callsites join the outer transaction, and their counters apply when it commits.</summary>
    private bool _inTx;
    private long _txCount, _txBytes;

    public SqliteStore(string directory)
    {
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "spyglass.db");
        _walFile = file + "-wal";
        var cs = new SqliteConnectionStringBuilder { DataSource = file, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = true }.ToString();
        _readCs = new SqliteConnectionStringBuilder(cs) { Mode = SqliteOpenMode.ReadOnly }.ToString();
        _write = new SqliteConnection(cs);
        _write.Open();
        // the ops table carries six secondary indexes: a 64 MiB page cache keeps their hot pages resident
        Exec("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000; PRAGMA foreign_keys=OFF; " +
             "PRAGMA cache_size=-65536; PRAGMA mmap_size=268435456; PRAGMA temp_store=MEMORY; PRAGMA journal_size_limit=67108864;");
        Exec("""
            CREATE TABLE IF NOT EXISTS ops (
              id TEXT PRIMARY KEY, ts INTEGER NOT NULL, command TEXT NOT NULL, coll TEXT, status TEXT NOT NULL,
              app TEXT, driver TEXT, latency INTEGER, shape TEXT NOT NULL, value_hash TEXT, reply_count INTEGER NOT NULL,
              alerts TEXT, size INTEGER NOT NULL, raw BLOB NOT NULL, ctx BLOB, uow TEXT) WITHOUT ROWID;
            CREATE INDEX IF NOT EXISTS ops_shape ON ops(shape, id);
            CREATE INDEX IF NOT EXISTS ops_app ON ops(app, id);
            CREATE INDEX IF NOT EXISTS ops_coll ON ops(coll, id);
            CREATE INDEX IF NOT EXISTS ops_command ON ops(command, id);
            CREATE INDEX IF NOT EXISTS ops_status ON ops(status, id) WHERE status <> 'ok';
            CREATE INDEX IF NOT EXISTS ops_latency ON ops(latency, id) WHERE latency IS NOT NULL;
            CREATE TABLE IF NOT EXISTS agg_minute (shape TEXT NOT NULL, minute INTEGER NOT NULL, data BLOB NOT NULL, PRIMARY KEY(shape, minute)) WITHOUT ROWID;
            CREATE INDEX IF NOT EXISTS agg_minute_t ON agg_minute(minute);
            CREATE TABLE IF NOT EXISTS agg_hour (shape TEXT NOT NULL, hour INTEGER NOT NULL, data BLOB NOT NULL, PRIMARY KEY(shape, hour)) WITHOUT ROWID;
            CREATE INDEX IF NOT EXISTS agg_hour_t ON agg_hour(hour);
            CREATE TABLE IF NOT EXISTS shapes (hash TEXT PRIMARY KEY, data TEXT NOT NULL) WITHOUT ROWID;
            CREATE TABLE IF NOT EXISTS alerts (id TEXT PRIMARY KEY, data TEXT NOT NULL) WITHOUT ROWID;
            CREATE TABLE IF NOT EXISTS callsites (hash TEXT PRIMARY KEY, data TEXT NOT NULL, last_used INTEGER NOT NULL) WITHOUT ROWID;
            """);
        // files from before the join (M1): add its columns
        if (Scalar("SELECT count(*) FROM pragma_table_info('ops') WHERE name = 'ctx'") == 0)
            Exec("ALTER TABLE ops ADD COLUMN ctx BLOB; ALTER TABLE ops ADD COLUMN uow TEXT;");
        Exec("CREATE INDEX IF NOT EXISTS ops_uow ON ops(uow, id) WHERE uow IS NOT NULL;");
        _count = Scalar("SELECT count(*) FROM ops");
        _bytes = Scalar("SELECT coalesce(sum(size), 0) FROM ops");
    }

    /// <summary>A transaction scope that is a no-op inside <see cref="Transact"/> (SQLite has no nested ones; the outer one commits).</summary>
    private sealed class Scope(SqliteTransaction? tx) : IDisposable
    {
        public void Commit() => tx?.Commit();
        public void Dispose() => tx?.Dispose();
    }

    private Scope Begin() => new(_inTx ? null : _write.BeginTransaction());

    /// <summary>Row and byte counters: held back while an outer transaction might still roll back.</summary>
    private void Counted(long rows, long bytes)
    {
        if (_inTx) { _txCount += rows; _txBytes += bytes; return; }
        Interlocked.Add(ref _count, rows);
        Interlocked.Add(ref _bytes, bytes);
    }

    /// <summary>One atomic write of whatever Append/Update/Upsert/Touch calls <paramref name="work"/> makes (CI-3).</summary>
    public void Transact(Action work)
    {
        lock (_writeLock)
        {
            using var tx = _write.BeginTransaction();
            _inTx = true;
            _txCount = _txBytes = 0;
            try
            {
                work();
                tx.Commit();
                Interlocked.Add(ref _count, _txCount);
                Interlocked.Add(ref _bytes, _txBytes);
            }
            finally { _inTx = false; }
        }
    }

    private void Exec(string sql)
    {
        using var c = _write.CreateCommand();
        c.CommandText = sql;
        c.ExecuteNonQuery();
    }

    private long Scalar(string sql)
    {
        using var c = _write.CreateCommand();
        c.CommandText = sql;
        return Convert.ToInt64(c.ExecuteScalar() ?? 0L);
    }

    private SqliteConnection Read()
    {
        var c = new SqliteConnection(_readCs);
        c.Open();
        return c;
    }

    public long Count => Interlocked.Read(ref _count);

    // ---- operations ------------------------------------------------------------------------

    private const string InsertSql = """
        INSERT OR IGNORE INTO ops (id, ts, command, coll, status, app, driver, latency, shape, value_hash, reply_count, alerts, size, raw, ctx, uow)
        VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11, ?12, ?13, ?14, ?15, ?16)
        """;

    /// <summary>The op insert, prepared once (SQLITE_PREPARE_PERSISTENT) and bound through SQLitePCL directly (write lock held).</summary>
    private sqlite3_stmt? _insert;

    /// <summary>
    /// The hot write: every stored op. Bound straight through SQLitePCL on a statement prepared once: Microsoft.Data.Sqlite
    /// allocated a parameter binder per parameter per row, boxed every integer, and encoded every string into a new array
    /// (1.2 KB a row). Strings go through a stack buffer, blobs as spans; SQLite copies both (SQLITE_TRANSIENT).
    /// </summary>
    public void Append(IReadOnlyList<Operation> ops)
    {
        lock (_writeLock)
        {
            var db = _write.Handle!;
            if (_insert == null)
                Check(raw.sqlite3_prepare_v3(db, InsertSql, 1 /* SQLITE_PREPARE_PERSISTENT */, out _insert), db);
            var st = _insert;
            using var tx = Begin();
            long inserted = 0, bytes = 0;
            try
            {
                foreach (var o in ops)
                {
                    var r = o.Op;
                    var ctx = Volatile.Read(ref o.Ctx);
                    var size = (r.Raw?.Length ?? 0) + (ctx?.Raw?.Length ?? 0) + RowOverhead;
                    Text(st, 1, o.Id, db);
                    Check(raw.sqlite3_bind_int64(st, 2, r.Ts), db);
                    Text(st, 3, r.Command, db);
                    Text(st, 4, r.Coll, db);
                    Text(st, 5, r.Status, db);
                    Text(st, 6, o.App, db);
                    Text(st, 7, o.Driver, db);
                    if (r.ServerLatencyUs is { } lat) Check(raw.sqlite3_bind_int64(st, 8, lat), db); else Check(raw.sqlite3_bind_null(st, 8), db);
                    Text(st, 9, o.ShapeHash, db);
                    Text(st, 10, o.ValueHash, db);
                    Check(raw.sqlite3_bind_int64(st, 11, o.ReplyCount), db);
                    Text(st, 12, o.Alerts is { Count: > 0 } ? string.Join(',', o.Alerts) : null, db);
                    Check(raw.sqlite3_bind_int64(st, 13, size), db);
                    Blob(st, 14, r.Raw, db);
                    Blob(st, 15, ctx?.Raw, db);
                    Text(st, 16, ctx?.UowId, db);
                    var rc = raw.sqlite3_step(st);
                    if (rc != raw.SQLITE_DONE) SqliteException.ThrowExceptionForRC(rc, db);
                    var n = raw.sqlite3_changes(db);
                    raw.sqlite3_reset(st);
                    inserted += n;
                    bytes += n * size;
                }
                tx.Commit();
            }
            finally
            {
                raw.sqlite3_reset(st); // a failed step leaves it mid-execution: reset before the rollback
            }
            Counted(inserted, bytes);
        }
    }

    private static void Check(int rc, sqlite3 db)
    {
        if (rc != raw.SQLITE_OK) SqliteException.ThrowExceptionForRC(rc, db);
    }

    private static void Text(sqlite3_stmt st, int index, string? v, sqlite3 db)
    {
        if (v == null) { Check(raw.sqlite3_bind_null(st, index), db); return; }
        // an empty span pins to a null pointer, which SQLite binds as NULL: "" goes through the string overload
        if (v.Length == 0 || v.Length > 128) { Check(raw.sqlite3_bind_text(st, index, v), db); return; }
        Span<byte> utf8 = stackalloc byte[128 * 3];
        Check(raw.sqlite3_bind_text(st, index, utf8[..Encoding.UTF8.GetBytes(v, utf8)]), db);
    }

    private static void Blob(sqlite3_stmt st, int index, byte[]? v, sqlite3 db) =>
        Check(v is { Length: > 0 } ? raw.sqlite3_bind_blob(st, index, v) : v == null ? raw.sqlite3_bind_null(st, index) : raw.sqlite3_bind_zeroblob(st, index, 0), db);

    public Operation? Get(string id)
    {
        using var c = Read();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM ops WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        using var rd = cmd.ExecuteReader();
        return rd.Read() ? Materialize(rd) : null;
    }

    /// <summary>
    /// Rows a min-latency query reads through the primary key, newest first, before it falls back to the latency index. A threshold
    /// that most rows pass fills the page long before this; a rare one is cheap through the index (FC-9).
    /// </summary>
    private const int LatencyScanBudget = 50_000;

    public List<Operation> Query(OpQuery q)
    {
        using var c = Read();
        var limit = Math.Clamp(q.Limit, 1, 1000);
        if (q.MinLatencyUs != null)
        {
            // WHERE latency >= ? ORDER BY id DESC LIMIT n plans as a search of ops_latency plus a sort of every match: it reads
            // millions of rows when the threshold is low. Walk the key backwards instead (the table is ordered by id) for a bounded
            // number of rows; only when that does not fill the page is the threshold selective enough for the index.
            var fast = Run(c, q, limit, latencyViaScan: true);
            if (q.Scanned >= limit) return fast;
        }
        return Run(c, q, limit, latencyViaScan: false);
    }

    private List<Operation> Run(SqliteConnection c, OpQuery q, int limit, bool latencyViaScan)
    {
        using var cmd = c.CreateCommand();
        var where = new List<string>();
        void Add(string clause, string name, object value) { where.Add(clause); cmd.Parameters.AddWithValue(name, value); }
        // ULID: the first 10 characters are the ms timestamp, so time bounds are key bounds
        if (q.From is { } f) Add("id >= $from", "$from", Ulid.Bound(f / 1000, low: true));
        if (q.To is { } t) Add("id <= $to", "$to", Ulid.Bound(t / 1000, low: false));
        if (q.Cursor != null) Add("id < $cursor", "$cursor", q.Cursor);
        if (q.App != null) Add("app = $app", "$app", q.App);
        if (q.Coll != null) Add("coll = $coll", "$coll", q.Coll);
        if (q.Command != null) Add("command = $command", "$command", q.Command);
        if (q.Shape != null) Add("shape = $shape", "$shape", q.Shape);
        if (q.Status != null)
        {
            Add("status = $status", "$status", q.Status);
            // SQLite uses the partial index ops_status only when the query repeats its WHERE term (CI-4)
            if (q.Status != "ok") where.Add("status <> 'ok'");
        }
        if (q.Uow != null) Add("uow = $uow", "$uow", q.Uow);
        if (q.MinLatencyUs != null && !latencyViaScan) Add("latency >= $lat", "$lat", q.MinLatencyUs);
        var filter = where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "";
        if (q.MinLatencyUs != null && latencyViaScan)
        {
            cmd.Parameters.AddWithValue("$lat", q.MinLatencyUs);
            cmd.CommandText = $"SELECT {Columns} FROM ops WHERE id IN (SELECT id FROM (SELECT id, latency FROM ops{filter} ORDER BY id DESC LIMIT {LatencyScanBudget}) " +
                              "WHERE latency >= $lat ORDER BY id DESC LIMIT $limit) ORDER BY id DESC";
        }
        else cmd.CommandText = $"SELECT {Columns} FROM ops{filter} ORDER BY id DESC LIMIT $limit";
        cmd.Parameters.AddWithValue("$limit", limit);
        var list = new List<Operation>();
        var scanned = 0;
        string? lastId = null;
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
        {
            scanned++;
            lastId = rd.GetString(0);
            if (Materialize(rd) is { } op) list.Add(op);
        }
        // a latency scan that came up short says so with Scanned < limit, even when the table was read to its end
        q.Scanned = scanned;
        q.LastId = lastId;
        return list;
    }

    private const string Columns = "id, ts, app, driver, shape, value_hash, reply_count, alerts, raw, ctx";

    /// <summary>
    /// A stored line through the ingest parser. A line an older build stored that the stricter parser now refuses for invalid UTF-8 is read
    /// again with the bad bytes replaced (U+FFFD) rather than made to vanish from queries (FC-21).
    /// </summary>
    private static bool ParseStored(byte[] raw, out Record? rec)
    {
        if (RecordParser.Parse(raw, out rec, out _) == ParseResult.Ok) return true;
        if (System.Text.Unicode.Utf8.IsValid(raw)) return false;
        var fixedUp = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(raw));
        if (RecordParser.Parse(fixedUp, out rec, out _) != ParseResult.Ok) return false;
        return true;
    }

    private static Operation? Materialize(SqliteDataReader rd)
    {
        var raw = (byte[])rd["raw"];
        if (!ParseStored(raw, out var rec) || rec is not OpRecord op) return null;
        op.Ts = rd.GetInt64(1); // the stored ts (clamped at ingest if it was in the future)
        CmdRecord? ctx = null;
        if (!rd.IsDBNull(9) && ParseStored((byte[])rd[9], out var c)) ctx = c as CmdRecord;
        return new Operation
        {
            Id = rd.GetString(0),
            Op = op,
            App = rd.IsDBNull(2) ? null : rd.GetString(2),
            Driver = rd.IsDBNull(3) ? null : rd.GetString(3),
            ShapeHash = rd.GetString(4),
            ValueHash = rd.IsDBNull(5) ? null : rd.GetString(5),
            ReplyCount = rd.GetInt32(6),
            Alerts = rd.IsDBNull(7) ? null : [.. rd.GetString(7).Split(',')],
            Ctx = ctx,
        };
    }

    public void Update(IReadOnlyList<OpUpdate> updates)
    {
        if (updates.Count == 0) return;
        lock (_writeLock)
        {
            using var tx = Begin();
            using var cmd = _write.CreateCommand();
            cmd.CommandText = """
                UPDATE ops SET reply_count = coalesce($rc, reply_count), alerts = coalesce($alerts, alerts),
                  ctx = coalesce($ctx, ctx), uow = coalesce($uow, uow), size = size + $grow
                WHERE id = $id
                """;
            var p = new Dictionary<string, SqliteParameter>();
            foreach (var n in (string[])["$rc", "$alerts", "$ctx", "$uow", "$grow", "$id"]) p[n] = cmd.Parameters.Add(n, SqliteType.Text);
            cmd.Prepare();
            long bytes = 0;
            foreach (var u in updates)
            {
                var grow = u.Ctx?.Raw?.Length ?? 0;
                p["$rc"].Value = (object?)u.ReplyCount ?? DBNull.Value;
                p["$alerts"].Value = u.Alerts is { Count: > 0 } ? string.Join(',', u.Alerts) : DBNull.Value;
                p["$ctx"].Value = (object?)u.Ctx?.Raw ?? DBNull.Value;
                p["$uow"].Value = (object?)u.Ctx?.UowId ?? DBNull.Value;
                p["$grow"].Value = grow;
                p["$id"].Value = u.Id;
                bytes += cmd.ExecuteNonQuery() * grow;
            }
            tx.Commit();
            Counted(0, bytes);
        }
    }

    public int Prune(long olderThanUs, long maxBytes, Action? progress = null)
    {
        // a chunk per write-lock hold: a day of ops is millions of rows, and the analysis stage's aggregate
        // flush waits on the same lock
        var removed = 0;
        var bound = Ulid.Bound(olderThanUs / 1000, low: true);
        int deleted;
        do
        {
            lock (_writeLock)
                deleted = Delete("id IN (SELECT id FROM ops WHERE id < $bound ORDER BY id LIMIT $n)", ("$bound", bound), ("$n", PruneChunk));
            removed += deleted;
            progress?.Invoke();
        } while (deleted == PruneChunk);
        // the WAL is disk the 03 §7 bound must count. Up to WalLimit it is working space (journal_size_limit keeps it there);
        // past that, truncate it, and charge what a busy reader kept us from reclaiming
        var wal = WalBytes();
        if (wal > WalLimit)
        {
            lock (_writeLock) Exec("PRAGMA wal_checkpoint(TRUNCATE);");
            wal = Math.Max(0, WalBytes() - WalLimit);
        }
        else wal = 0;
        // size bound: oldest first, a proportional estimate at a time (rows differ in size), until it fits
        while (Interlocked.Read(ref _bytes) + wal > maxBytes && Interlocked.Read(ref _count) > 0)
        {
            lock (_writeLock)
            {
                var excess = (double)(_bytes + wal - maxBytes) / Math.Max(1, _bytes);
                var n = (int)Math.Clamp(Math.Ceiling(_count * excess), 1, PruneChunk);
                deleted = Delete("id IN (SELECT id FROM ops ORDER BY id LIMIT $n)", ("$n", n));
            }
            removed += deleted;
            progress?.Invoke();
            if (deleted == 0) break;
        }
        if (removed > 0)
            lock (_writeLock) Exec("PRAGMA wal_checkpoint(PASSIVE);");
        return removed;
    }

    private long WalBytes() => File.Exists(_walFile) ? new FileInfo(_walFile).Length : 0;

    /// <summary>One statement per chunk: RETURNING gives the sizes of the rows it removed, so the subquery runs once (CI-20).</summary>
    private int Delete(string where, params (string Name, object Value)[] ps)
    {
        using var del = _write.CreateCommand();
        del.CommandText = $"DELETE FROM ops WHERE {where} RETURNING size";
        foreach (var p in ps) del.Parameters.AddWithValue(p.Name, p.Value);
        long rows = 0, bytes = 0;
        using (var rd = del.ExecuteReader())
            while (rd.Read()) { rows++; bytes += rd.GetInt64(0); }
        Interlocked.Add(ref _count, -rows);
        Interlocked.Add(ref _bytes, -bytes);
        return (int)rows;
    }

    /// <summary>
    /// Logical size of the stored operations (raw lines plus per-row overhead). This, not the file size, is the
    /// 03 §7 bound: SQLite reuses freed space, and index pages free only partially when old rows go.
    /// </summary>
    public long Bytes => Interlocked.Read(ref _bytes);

    // ---- aggregates ------------------------------------------------------------------------

    public void Merge(IReadOnlyList<MinuteBucket> deltas)
    {
        lock (_writeLock)
        {
            using var tx = _write.BeginTransaction();
            foreach (var d in deltas) MergeInto("agg_minute", "minute", d.ShapeHash, d.Minute, d);
            tx.Commit();
        }
    }

    private void MergeInto(string table, string col, string shape, long key, MinuteBucket delta)
    {
        using var get = _write.CreateCommand();
        get.CommandText = $"SELECT data FROM {table} WHERE shape = $s AND {col} = $k";
        get.Parameters.AddWithValue("$s", shape);
        get.Parameters.AddWithValue("$k", key);
        MinuteBucket? b = null;
        if (get.ExecuteScalar() is byte[] existing)
        {
            // FC-7: one row that no longer decodes must not stop every later write: it is replaced by the delta and counted
            try { b = Decode(existing, shape, key); }
            catch (Exception)
            {
                Interlocked.Increment(ref _undecodable);
            }
        }
        b ??= new MinuteBucket { ShapeHash = shape, Minute = key };
        b.Merge(delta);
        using var put = _write.CreateCommand();
        put.CommandText = $"INSERT OR REPLACE INTO {table} (shape, {col}, data) VALUES ($s, $k, $d)";
        put.Parameters.AddWithValue("$s", shape);
        put.Parameters.AddWithValue("$k", key);
        put.Parameters.AddWithValue("$d", Encode(b));
        put.ExecuteNonQuery();
    }

    private long _undecodable;
    /// <summary>Stored aggregate rows that could not be decoded and were replaced or skipped.</summary>
    public long UndecodableRows => Interlocked.Read(ref _undecodable);

    private static byte[] Encode(MinuteBucket b)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(b.Count); w.Write(b.Errors); w.Write(b.SumRespBytes); w.Write(b.SumReqBytes); w.Write(b.SumReturned); w.Write(b.MaxReturned);
        var sk = b.Latency.Serialize();
        w.Write(sk.Length);
        w.Write(sk);
        return ms.ToArray();
    }

    private static MinuteBucket Decode(byte[] data, string shape, long minute)
    {
        using var r = new BinaryReader(new MemoryStream(data));
        var b = new MinuteBucket { ShapeHash = shape, Minute = minute };
        b.Count = r.ReadInt64(); b.Errors = r.ReadInt64(); b.SumRespBytes = r.ReadInt64(); b.SumReqBytes = r.ReadInt64();
        b.SumReturned = r.ReadInt64(); b.MaxReturned = r.ReadInt64();
        b.Latency = Sketch.Deserialize(r.ReadBytes(r.ReadInt32()));
        return b;
    }

    /// <summary>Minute buckets in range, plus hourly ones (after rollup) reported at their hour's first minute.</summary>
    public List<MinuteBucket> Buckets(long fromMinute, long toMinute, string? shape = null)
    {
        using var c = Read();
        var list = new List<MinuteBucket>();
        foreach (var (table, col, scale) in new[] { ("agg_minute", "minute", 1L), ("agg_hour", "hour", 60L) })
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"SELECT shape, {col}, data FROM {table} WHERE {col} >= $f AND {col} <= $t" + (shape != null ? " AND shape = $s" : "");
            cmd.Parameters.AddWithValue("$f", scale == 1 ? fromMinute : fromMinute / 60);
            cmd.Parameters.AddWithValue("$t", scale == 1 ? toMinute : toMinute / 60);
            if (shape != null) cmd.Parameters.AddWithValue("$s", shape);
            using var rd = cmd.ExecuteReader();
            while (rd.Read())
            {
                try { list.Add(Decode((byte[])rd[2], rd.GetString(0), rd.GetInt64(1) * scale)); }
                catch (Exception) { Interlocked.Increment(ref _undecodable); }
            }
        }
        return list;
    }

    public void Rollup(long nowUs)
    {
        var nowMinute = nowUs / 60_000_000;
        var cutoff = nowMinute - 48 * 60;
        lock (_writeLock)
        {
            var old = new List<MinuteBucket>();
            using (var cmd = _write.CreateCommand())
            {
                cmd.CommandText = "SELECT shape, minute, data FROM agg_minute WHERE minute < $c";
                cmd.Parameters.AddWithValue("$c", cutoff);
                using var rd = cmd.ExecuteReader();
                while (rd.Read())
                {
                    try { old.Add(Decode((byte[])rd[2], rd.GetString(0), rd.GetInt64(1))); }
                    catch (Exception) { Interlocked.Increment(ref _undecodable); } // dropped with the rows below
                }
            }
            using var tx = _write.BeginTransaction();
            foreach (var g in old.GroupBy(b => (b.ShapeHash, Hour: b.Minute / 60)))
            {
                var sum = new MinuteBucket { ShapeHash = g.Key.ShapeHash, Minute = g.Key.Hour };
                foreach (var b in g) sum.Merge(b);
                MergeInto("agg_hour", "hour", g.Key.ShapeHash, g.Key.Hour, sum);
            }
            using (var del = _write.CreateCommand())
            {
                del.CommandText = "DELETE FROM agg_minute WHERE minute < $c; DELETE FROM agg_hour WHERE hour < $h";
                del.Parameters.AddWithValue("$c", cutoff);
                del.Parameters.AddWithValue("$h", (nowMinute - 30 * 24 * 60) / 60);
                del.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    public void UpsertShapes(IReadOnlyList<ShapeInfo> shapes)
    {
        lock (_writeLock)
        {
            using var tx = _write.BeginTransaction();
            foreach (var s in shapes)
            {
                using var cmd = _write.CreateCommand();
                cmd.CommandText = "INSERT OR REPLACE INTO shapes (hash, data) VALUES ($h, $d)";
                cmd.Parameters.AddWithValue("$h", s.ShapeHash);
                cmd.Parameters.AddWithValue("$d", JsonSerializer.Serialize(s, StoreJson.Default.ShapeInfo));
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    public ShapeInfo? Shape(string hash)
    {
        using var c = Read();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT data FROM shapes WHERE hash = $h";
        cmd.Parameters.AddWithValue("$h", hash);
        return cmd.ExecuteScalar() is string json ? JsonSerializer.Deserialize(json, StoreJson.Default.ShapeInfo) : null;
    }

    public List<ShapeInfo> Shapes()
    {
        using var c = Read();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT data FROM shapes";
        var list = new List<ShapeInfo>();
        using var rd = cmd.ExecuteReader();
        while (rd.Read()) list.Add(JsonSerializer.Deserialize(rd.GetString(0), StoreJson.Default.ShapeInfo)!);
        return list;
    }

    public int PruneShapes(long lastSeenBeforeUs)
    {
        lock (_writeLock)
        {
            using var cmd = _write.CreateCommand();
            cmd.CommandText = "DELETE FROM shapes WHERE coalesce(json_extract(data, '$.LastSeen'), 0) < $t";
            cmd.Parameters.AddWithValue("$t", lastSeenBeforeUs);
            return cmd.ExecuteNonQuery();
        }
    }

    // ---- alerts ----------------------------------------------------------------------------

    public void Delete(IReadOnlyCollection<string> ids)
    {
        if (ids.Count == 0) return;
        lock (_writeLock)
        {
            using var tx = _write.BeginTransaction();
            using var cmd = _write.CreateCommand();
            cmd.CommandText = "DELETE FROM alerts WHERE id = $id";
            var p = cmd.Parameters.Add("$id", SqliteType.Text);
            foreach (var id in ids) { p.Value = id; cmd.ExecuteNonQuery(); }
            tx.Commit();
        }
    }

    public void Upsert(IReadOnlyList<Alert> alerts)
    {
        lock (_writeLock)
        {
            using var tx = _write.BeginTransaction();
            foreach (var a in alerts)
            {
                using var cmd = _write.CreateCommand();
                cmd.CommandText = "INSERT OR REPLACE INTO alerts (id, data) VALUES ($id, $d)";
                cmd.Parameters.AddWithValue("$id", a.Id);
                cmd.Parameters.AddWithValue("$d", JsonSerializer.Serialize(a, StoreJson.Default.Alert));
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    public List<Alert> All()
    {
        using var c = Read();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT data FROM alerts";
        var list = new List<Alert>();
        using var rd = cmd.ExecuteReader();
        while (rd.Read()) list.Add(JsonSerializer.Deserialize(rd.GetString(0), StoreJson.Default.Alert)!);
        return list;
    }

    // ---- callsites -------------------------------------------------------------------------

    public void Upsert(IReadOnlyList<CallsiteRecord> callsites, long nowUs)
    {
        if (callsites.Count == 0) return;
        lock (_writeLock)
        {
            using var tx = Begin();
            foreach (var c in callsites)
            {
                using var cmd = _write.CreateCommand();
                cmd.CommandText = "INSERT OR REPLACE INTO callsites (hash, data, last_used) VALUES ($h, $d, $t)";
                cmd.Parameters.AddWithValue("$h", c.Callsite);
                cmd.Parameters.AddWithValue("$d", JsonSerializer.Serialize(new StoredCallsite(c.Lang, c.Frames.Select(f => new StoredFrame(f.Fn, f.File, f.Line)).ToList()), StoreJson.Default.StoredCallsite));
                cmd.Parameters.AddWithValue("$t", nowUs);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    public void Touch(IReadOnlyCollection<string> hashes, long nowUs)
    {
        if (hashes.Count == 0) return;
        lock (_writeLock)
        {
            using var tx = Begin();
            using var cmd = _write.CreateCommand();
            cmd.CommandText = "UPDATE callsites SET last_used = $t WHERE hash = $h";
            var h = cmd.Parameters.Add("$h", SqliteType.Text);
            cmd.Parameters.AddWithValue("$t", nowUs);
            foreach (var x in hashes) { h.Value = x; cmd.ExecuteNonQuery(); }
            tx.Commit();
        }
    }

    public CallsiteRecord? Callsite(string hash)
    {
        using var c = Read();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT data FROM callsites WHERE hash = $h";
        cmd.Parameters.AddWithValue("$h", hash);
        if (cmd.ExecuteScalar() is not string json || JsonSerializer.Deserialize(json, StoreJson.Default.StoredCallsite) is not { } s) return null;
        return new CallsiteRecord { Callsite = hash, Lang = s.Lang, Frames = s.Frames.Select(f => (f.Fn, f.File, f.Line)).ToList() };
    }

    public int PruneCallsites(long unusedSinceUs)
    {
        lock (_writeLock)
        {
            using var cmd = _write.CreateCommand();
            cmd.CommandText = "DELETE FROM callsites WHERE last_used < $t";
            cmd.Parameters.AddWithValue("$t", unusedSinceUs);
            return cmd.ExecuteNonQuery();
        }
    }

    public void Dispose()
    {
        lock (_writeLock)
        {
            _insert?.Dispose(); // sqlite3_finalize: before the connection closes
            _write.Dispose();
        }
        SqliteConnection.ClearAllPools();
    }
}
