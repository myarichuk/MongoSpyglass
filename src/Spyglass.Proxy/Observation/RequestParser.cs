using System.Buffers.Binary;
using ZLibStream = System.IO.Compression.ZLibStream;
using System.Security.Cryptography;
using System.Text;
using Spyglass.Proxy.Config;
using Spyglass.Proxy.Wire;

namespace Spyglass.Proxy.Observation;

/// <summary>
/// What the observer keeps of a request after its capture slot is released. Pooled per observer worker
/// (<see cref="RequestParser.Rent"/>/<see cref="RequestParser.Return"/>): one is in use from the request
/// until its op (or stream) completes.
/// </summary>
internal sealed class RequestInfo
{
    /// <summary>A pooled instance keeps a redacted body buffer up to this size; larger ones go back to the GC.</summary>
    public const int RetainedCommandBytes = 4096;

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
    public readonly List<long> KillCursorIds = [];
    /// <summary>Redacted command body (BSON), valid when <see cref="HasRedactedCommand"/>.</summary>
    public byte[] RedactedCommandBuffer = [];
    public int RedactedCommandLength = -1;
    public bool CommandTruncated;
    public readonly List<(string Identifier, int Count)> SeqDocs = [];
    public bool IsHello;
    public bool IsRedactedCommand;
    public ClientMetadata? Client;
    public string? Compressor;

    public bool MoreToCome => (Flags & MsgFlags.MoreToCome) != 0;
    public bool HasRedactedCommand => RedactedCommandLength >= 0;
    public ReadOnlySpan<byte> RedactedCommand => RedactedCommandBuffer.AsSpan(0, Math.Max(0, RedactedCommandLength));

    public void SetRedactedCommand(ReadOnlySpan<byte> bson)
    {
        if (RedactedCommandBuffer.Length < bson.Length)
            RedactedCommandBuffer = new byte[Math.Max(bson.Length, Math.Min(RetainedCommandBytes, 2 * RedactedCommandBuffer.Length))];
        bson.CopyTo(RedactedCommandBuffer);
        RedactedCommandLength = bson.Length;
    }

    public void Reset()
    {
        Command = "(unknown)";
        Db = Coll = Lsid = Comment = Compressor = null;
        TxnNumber = GetMoreCursorId = null;
        KillCursorIds.Clear();
        SeqDocs.Clear();
        RedactedCommandLength = -1;
        if (RedactedCommandBuffer.Length > RetainedCommandBytes)
            RedactedCommandBuffer = [];
        CommandTruncated = IsHello = IsRedactedCommand = false;
        Client = null;
    }
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

/// <summary>
/// Request side of the observer: capture → <see cref="RequestInfo"/>. One per observer worker; its caches
/// (interned names, lsid hashes) and its <see cref="RequestInfo"/> pool are single-threaded.
/// </summary>
internal sealed class RequestParser
{
    /// <summary>compression=observe never inflates more than this per frame (I4).</summary>
    private const int MaxObservedInflate = 16 * 1024 * 1024;
    private const int MaxPooled = 1024;

    private readonly Redactor _redactor;
    private readonly CompressionMode _compression;
    private readonly Utf8Interner _names = new();
    private readonly LsidHasher _lsids = new();
    private readonly BsonWriter _scratch = new(1024);
    private readonly List<(int IdStart, int IdLength, int DocStart, int DocLength)> _firstSeqDocs = [];
    private readonly Stack<RequestInfo> _pool = new();
    /// <summary>Reusable inflate target for compressed request bodies (dropped again when a frame made it big).</summary>
    private byte[] _inflate = [];
    private readonly byte[] _replyChunk = new byte[16 * 1024];

    public RequestParser(Redactor redactor, CompressionMode compression)
    {
        _redactor = redactor;
        _compression = compression;
    }

    internal Redactor Redactor => _redactor;
    internal Utf8Interner Names => _names;
    internal LsidHasher Lsids => _lsids;

    public RequestInfo Rent() => _pool.TryPop(out var r) ? r : new RequestInfo();

    public void Return(RequestInfo r)
    {
        if (_pool.Count >= MaxPooled) return;
        r.Reset();
        _pool.Push(r);
    }

    /// <param name="records">false when headless: nothing that only records use is extracted or built (01 §11).</param>
    public RequestInfo Parse(FrameCapture c, bool records)
    {
        try { return ParseCore(c, records); }
        finally
        {
            if (_inflate.Length > 1 << 20) _inflate = [];
        }
    }

