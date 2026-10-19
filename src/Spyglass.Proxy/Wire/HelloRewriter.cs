using System.Buffers.Binary;
using Spyglass.Proxy.Config;

namespace Spyglass.Proxy.Wire;

/// <summary>Maps upstream member addresses to the advertised addresses of their listeners (R1, 01 §5.3).</summary>
public interface IAddressMap
{
    /// <summary>Advertised <c>host:port</c> for an upstream member; allocates a listener for an unknown member.
    /// Returns null if no listener can be allocated (the original string is then left unchanged).</summary>
    string? Advertise(HostPort upstream);
}

public sealed class HelloReplyFacts
{
    public long? ConnectionId;
    public int? MaxMessageSizeBytes;
    public int? MaxWireVersion;
    public string? SetName;
    public bool IsMongos;
    public string[]? Compression;
    public readonly List<HostPort> Members = [];
    public string? Me;
    public bool HasTopologyVersion;
    /// <summary>The reply's <c>me</c> did not map to the listener the reply was forwarded on (01 §5.3).</summary>
    public string? MeMismatch;
}

public sealed class HelloRewriteContext
{
    public required bool RewriteTopology { get; init; }
    public required IAddressMap Map { get; init; }
    /// <summary>Advertised address of the listener the reply is forwarded on.</summary>
    public required string Self { get; init; }
    /// <summary>Encoded <c>$spyglass</c> document (02 §2.2), length prefix included.</summary>
    public required byte[] SpyglassDocument { get; init; }
}

public enum RewriteFailure
{
    None,
    Malformed,
    UnsupportedOpCode,
    Compressed,
}

/// <summary>
/// R1–R3 (01 §3.3, I2). Works on a fully buffered frame and returns a new frame. Never throws on bad
/// input: a failure returns <c>null</c> plus a reason and the caller forwards the original bytes.
/// </summary>
public static class HelloRewriter
{
    private static readonly byte[] SpyglassName = "$spyglass"u8.ToArray();

    // ---- R3: client hello ------------------------------------------------------------------

    /// <summary>Removes <c>compression</c> from a hello request. Returns null if there is nothing to remove.</summary>
    public static byte[]? StripCompression(ReadOnlySpan<byte> frame, out RewriteFailure failure)
    {
        failure = RewriteFailure.None;
        return RewriteBody(frame, (BsonWriter w, ReadOnlySpan<byte> doc) =>
        {
            var changed = false;
            w.StartDocument();
            foreach (var e in Bson.Elements(doc))
            {
                if (e.NameIs("compression"u8))
                {
                    changed = true;
                    continue;
                }
                w.WriteElement(e);
            }
            w.EndDocument();
            return changed;
        }, out failure);
    }

    // ---- R1 + R2: hello reply --------------------------------------------------------------

    public static byte[]? RewriteReply(ReadOnlySpan<byte> frame, HelloRewriteContext ctx, HelloReplyFacts facts, out RewriteFailure failure)
    {
        return RewriteBody(frame, (BsonWriter w, ReadOnlySpan<byte> doc) =>
        {
            Span<byte> scratch = stackalloc byte[12];
            w.StartDocument();
            foreach (var e in Bson.Elements(doc))
            {
                CollectFact(e, facts);

                if (e.NameIs(SpyglassName))
                    continue; // never forward a stale/foreign $spyglass (e.g. a chained proxy); ours is appended below

                if (ctx.RewriteTopology && e.Type == BsonType.Array &&
                    (e.NameIs("hosts"u8) || e.NameIs("passives"u8) || e.NameIs("arbiters"u8)))
                {
                    w.StartArray(e.Name);
                    var i = 0;
                    foreach (var item in Bson.Elements(e.Value))
                    {
                        var idx = BsonWriter.IndexName(i++, scratch);
                        if (item.Type == BsonType.String && item.AsString() is { } s && HostPort.TryParse(s, out var hp))
                        {
                            facts.Members.Add(hp);
                            w.WriteString(idx, ctx.Map.Advertise(hp) ?? s);
                        }
                        else
                            w.WriteElement(idx, item.Type, item.Value);
                    }
                    w.EndDocument();
                    continue;
                }

                if (ctx.RewriteTopology && e.Type == BsonType.String && e.NameIs("primary"u8)
                    && e.AsString() is { } primary && HostPort.TryParse(primary, out var php))
                {
                    w.WriteString(e.Name, ctx.Map.Advertise(php) ?? primary);
                    continue;
                }

                if (ctx.RewriteTopology && e.Type == BsonType.String && e.NameIs("me"u8) && e.AsString() is { } me)
                {
                    facts.Me = me;
                    if (HostPort.TryParse(me, out var mhp) && ctx.Map.Advertise(mhp) is { } mapped && mapped != ctx.Self)
                        facts.MeMismatch = $"member reports me={me} (→ {mapped}) on listener {ctx.Self}";
                    // Drivers drop a member whose `me` differs from the address they dialled, so always use this listener's.
                    w.WriteString(e.Name, ctx.Self);
                    continue;
                }

                w.WriteElement(e);
            }
            w.WriteElement(SpyglassName, BsonType.Document, ctx.SpyglassDocument);
            w.EndDocument();
            return true;
        }, out failure);
    }

