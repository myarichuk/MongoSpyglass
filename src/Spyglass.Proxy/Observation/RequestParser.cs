using System.Buffers.Binary;
using ZLibStream = System.IO.Compression.ZLibStream;
using System.Security.Cryptography;
using System.Text;
using Spyglass.Proxy.Config;
using Spyglass.Proxy.Wire;

namespace Spyglass.Proxy.Observation;

/// <summary>What the observer keeps of a request after its capture slot is released.</summary>
internal sealed class RequestInfo
{
    public int RequestId;
    public int OpCode;
    public int OriginalOpCode;
    public MsgFlags Flags;
    public long TFirst, TLast;
    public int FrameLength;
    public string Command = "(unknown)";
    public string? Db;
    public string? Coll;
    public string? Lsid;
    public long? TxnNumber;
    public string? Comment;
    public long? GetMoreCursorId;
    public List<long>? KillCursorIds;
    public byte[]? RedactedCommand;
    public bool CommandTruncated;
    public List<(string Identifier, int Count)>? SeqDocs;
    public bool IsHello;
    public bool IsRedactedCommand;
    public ClientMetadata? Client;
    public string? Compressor;

    public bool MoreToCome => (Flags & MsgFlags.MoreToCome) != 0;
}

internal sealed class ClientMetadata
{
    public string? Application;
    public string? Driver;
    public string? DriverVersion;
    public string? OsType;
    public string? Platform;
    public string? Exporter;
}

internal static class RequestParser
{
    /// <summary>compression=observe never inflates more than this per frame (I4).</summary>
    private const int MaxObservedInflate = 16 * 1024 * 1024;

    public static RequestInfo Parse(FrameCapture c, Redactor redactor, CompressionMode compression)
    {
        var info = new RequestInfo
        {
            RequestId = c.RequestId,
            OpCode = c.OpCode,
            OriginalOpCode = c.OpCode,
            Flags = c.Flags,
            TFirst = c.TFirst,
            TLast = c.TLast,
            FrameLength = c.FrameLength,
            IsHello = c.Kind == RequestKind.Hello,
        };

        ReadOnlySpan<byte> body = c.Buffer.AsSpan(0, c.CapturedLength);
        var truncated = c.Truncated;
        var opCode = c.OpCode;
        byte[]? inflated = null;

        if (opCode == OpCodes.Compressed)
        {
            if (body.Length < 9)
                return info;
            info.OriginalOpCode = BinaryPrimitives.ReadInt32LittleEndian(body);
            var compressorId = body[8];
            info.Compressor = compressorId switch { 0 => "noop", 1 => "snappy", 2 => "zlib", 3 => "zstd", _ => "unknown" };
            info.Command = "(compressed)";
            if (compression != CompressionMode.Observe || truncated || compressorId is not (0 or 2))
                return info;
            var uncompressedSize = BinaryPrimitives.ReadInt32LittleEndian(body[4..]);
            if (uncompressedSize is < 0 or > MaxObservedInflate)
                return info;
            inflated = new byte[uncompressedSize];
            if (compressorId == 0)
                body[9..].CopyTo(inflated);
            else
            {
                using var z = new ZLibStream(new MemoryStream(body[9..].ToArray()), System.IO.Compression.CompressionMode.Decompress);
                z.ReadExactly(inflated);
            }
            body = inflated;
            opCode = info.OriginalOpCode;
            if (opCode == OpCodes.Msg && body.Length >= 4)
                info.Flags = (MsgFlags)BinaryPrimitives.ReadUInt32LittleEndian(body);
        }

        ReadOnlySpan<byte> doc;
        string? dbFromNs = null;
        var sequencesStart = -1;
        if (opCode == OpCodes.Msg)
        {
            // flags(4) kind(1) doc
            if (body.Length < 4 + 1 + 4 || body[4] != 0)
                return info;
            doc = body[5..];
            var declared = Bson.DeclaredLength(doc);
            if (declared > 0 && declared < doc.Length)
            {
                sequencesStart = 5 + declared;
                doc = doc[..declared];
            }
        }
        else if (opCode == OpCodes.Query)
        {
            if (body.Length < 5) return info;
            var nul = body[4..].IndexOf((byte)0);
            if (nul < 0) return info;
            var ns = Encoding.UTF8.GetString(body.Slice(4, nul));
            var p = 4 + nul + 1 + 8;
            if (p >= body.Length) return info;
            doc = body[p..];
            var dot = ns.IndexOf('.');
            dbFromNs = dot > 0 ? ns[..dot] : ns;
            if (!ns.EndsWith(".$cmd", StringComparison.Ordinal))
            {
                info.Command = "(legacy query)";
                info.Db = dbFromNs;
                info.Coll = dot > 0 ? ns[(dot + 1)..] : null;
                return info;
            }
            // unwrap {$query: {...}}
            foreach (var e in Bson.Elements(doc))
            {
                if ((e.NameIs("$query"u8) || e.NameIs("query"u8)) && e.Type == BsonType.Document)
                    doc = e.Value;
                break;
            }
        }
        else
        {
            info.Command = "(" + OpCodes.Name(opCode) + ")";
            return info;
        }

        ExtractCommandFields(doc, info);
        info.Db ??= dbFromNs;

        if (info.IsHello)
            info.Client = ParseClient(doc);

        // kind-1 sequences: counts come from the streaming scanner (all documents), the first document from the capture
        if (c.Sequences.SectionCount > 0 && inflated == null)
        {
            info.SeqDocs = [];
            for (var i = 0; i < c.Sequences.SectionCount; i++)
                info.SeqDocs.Add((Encoding.UTF8.GetString(c.Sequences.Identifiers[i], 0, c.Sequences.IdentifierLengths[i]), c.Sequences.DocCounts[i]));
        }

        var firstSeqDocs = sequencesStart > 0 ? FirstSequenceDocuments(body[sequencesStart..]) : null;
        (info.RedactedCommand, info.CommandTruncated, info.IsRedactedCommand) =
            redactor.Redact(info.Command, doc, firstSeqDocs, isHello: info.IsHello, captureTruncated: truncated);
        return info;
    }