    private RequestInfo ParseCore(FrameCapture c, bool records)
    {
        var info = Rent();
        info.RequestId = c.RequestId;
        info.OpCode = c.OpCode;
        info.OriginalOpCode = c.OpCode;
        info.Flags = c.Flags;
        info.TFirst = c.TFirst;
        info.TLast = c.TLast;
        info.FrameLength = c.FrameLength;
        info.IsHello = c.Kind == RequestKind.Hello;

        ReadOnlySpan<byte> body = c.Buffer.AsSpan(0, c.CapturedLength);
        var truncated = c.Truncated;
        var opCode = c.OpCode;
        var wasInflated = false;

        if (opCode == OpCodes.Compressed)
        {
            if (body.Length < 9)
                return info;
            info.OriginalOpCode = BinaryPrimitives.ReadInt32LittleEndian(body);
            var compressorId = body[8];
            info.Compressor = compressorId switch { 0 => "noop", 1 => "snappy", 2 => "zlib", 3 => "zstd", _ => "unknown" };
            info.Command = "(compressed)";
            if (_compression != CompressionMode.Observe || truncated || compressorId is not (0 or 2))
                return info;
            var uncompressedSize = BinaryPrimitives.ReadInt32LittleEndian(body[4..]);
            if (uncompressedSize is < 0 or > MaxObservedInflate)
                return info;
            // The declared size is the client's word: deflate can't expand more than ~1032:1, so never size the target past that.
            var target = (int)Math.Min(uncompressedSize, 1100L * (body.Length - 9) + 64);
            if (_inflate.Length < target) _inflate = new byte[target];
            int got;
            try
            {
                using var src = new MemoryStream(c.Buffer, 9, c.CapturedLength - 9, writable: false);
                using var z = compressorId == 0 ? null : new ZLibStream(src, System.IO.Compression.CompressionMode.Decompress);
                got = (z ?? (Stream)src).ReadAtLeast(_inflate.AsSpan(0, target), target, throwOnEndOfStream: false);
            }
            catch (InvalidDataException)
            {
                return info; // corrupt: stays "(compressed)"
            }
            if (got != uncompressedSize)
                return info;
            wasInflated = true;
            body = _inflate.AsSpan(0, got);
            opCode = info.OriginalOpCode;
            if (opCode == OpCodes.Msg && body.Length >= 4)
            {
                info.Flags = (MsgFlags)BinaryPrimitives.ReadUInt32LittleEndian(body);
                // kind-1 counts and the trailing $db/lsid/txnNumber come from the streaming scanner, which only saw compressed bytes
                c.Sequences.Begin(OpCodes.Msg, body.Length);
                c.Sequences.Feed(body);
            }
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
            var ns = body.Slice(4, nul);
            var p = 4 + nul + 1 + 8;
            if (p >= body.Length) return info;
            doc = body[p..];
            var dot = ns.IndexOf((byte)'.');
            dbFromNs = _names.Get(dot > 0 ? ns[..dot] : ns);
            if (!ns.EndsWith(".$cmd"u8))
            {
                info.Command = "(legacy query)";
                info.Db = dbFromNs;
                info.Coll = dot > 0 ? _names.Get(ns[(dot + 1)..]) : null;
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

        ExtractCommandFields(doc, info, records);
        if (!wasInflated)
        {
            // Fields at the end of a body longer than the capture come from the streaming scanner.
            var fields = c.Sequences.Fields;
            if (info.Db == null && fields.DbLength >= 0) info.Db = _names.Get(fields.Db.AsSpan(0, fields.DbLength));
            info.TxnNumber ??= fields.TxnNumber;
            if (records && info.Lsid == null && fields.LsidIdLength > 0) info.Lsid = _lsids.Hash(fields.LsidId.AsSpan(0, fields.LsidIdLength));
        }
        info.Db ??= dbFromNs;

        if (info.IsHello)
            info.Client = ParseClient(doc);

        // kind-1 sequences: counts come from the streaming scanner (all documents), the first document from the capture
        if (c.Sequences.SectionCount > 0)
        {
            for (var i = 0; i < c.Sequences.SectionCount; i++)
                info.SeqDocs.Add((_names.Get(c.Sequences.Identifiers[i].AsSpan(0, c.Sequences.IdentifierLengths[i])), c.Sequences.DocCounts[i]));
        }

        info.IsRedactedCommand = Redactor.RedactedCommands.Contains(info.Command);
        if (!records) // headless: the exported body is never built (01 §11)
            return info;
        _firstSeqDocs.Clear();
        var sections = sequencesStart > 0 ? body[sequencesStart..] : default;
        if (sequencesStart > 0)
            FirstSequenceDocuments(sections, _firstSeqDocs);
        _scratch.Reset();
        (info.CommandTruncated, info.IsRedactedCommand) =
            _redactor.Redact(_scratch, info.Command, doc, sections, _firstSeqDocs, _lsids, isHello: info.IsHello, captureTruncated: truncated);
        info.SetRedactedCommand(_scratch.WrittenSpan);
        return info;
    }

    private void ExtractCommandFields(ReadOnlySpan<byte> doc, RequestInfo info, bool records)
    {
        var first = true;
        foreach (var e in Bson.Elements(doc))
        {
            if (first)
            {
                first = false;
                info.Command = _names.Get(e.Name);
                if (e.Type == BsonType.String)
                    info.Coll = _names.Get(e.StringBytes);
                else if (e.NameIs("getMore"u8))
                    info.GetMoreCursorId = e.AsInt64();
                continue;
            }
            if (e.NameIs("$db"u8)) { if (e.Type == BsonType.String) info.Db = _names.Get(e.StringBytes); }
            else if (e.NameIs("collection"u8) && e.Type == BsonType.String && info.Command == "getMore") info.Coll = _names.Get(e.StringBytes);
            else if (e.NameIs("txnNumber"u8)) info.TxnNumber = e.AsInt64();
            else if (e.NameIs("cursors"u8) && e.Type == BsonType.Array && info.Command == "killCursors")
            {
                foreach (var id in Bson.Elements(e.Value))
                    if (id.AsInt64() is { } v) info.KillCursorIds.Add(v);
            }
            else if (!records) continue; // below: only records use these
            else if (e.NameIs("comment"u8) && e.Type == BsonType.String) info.Comment = ExportComment(e.StringBytes);
            else if (e.NameIs("lsid"u8) && e.Type == BsonType.Document) info.Lsid = _lsids.HashDocument(e.Value);
        }
    }

    /// <summary>01 §9.2: <c>lsid.id</c> becomes a 16-hex SHA-256 prefix, so sessions group without exporting the UUID.
    /// Uncached; the observer goes through <see cref="LsidHasher"/>.</summary>
    public static string HashLsidBytes(ReadOnlySpan<byte> uuid)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(uuid, hash);
        return Convert.ToHexStringLower(hash[..8]);
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

    /// <summary>First document of each kind-1 section present in the capture, as offsets into <paramref name="sections"/>.</summary>
    internal static void FirstSequenceDocuments(ReadOnlySpan<byte> sections, List<(int IdStart, int IdLength, int DocStart, int DocLength)> result)
    {
        var it = new MsgSections(sections);
        while (it.MoveNext() && it.Kind == 1)
        {
            var p = it.Offset;
            var idStart = p + 5;
            var nul = sections[idStart..].IndexOf((byte)0);
            if (nul < 0) break;
            var docStart = idStart + nul + 1;
            // no `docStart + docLen`: docLen is untrusted (PO-9)
            if (sections.Length - docStart >= 4 && docStart - p - 1 < it.Size)
            {
                var docLen = BinaryPrimitives.ReadInt32LittleEndian(sections[docStart..]);
                if (docLen >= 5 && docLen <= sections.Length - docStart)
                    result.Add((idStart, nul, docStart, docLen));
            }
            if (it.Truncated) break;
        }
    }

    /// <summary>
    /// The <c>comment</c> as exported (02 §4.2): up to 256 chars, under the value mode like any other string
    /// (cut at <c>truncateStringsAt</c> when <c>truncated</c>, absent when <c>shape</c>). Decodes only what it keeps.
    /// </summary>
    private string? ExportComment(ReadOnlySpan<byte> utf8)
    {
        var mode = _redactor.Mode;
        if (mode == ValueMode.Shape) return null;
        var max = mode == ValueMode.Truncated ? Math.Min(256, _redactor.TruncateAt) : 256;
        if (utf8.Length > max * 4) // a char is at most 4 UTF-8 bytes
        {
            var cut = max * 4;
            while (cut > 0 && (utf8[cut] & 0xC0) == 0x80) cut--;
            utf8 = utf8[..cut];
        }
        var s = Encoding.UTF8.GetString(utf8);
        return s.Length <= max ? s : s[..max];
    }

    /// <summary>
    /// compression=observe, reply side (01 §8): runs the reply scanner over the inflated head of an OP_COMPRESSED reply
    /// (the capture holds the first <c>commandBytes</c> of it) and takes <c>moreToCome</c> from the inflated flags.
    /// A reply cut short by the capture leaves <c>facts.Parsed</c> false. Never throws on corrupt input.
    /// </summary>
    public void ScanCompressedReply(FrameCapture f)
    {
        if (_compression != CompressionMode.Observe || f.CapturedLength < 9) return;
        var b = f.Buffer;
        var original = BinaryPrimitives.ReadInt32LittleEndian(b);
        var size = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(4));
        var compressorId = b[8];
        if (compressorId is not (0 or 2) || original is not (OpCodes.Msg or OpCodes.Reply) || size < 0) return;
        var limit = Math.Min(size, MaxObservedInflate);
        f.Scanner.Begin(original, f.Facts);
        try
        {
            using var src = new MemoryStream(b, 9, f.CapturedLength - 9, writable: false);
            using var z = compressorId == 0 ? null : new ZLibStream(src, System.IO.Compression.CompressionMode.Decompress);
            var stream = z ?? (Stream)src;
            for (var total = 0; total < limit;)
            {
                var want = Math.Min(_replyChunk.Length, limit - total);
                var n = stream.ReadAtLeast(_replyChunk.AsSpan(0, want), want, throwOnEndOfStream: false);
                if (n <= 0) break;
                if (total == 0 && original == OpCodes.Msg && n >= 4)
                    f.Flags = (MsgFlags)BinaryPrimitives.ReadUInt32LittleEndian(_replyChunk);
                f.Scanner.Feed(_replyChunk.AsSpan(0, n));
                total += n;
            }
        }
        catch (InvalidDataException)
        {
            // corrupt or cut-off deflate stream: the facts so far stand, Parsed stays false
        }
    }
}
