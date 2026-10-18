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

/// <summary>An exporter record (02 §4.3). M1 is wire-only: these are accepted and counted, joined in M2.</summary>
public sealed class ContextRecord : Record
{
    public string Type = "";
}