    private static void ExtractCommandFields(ReadOnlySpan<byte> doc, RequestInfo info)
    {
        var first = true;
        foreach (var e in Bson.Elements(doc))
        {
            if (first)
            {
                first = false;
                info.Command = e.NameString;
                if (e.Type == BsonType.String)
                    info.Coll = e.AsString();
                else if (info.Command == "getMore")
                    info.GetMoreCursorId = e.AsInt64();
                continue;
            }
            if (e.NameIs("$db"u8)) info.Db = e.AsString();
            else if (e.NameIs("collection"u8) && info.Command == "getMore") info.Coll = e.AsString();
            else if (e.NameIs("txnNumber"u8)) info.TxnNumber = e.AsInt64();
            else if (e.NameIs("comment"u8) && e.Type == BsonType.String) info.Comment = Truncate(e.AsString()!, 256);
            else if (e.NameIs("lsid"u8) && e.Type == BsonType.Document) info.Lsid = HashLsid(e.Value);
            else if (e.NameIs("cursors"u8) && e.Type == BsonType.Array && info.Command == "killCursors")
            {
                info.KillCursorIds = [];
                foreach (var id in Bson.Elements(e.Value))
                    if (id.AsInt64() is { } v) info.KillCursorIds.Add(v);
            }
        }
    }

    /// <summary>01 §9.2: <c>lsid.id</c> becomes a 16-hex SHA-256 prefix, so sessions group without exporting the UUID.</summary>
    public static string? HashLsid(ReadOnlySpan<byte> lsidDoc)
    {
        foreach (var e in Bson.Elements(lsidDoc))
        {
            if (!e.NameIs("id"u8) || e.Type != BsonType.Binary || e.Value.Length < 5) continue;
            Span<byte> hash = stackalloc byte[32];
            SHA256.HashData(e.Value[5..], hash);
            return Convert.ToHexStringLower(hash[..8]);
        }
        return null;
    }

    private static ClientMetadata? ParseClient(ReadOnlySpan<byte> doc)
    {
        if (!Bson.TryGetElement(doc, "client"u8, out var client) || client.Type != BsonType.Document)
            return null;
        var m = new ClientMetadata();
        foreach (var e in Bson.Elements(client.Value))
        {
            if (e.NameIs("application"u8) && e.Type == BsonType.Document && Bson.TryGetElement(e.Value, "name"u8, out var an))
                m.Application = an.AsString();
            else if (e.NameIs("driver"u8) && e.Type == BsonType.Document)
            {
                if (Bson.TryGetElement(e.Value, "name"u8, out var dn)) m.Driver = dn.AsString();
                if (Bson.TryGetElement(e.Value, "version"u8, out var dv)) m.DriverVersion = dv.AsString();
            }
            else if (e.NameIs("os"u8) && e.Type == BsonType.Document && Bson.TryGetElement(e.Value, "type"u8, out var os))
                m.OsType = os.AsString();
            else if (e.NameIs("platform"u8)) m.Platform = e.AsString();
        }
        // 02 §2.4: exporter marker is a |-separated driver name token
        if (m.Driver != null)
            foreach (var token in m.Driver.Split('|'))
                if (IsExporterToken(token))
                    m.Exporter = token;
        return m;
    }

    internal static bool IsExporterToken(string t)
    {
        if (!t.StartsWith("spyglass-", StringComparison.Ordinal) || t.Length == "spyglass-".Length) return false;
        foreach (var ch in t.AsSpan("spyglass-".Length))
            if (!(ch is >= 'a' and <= 'z' or >= '0' and <= '9' or '-'))
                return false;
        return true;
    }

    /// <summary>First document of each kind-1 section present in the capture, keyed by identifier.</summary>
    private static List<(string Identifier, byte[] FirstDoc)>? FirstSequenceDocuments(ReadOnlySpan<byte> sections)
    {
        List<(string, byte[])>? result = null;
        var p = 0;
        while (p + 5 <= sections.Length && sections[p] == 1)
        {
            var size = BinaryPrimitives.ReadInt32LittleEndian(sections[(p + 1)..]);
            if (size < 4) break;
            var idStart = p + 5;
            var nul = sections[idStart..].IndexOf((byte)0);
            if (nul < 0) break;
            var id = Encoding.UTF8.GetString(sections.Slice(idStart, nul));
            var docStart = idStart + nul + 1;
            if (docStart + 4 <= sections.Length && docStart < p + 1 + size)
            {
                var docLen = BinaryPrimitives.ReadInt32LittleEndian(sections[docStart..]);
                if (docLen >= 5 && docStart + docLen <= sections.Length)
                    (result ??= []).Add((id, sections.Slice(docStart, docLen).ToArray()));
            }
            p += 1 + size;
        }
        return result;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
