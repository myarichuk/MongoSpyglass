namespace Spyglass.Collector.Model;

/// <summary>An ingested record (02 §4). Absent fields hold their 02 §4 default.</summary>
public abstract class Record
{
    public long Ts;
    public string Src = "";
    /// <summary>Unknown fields, verbatim, as a JSON object; null if there were none (03 §2).</summary>
    public string? Extra;
    /// <summary>Approximate in-memory cost, charged against the ingest queue bound.</summary>
    public int Size;
}

/// <summary>Reply facts of an <c>op</c> (02 §4.2). Absent counts are 0, absent booleans false.</summary>
public sealed class ReplyFacts
{
    public double? Ok;
    public int? Code;
    public string? CodeName;
    public string? Errmsg;
    public long? CursorId;
    public int? NReturned;
    public long? N;
    public long? NModified;
    public int WriteErrors;
    public bool WriteConcernError;
    public int Upserted;
    public int CursorsKilled;
    public int CursorsNotFound;
    public bool Parsed = true;
}

public sealed class OpRecord : Record
{
    /// <summary>The NDJSON line as received: what the SQLite store keeps (03 §2: unknown fields survive).</summary>
    public byte[]? Raw;
    public string Member = "";
    public long? ServerConnectionId;
    public int RequestId;
    public long ConnSeq;
    public string OpCode = "OP_MSG";
    public string Command = "";
    public string? Db;
    public string? Coll;
    public bool Stream;
    public string Status = "";
    public int ReqBytes;
    public int RespBytes;
    public long ReqLastUs;
    public long? RespFirstUs;
    public long? RespLastUs;
    public ReplyFacts? Reply;
    public string? Lsid;
    public long? TxnNumber;
    public string? Comment;
    public byte[]? Cmd;
    public bool CmdTruncated;
    public List<(string Identifier, int Count)>? SeqDocs;
    public string? Compressor;

    // 02 §4.2 derivations
    public string ProxyId => Src;
    public bool Ack => Status != "unacknowledged";
    public long? ServerLatencyUs => RespFirstUs is { } rf ? Math.Max(0, rf - ReqLastUs) : null;
    public bool HasReply => Status is not ("unacknowledged" or "connection_closed");
}

public sealed class ConnRecord : Record
{
    public string Event = "";
    public string Member = "";
    public long ConnSeq;
    public long? ServerConnectionId;
    public string? Client;
    public bool Tls;
    public string? App;
    public string? Driver;
    public string? DriverVersion;
    public string? Exporter;
    public string? Os;
    public string? Platform;
    public string? CloseReason;
    public long BytesIn, BytesOut;
}

public sealed class StreamRecord : Record
{
    public string Member = "";
    public long? ServerConnectionId;
    public int RequestId;
    public int Seq;
    public int RespBytes;
    public long? CursorId;
    public int? NReturned;
    public double? Ok;
}

public sealed class ProxyRecord : Record
{
    public string Version = "";
    public int Schema;
    public string Topology = "";
    public string? SetName;
    public List<(string Member, string Upstream)> Members = [];
    public string? Compression;
    public string? Values;
}

public sealed class StatsRecord : Record
{
    public Dictionary<string, long> Dropped = [];
    public long SentRecords, SentBatches;
}

/// <summary>
/// 02 §4.3 <c>cmd</c>: what the application's driver knew about one command attempt. Joined onto the proxy's
/// <c>op</c> by <see cref="JoinKey"/> (02 §6). No collection, filter, values or reply content (I6).
/// </summary>
public sealed class CmdRecord : Record
{
    /// <summary>The NDJSON line as received: stored with the op it joins, re-read through the parser.</summary>
    public byte[]? Raw;
    public string? ProxyId;
    public string? Member;
    public long? ServerConnectionId;
    public int RequestId;
    public string? OperationId;
    public string Command = "";
    public string? Db;
    public string Status = "";
    public long? ClientUs;
    public long? PoolWaitUs;
    public int? Attempt;
    public string? UowId, UowName, UowKind;
    public string? Traceparent;
    public string? Callsite;
    public long? CursorId;
    /// <summary>A server code (number) or a client-side code name: kept as text.</summary>
    public string? ErrorCode;
    public List<string>? ErrorLabels;

    /// <summary>Null when the exporter could not build the key (02 §3.3): an orphan from the start.</summary>
    public JoinKey? Key => ProxyId != null && Member != null && ServerConnectionId is { } sid ? new JoinKey(ProxyId, Member, sid, RequestId) : null;
}

/// <summary>02 §3.1 <c>(proxyId, member, serverConnectionId, requestId)</c>.</summary>
public readonly record struct JoinKey(string ProxyId, string Member, long ServerConnectionId, int RequestId);

/// <summary>02 §4.3 <c>hello</c>: one per exporter start.</summary>
public sealed class HelloRecord : Record
{
    public string Exporter = "";
    public string? Version, Runtime, Driver, Host, App, AppVersion;
    public int Schema;
    public long? Pid;
}

/// <summary>02 §4.3 <c>callsite</c>: the frames behind a callsite hash, once per hash per exporter process.</summary>
public sealed class CallsiteRecord : Record
{
    public string Callsite = "";
    public string? Lang;
    public List<(string Fn, string? File, int? Line)> Frames = [];
}

/// <summary>02 §4.3 <c>uow</c> and <c>pool</c>: accepted and counted; the analyzers that read them are M3 (03 §5.3).</summary>
public sealed class ContextRecord : Record
{
    public string Type = "";
}
