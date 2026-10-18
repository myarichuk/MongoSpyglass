using System.Text.Json;
using Microsoft.Data.Sqlite;
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
public sealed class SqliteStore : IOperationStore, IAggregateStore, IAlertStore, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { IncludeFields = true };
    private const int PruneChunk = 5000;

    private readonly string _readCs;
    private readonly SqliteConnection _write;
    private readonly object _writeLock = new();
    private long _count, _bytes;
    /// <summary>Per-row overhead beyond the raw line: columns and the five index entries.</summary>
    private const int RowOverhead = 256;

    public SqliteStore(string directory)
    {
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "spyglass.db");
        var cs = new SqliteConnectionStringBuilder { DataSource = file, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = true }.ToString();
        _readCs = new SqliteConnectionStringBuilder(cs) { Mode = SqliteOpenMode.ReadOnly }.ToString();
        _write = new SqliteConnection(cs);
        _write.Open();
        // the ops table carries five secondary indexes: a 64 MiB page cache keeps their hot pages resident
        Exec("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000; PRAGMA foreign_keys=OFF; " +
             "PRAGMA cache_size=-65536; PRAGMA mmap_size=268435456; PRAGMA temp_store=MEMORY;");
        Exec("""
            CREATE TABLE IF NOT EXISTS ops (
              id TEXT PRIMARY KEY, ts INTEGER NOT NULL, command TEXT NOT NULL, coll TEXT, status TEXT NOT NULL,
              app TEXT, driver TEXT, latency INTEGER, shape TEXT NOT NULL, value_hash TEXT, reply_count INTEGER NOT NULL,
              alerts TEXT, size INTEGER NOT NULL, raw BLOB NOT NULL) WITHOUT ROWID;
            CREATE INDEX IF NOT EXISTS ops_shape ON ops(shape, id);
            CREATE INDEX IF NOT EXISTS ops_app ON ops(app, id);
            CREATE INDEX IF NOT EXISTS ops_coll ON ops(coll, id);
            CREATE INDEX IF NOT EXISTS ops_command ON ops(command, id);
            CREATE INDEX IF NOT EXISTS ops_status ON ops(status, id) WHERE status <> 'ok';
            CREATE TABLE IF NOT EXISTS agg_minute (shape TEXT NOT NULL, minute INTEGER NOT NULL, data BLOB NOT NULL, PRIMARY KEY(shape, minute)) WITHOUT ROWID;
            CREATE INDEX IF NOT EXISTS agg_minute_t ON agg_minute(minute);
            CREATE TABLE IF NOT EXISTS agg_hour (shape TEXT NOT NULL, hour INTEGER NOT NULL, data BLOB NOT NULL, PRIMARY KEY(shape, hour)) WITHOUT ROWID;
            CREATE INDEX IF NOT EXISTS agg_hour_t ON agg_hour(hour);
            CREATE TABLE IF NOT EXISTS shapes (hash TEXT PRIMARY KEY, data TEXT NOT NULL) WITHOUT ROWID;
            CREATE TABLE IF NOT EXISTS alerts (id TEXT PRIMARY KEY, data TEXT NOT NULL) WITHOUT ROWID;
            """);
        _count = Scalar("SELECT count(*) FROM ops");
        _bytes = Scalar("SELECT coalesce(sum(size), 0) FROM ops");
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

    public void Append(IReadOnlyList<Operation> ops)
    {
        lock (_writeLock)
        {
            using var tx = _write.BeginTransaction();
            using var cmd = _write.CreateCommand();
            cmd.CommandText = """
                INSERT OR IGNORE INTO ops (id, ts, command, coll, status, app, driver, latency, shape, value_hash, reply_count, alerts, size, raw)
                VALUES ($id, $ts, $command, $coll, $status, $app, $driver, $latency, $shape, $vh, $rc, $alerts, $size, $raw)
                """;
            var p = new Dictionary<string, SqliteParameter>();
            foreach (var n in (string[])["$id", "$ts", "$command", "$coll", "$status", "$app", "$driver", "$latency", "$shape", "$vh", "$rc", "$alerts", "$size", "$raw"])
                p[n] = cmd.Parameters.Add(n, SqliteType.Text);
            cmd.Prepare();
            long inserted = 0, bytes = 0;
            foreach (var o in ops)
            {
                var size = (o.Op.Raw?.Length ?? 0) + RowOverhead;
                var r = o.Op;
                p["$id"].Value = o.Id;
                p["$ts"].Value = r.Ts;
                p["$command"].Value = r.Command;
                p["$coll"].Value = (object?)r.Coll ?? DBNull.Value;
                p["$status"].Value = r.Status;
                p["$app"].Value = (object?)o.App ?? DBNull.Value;
                p["$driver"].Value = (object?)o.Driver ?? DBNull.Value;
                p["$latency"].Value = (object?)r.ServerLatencyUs ?? DBNull.Value;
                p["$shape"].Value = o.ShapeHash;
                p["$vh"].Value = (object?)o.ValueHash ?? DBNull.Value;
                p["$rc"].Value = o.ReplyCount;
                p["$alerts"].Value = o.Alerts is { Count: > 0 } ? string.Join(',', o.Alerts) : DBNull.Value;
                p["$raw"].Value = (object?)r.Raw ?? DBNull.Value;
                p["$size"].Value = size;
                var n = cmd.ExecuteNonQuery();
                inserted += n;
                bytes += n * size;
            }
            tx.Commit();
            Interlocked.Add(ref _count, inserted);
            Interlocked.Add(ref _bytes, bytes);
        }
    }

    public Operation? Get(string id)
    {
        using var c = Read();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, ts, app, driver, shape, value_hash, reply_count, alerts, raw FROM ops WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        using var rd = cmd.ExecuteReader();
        return rd.Read() ? Materialize(rd) : null;
    }

    public List<Operation> Query(OpQuery q)
    {
        using var c = Read();
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
        if (q.Status != null) Add("status = $status", "$status", q.Status);
        if (q.MinLatencyUs != null) Add("latency >= $lat", "$lat", q.MinLatencyUs);
        cmd.CommandText = "SELECT id, ts, app, driver, shape, value_hash, reply_count, alerts, raw FROM ops" +
                          (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "") + " ORDER BY id DESC LIMIT $limit";
        cmd.Parameters.AddWithValue("$limit", Math.Clamp(q.Limit, 1, 1000));
        var list = new List<Operation>();
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
            if (Materialize(rd) is { } op) list.Add(op);
        return list;
    }

    private static Operation? Materialize(SqliteDataReader rd)
    {
        var raw = (byte[])rd["raw"];
        if (RecordParser.Parse(raw, out var rec, out _) != ParseResult.Ok || rec is not OpRecord op) return null;
        op.Ts = rd.GetInt64(1); // the stored ts (clamped at ingest if it was in the future)
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
        };
    }

    public void Update(string id, int replyCount, IReadOnlyList<string>? alerts)
    {
        lock (_writeLock)
        {
            using var cmd = _write.CreateCommand();
            cmd.CommandText = "UPDATE ops SET reply_count = $rc, alerts = coalesce($alerts, alerts) WHERE id = $id";
            cmd.Parameters.AddWithValue("$rc", replyCount);
            cmd.Parameters.AddWithValue("$alerts", alerts is { Count: > 0 } ? string.Join(',', alerts) : DBNull.Value);
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
    }

    public int Prune(long olderThanUs, long maxBytes)
    {
        var removed = 0;
        lock (_writeLock)
        {
            removed += Delete("id < $bound", ("$bound", Ulid.Bound(olderThanUs / 1000, low: true)));
            // size bound: oldest first, a proportional estimate at a time (rows differ in size), until it fits
            while (_bytes > maxBytes && _count > 0)
            {
                var excess = (double)(_bytes - maxBytes) / _bytes;
                var n = (int)Math.Clamp(Math.Ceiling(_count * excess), 1, PruneChunk);
                var deleted = Delete("id IN (SELECT id FROM ops ORDER BY id LIMIT $n)", ("$n", n));
                removed += deleted;
                if (deleted == 0) break;
            }
            if (removed > 0) Exec("PRAGMA wal_checkpoint(PASSIVE);");
        }
        return removed;
    }

    private int Delete(string where, (string Name, object Value) p)
    {
        using var size = _write.CreateCommand();
        size.CommandText = $"SELECT count(*), coalesce(sum(size), 0) FROM ops WHERE {where}";
        size.Parameters.AddWithValue(p.Name, p.Value);
        long rows, bytes;
        using (var rd = size.ExecuteReader()) { rd.Read(); rows = rd.GetInt64(0); bytes = rd.GetInt64(1); }
        if (rows == 0) return 0;
        using var del = _write.CreateCommand();
        del.CommandText = $"DELETE FROM ops WHERE {where}";
        del.Parameters.AddWithValue(p.Name, p.Value);
        var n = del.ExecuteNonQuery();
        Interlocked.Add(ref _count, -n);
        Interlocked.Add(ref _bytes, -bytes);
        return n;
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
        var b = get.ExecuteScalar() is byte[] existing ? Decode(existing, shape, key) : new MinuteBucket { ShapeHash = shape, Minute = key };
        b.Merge(delta);
        using var put = _write.CreateCommand();
        put.CommandText = $"INSERT OR REPLACE INTO {table} (shape, {col}, data) VALUES ($s, $k, $d)";
        put.Parameters.AddWithValue("$s", shape);
        put.Parameters.AddWithValue("$k", key);
        put.Parameters.AddWithValue("$d", Encode(b));
        put.ExecuteNonQuery();
    }

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
                list.Add(Decode((byte[])rd[2], rd.GetString(0), rd.GetInt64(1) * scale));
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
                while (rd.Read()) old.Add(Decode((byte[])rd[2], rd.GetString(0), rd.GetInt64(1)));
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
                cmd.Parameters.AddWithValue("$d", JsonSerializer.Serialize(s, JsonOptions));
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
        return cmd.ExecuteScalar() is string json ? JsonSerializer.Deserialize<ShapeInfo>(json, JsonOptions) : null;
    }

    public List<ShapeInfo> Shapes()
    {
        using var c = Read();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT data FROM shapes";
        var list = new List<ShapeInfo>();
        using var rd = cmd.ExecuteReader();
        while (rd.Read()) list.Add(JsonSerializer.Deserialize<ShapeInfo>(rd.GetString(0), JsonOptions)!);
        return list;
    }

    // ---- alerts ----------------------------------------------------------------------------

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
                cmd.Parameters.AddWithValue("$d", JsonSerializer.Serialize(a, JsonOptions));
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
        while (rd.Read()) list.Add(JsonSerializer.Deserialize<Alert>(rd.GetString(0), JsonOptions)!);
        return list;
    }

    public void Dispose()
    {
        lock (_writeLock) _write.Dispose();
        SqliteConnection.ClearAllPools();
    }
}