    /// <summary>Extracts hello facts without rewriting (used when the rewrite is skipped).</summary>
    public static void ExtractFacts(ReadOnlySpan<byte> frame, HelloReplyFacts facts)
    {
        var doc = FirstDocument(frame);
        if (doc.IsEmpty) return;
        foreach (var e in Bson.Elements(doc))
        {
            CollectFact(e, facts);
            if (e.Type == BsonType.Array && (e.NameIs("hosts"u8) || e.NameIs("passives"u8) || e.NameIs("arbiters"u8)))
                foreach (var item in Bson.Elements(e.Value))
                    if (item.AsString() is { } s && HostPort.TryParse(s, out var hp))
                        facts.Members.Add(hp);
        }
    }

    private static void CollectFact(in BsonElement e, HelloReplyFacts facts)
    {
        if (e.NameIs("connectionId"u8)) facts.ConnectionId = e.AsInt64();
        else if (e.NameIs("maxMessageSizeBytes"u8)) facts.MaxMessageSizeBytes = (int?)e.AsInt64();
        else if (e.NameIs("maxWireVersion"u8)) facts.MaxWireVersion = (int?)e.AsInt64();
        else if (e.NameIs("setName"u8)) facts.SetName = e.AsString();
        else if (e.NameIs("msg"u8)) facts.IsMongos = e.StringBytes.SequenceEqual("isdbgrid"u8);
        else if (e.NameIs("topologyVersion"u8)) facts.HasTopologyVersion = true;
        else if (e.NameIs("me"u8)) facts.Me ??= e.AsString();
        else if (e.NameIs("compression"u8) && e.Type == BsonType.Array)
        {
            var list = new List<string>();
            foreach (var c in Bson.Elements(e.Value))
                if (c.AsString() is { } s) list.Add(s);
            facts.Compression = list.ToArray();
        }
    }

    // ---- framing ---------------------------------------------------------------------------

    private delegate bool DocRewrite(BsonWriter w, ReadOnlySpan<byte> doc);

    /// <summary>The body document of an OP_MSG (first kind-0 section), OP_REPLY (first document) or OP_QUERY (query).</summary>
    public static ReadOnlySpan<byte> FirstDocument(ReadOnlySpan<byte> frame) =>
        TryFindFirstDocument(frame, out var offset, out var size) ? frame.Slice(offset, size) : default;

