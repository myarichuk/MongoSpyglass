using System.Security.Cryptography;

namespace Spyglass.Collector.Model;

/// <summary>
/// The stored entity (03 §3.3). M1 is wire-only: built from an <c>op</c> record plus its connection's
/// <c>conn</c> metadata; the exporter fields arrive with the M2 join.
/// </summary>
public sealed class Operation
{
    public required string Id;
    public required OpRecord Op;

    // enrichment
    public string? App, Driver;
    public string ShapeHash = "";
    public string? ValueHash;
    public string? ShapeJson;
    public int InCardinality;
    /// <summary>getMore: the cursor id from the command; killCursors: the ids listed.</summary>
    public long? GetMoreCursorId;
    public List<long>? KillCursorIds;
    /// <summary>find/count/aggregate without a filter/limit or a leading $match/$limit (03 §5.2 unbounded-scan input).</summary>
    public bool Unbounded;
    /// <summary>
    /// Waits on the server by design: hello-family monitoring (streaming hello holds for maxAwaitTimeMS),
    /// tailable/awaitData cursors and change streams, and their getMores. Not slow, whatever its latency.
    /// </summary>
    public bool AwaitsByDesign;
    /// <summary>"db.coll", or "db" for commands with no collection.</summary>
    public string Ns => Op.Coll is { } c ? $"{Op.Db}.{c}" : Op.Db ?? "";
    public int ReplyCount;
    /// <summary>Alert rules this op raised (03 §8 op detail "linked alerts").</summary>
    public List<string>? Alerts;

    public long Ts => Op.Ts;
    public long? ServerLatencyUs => Op.ServerLatencyUs;
    public bool IsError => Op.Status == "error";
    /// <summary>
    /// Failed from the application's point of view: ok:0, or ok:1 with write errors or a write-concern error
    /// (a duplicate key is ok:1 + writeErrors). What error-burst and the aggregates' error counts use.
    /// </summary>
    public bool Failed => IsError || Op.Reply is { WriteErrors: > 0 } or { WriteConcernError: true };
    public string? FailureName => Op.Reply switch
    {
        { CodeName: { } cn } => cn,
        { Code: { } code } => code.ToString(System.Globalization.CultureInfo.InvariantCulture),
        { WriteErrors: > 0 } => "WriteError",
        { WriteConcernError: true } => "WriteConcernError",
        _ => null,
    };
}

/// <summary>ULIDs (Crockford base32, 48-bit ms time + 80 random bits): sortable by creation time.</summary>
public static class Ulid
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    /// <summary>The smallest (or largest) ULID of millisecond <paramref name="unixMs"/>: time ranges as key ranges.</summary>
    public static string Bound(long unixMs, bool low)
    {
        Span<char> c = stackalloc char[26];
        var t = (ulong)Math.Max(0, unixMs);
        for (var i = 9; i >= 0; i--) { c[i] = Alphabet[(int)(t & 31)]; t >>= 5; }
        c[10..].Fill(low ? '0' : 'Z');
        return new string(c);
    }

    public static string New(long unixMs)
    {
        Span<byte> b = stackalloc byte[16];
        for (var i = 5; i >= 0; i--) { b[i] = (byte)unixMs; unixMs >>= 8; }
        RandomNumberGenerator.Fill(b[6..]);
        Span<char> c = stackalloc char[26];
        // 128 bits → 26 chars, 5 bits each, most significant first (the first char carries 3 bits)
        var hi = ((UInt128)System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(b) << 64) | System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(b[8..]);
        for (var i = 25; i >= 0; i--) { c[i] = Alphabet[(int)(hi & 31)]; hi >>= 5; }
        return new string(c);
    }
}