    public static bool TryFindFirstDocument(ReadOnlySpan<byte> frame, out int offset, out int size)
    {
        offset = size = 0;
        if (frame.Length < FrameHeader.Size + 4) return false;
        var h = FrameHeader.Read(frame);
        if (h.MessageLength != frame.Length) return false;
        int p;
        var end = frame.Length;
        switch (h.OpCode)
        {
            case OpCodes.Msg:
            {
                var flags = (MsgFlags)BinaryPrimitives.ReadUInt32LittleEndian(frame[16..]);
                end -= (flags & MsgFlags.ChecksumPresent) != 0 ? 4 : 0;
                p = 20;
                while (p + 5 <= end)
                {
                    var kind = frame[p];
                    var len = BinaryPrimitives.ReadInt32LittleEndian(frame[(p + 1)..]);
                    if (len < 5 || p + 1 + len > end) return false;
                    if (kind == 0)
                    {
                        offset = p + 1;
                        size = len;
                        return true;
                    }
                    if (kind != 1) return false;
                    p += 1 + len;
                }
                return false;
            }
            case OpCodes.Reply:
                p = 16 + 20;
                break;
            case OpCodes.Query:
            {
                var nul = frame[20..].IndexOf((byte)0);
                if (nul < 0) return false;
                p = 20 + nul + 1 + 8;
                break;
            }
            default:
                return false;
        }
        if (end < p + 5) return false;
        var docLen = BinaryPrimitives.ReadInt32LittleEndian(frame[p..]);
        if (docLen < 5 || p + docLen > end) return false;
        offset = p;
        size = docLen;
        return true;
    }

    private static byte[]? RewriteBody(ReadOnlySpan<byte> frame, DocRewrite rewrite, out RewriteFailure failure)
    {
        failure = RewriteFailure.None;
        if (frame.Length < FrameHeader.Size + 5)
        {
            failure = RewriteFailure.Malformed;
            return null;
        }
        var h = FrameHeader.Read(frame);
        if (h.MessageLength != frame.Length)
        {
            failure = RewriteFailure.Malformed;
            return null;
        }
        if (h.OpCode == OpCodes.Compressed)
        {
            failure = RewriteFailure.Compressed;
            return null;
        }

        if (!TryFindFirstDocument(frame, out var docOffset, out var docSize))
        {
            failure = RewriteFailure.Malformed;
            return null;
        }
        var doc = frame.Slice(docOffset, docSize);

        // Reject documents we can't walk completely: a partial rewrite would corrupt the reply.
        var check = Bson.Elements(doc);
        while (check.MoveNext()) { }
        if (!check.Complete)
        {
            failure = RewriteFailure.Malformed;
            return null;
        }

        var w = new BsonWriter(frame.Length + 256);
        w.WriteRaw(frame[..docOffset]);

        // unwrap OP_QUERY {$query: {...}, $readPreference: ...}
        var wrapped = false;
        if (h.OpCode == OpCodes.Query)
        {
            var first = Bson.Elements(doc);
            if (first.MoveNext() && (first.Current.NameIs("$query"u8) || first.Current.NameIs("query"u8)) && first.Current.Type == BsonType.Document)
                wrapped = true;
        }

        bool changed;
        if (wrapped)
        {
            changed = false;
            w.StartDocument();
            foreach (var e in Bson.Elements(doc))
            {
                if ((e.NameIs("$query"u8) || e.NameIs("query"u8)) && e.Type == BsonType.Document)
                {
                    var inner = new BsonWriter(e.Value.Length + 256);
                    changed = rewrite(inner, e.Value);
                    w.WriteElement(e.Name, BsonType.Document, inner.WrittenSpan);
                }
                else w.WriteElement(e);
            }
            w.EndDocument();
        }
        else
            changed = rewrite(w, doc);

        if (!changed)
            return null;

        var docEnd = docOffset + doc.Length;
        if (h.OpCode == OpCodes.Msg)
        {
            var flags = (MsgFlags)BinaryPrimitives.ReadUInt32LittleEndian(frame[16..]);
            var end = frame.Length - ((flags & MsgFlags.ChecksumPresent) != 0 ? 4 : 0);
            w.WriteRaw(frame[docEnd..end]); // any further sections; the checksum is dropped
            w.PatchInt32(16, (int)(flags & ~MsgFlags.ChecksumPresent));
        }
        else
        {
            w.WriteRaw(frame[docEnd..]);
        }

        w.PatchInt32(0, w.Length);
        return w.ToArray();
    }
}
