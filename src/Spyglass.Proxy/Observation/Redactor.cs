using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;
using Spyglass.Proxy.Config;
using Spyglass.Proxy.Wire;

namespace Spyglass.Proxy.Observation;

/// <summary>
/// Builds the exported command body (01 §9). Runs on the observer, before anything is queued for export (I7).
/// The output is BSON; truncated captures yield the complete elements only.
/// </summary>
internal sealed class Redactor
{
    /// <summary>01 §9.1: bodies replaced entirely.</summary>
    public static readonly HashSet<string> RedactedCommands =
    [
        "authenticate", "saslStart", "saslContinue", "getnonce", "createUser", "updateUser",
        "copydbgetnonce", "copydbsaslstart", "copydb",
    ];

    private const int MaxArray = 20, MaxDepth = 64;

    private readonly ValueMode _mode;
    private readonly int _truncateAt;
    private readonly Regex _fieldPattern;
    /// <summary>Field name → pattern verdict. The pattern is fixed for the process, so a cached verdict is exact.</summary>
    private readonly Utf8Table<bool> _verdicts = new(4096);
    private readonly byte[] _truncated;
    private readonly byte[] _errmsg = new byte[2048]; // 256 captured bytes + a "$redacted" (11 bytes) per scrubbed value
    private LsidHasher _lsids = new(capacity: 64);

    public Redactor(CaptureConfig config)
    {
        _mode = config.Values;
        _truncateAt = config.TruncateStringsAt;
        _fieldPattern = new Regex(config.RedactFieldPattern, RegexOptions.CultureInvariant);
        _truncated = new byte[_truncateAt + 32];
    }

    public ValueMode Mode => _mode;
    public int TruncateAt => _truncateAt;

    /// <summary>Convenience for tests and one-offs: a fresh buffer per call.</summary>
    public (byte[] Bson, bool Truncated, bool WholeCommandRedacted) Redact(string command, ReadOnlySpan<byte> doc, bool isHello, bool captureTruncated)
    {
        var w = new BsonWriter(Math.Min(doc.Length + 64, 64 * 1024));
        var (truncated, whole) = Redact(w, command, doc, default, [], _lsids, isHello, captureTruncated);
        return (w.ToArray(), truncated, whole);
    }

    /// <summary>Writes the exported body into <paramref name="w"/> (reset by the caller).</summary>
    /// <param name="sections">kind-1 sections of the capture; <paramref name="firstSeqDocs"/> holds offsets into it.</param>
    public (bool Truncated, bool WholeCommandRedacted) Redact(
        BsonWriter w, string command, ReadOnlySpan<byte> doc,
        ReadOnlySpan<byte> sections, List<(int IdStart, int IdLength, int DocStart, int DocLength)> firstSeqDocs,
        LsidHasher lsids, bool isHello, bool captureTruncated)
    {
        _lsids = lsids;
        if (RedactedCommands.Contains(command))
        {
            w.StartDocument();
            w.WriteString("$redacted", command);
            w.EndDocument();
            return (false, true);
        }

        w.StartDocument();
        var truncated = WriteElements(w, doc, depth: 0, topLevel: true, isHello) || captureTruncated;
        if (firstSeqDocs.Count > 0)
        {
            Span<byte> scratch = stackalloc byte[12];
            foreach (var (idStart, idLength, docStart, docLength) in firstSeqDocs)
            {
                w.StartArray(sections.Slice(idStart, idLength));
                w.StartDocument(BsonWriter.IndexName(0, scratch));
                truncated |= WriteElements(w, sections.Slice(docStart, docLength), 1, topLevel: false, isHello: false);
                w.EndDocument();
                w.EndDocument();
            }
        }
        w.EndDocument();
        return (truncated, false);
    }

    /// <returns>true if the source document was truncated or malformed.</returns>
    /// <param name="structural">Inside a <c>sort</c> / <c>projection</c> / <c>hint</c> spec: numeric and boolean values are directions and flags, not data, whatever the field is called.</param>
    /// <param name="inheritRedact">Inside an expression that compares a secret field path (see <see cref="HasSecretPath"/>): every literal below is redacted.</param>
    private bool WriteElements(BsonWriter w, ReadOnlySpan<byte> doc, int depth, bool topLevel, bool isHello, bool isArray = false,
        bool structural = false, bool inheritRedact = false)
    {
        var elements = Bson.Elements(doc);
        var index = 0;
        Span<byte> scratch = stackalloc byte[12];
        var redactLiterals = inheritRedact || (isArray && HasSecretPath(doc, 0));
        while (elements.MoveNext())
        {
            var e = elements.Current;

            if (isArray && _mode != ValueMode.Full && index == MaxArray)
            {
                // count the rest without writing them
                var more = 1;
                while (elements.MoveNext()) more++;
                w.StartDocument(BsonWriter.IndexName(index, scratch));
                w.WriteInt32("$more", more);
                w.EndDocument();
                return elements.Truncated || elements.Malformed;
            }
            index++;

            if ((topLevel && isHello && e.NameIs("speculativeAuthenticate"u8)) ||
                (NameMatches(e.Name) && !(structural && e.Type is BsonType.Int32 or BsonType.Int64 or BsonType.Double or BsonType.Decimal128 or BsonType.Boolean)) ||
                (e.Type is BsonType.String or BsonType.JavaScript && e.NameIs("$where"u8) && TextMentionsSecret(e.StringBytes)))
            {
                w.WriteStringUtf8(e.Name, "$redacted"u8);
                continue;
            }

            if (topLevel && e.NameIs("lsid"u8) && e.Type == BsonType.Document)
            {
                w.StartDocument(e.Name);
                w.WriteString("id"u8, _lsids.HashDocument(e.Value) ?? "$redacted");
                w.EndDocument();
                continue;
            }

            if (topLevel && ((e.NameIs("$db"u8) && e.Type == BsonType.String) ||
                             (e.Index0() && e.Type is BsonType.String or BsonType.Int32 or BsonType.Int64 or BsonType.Double or BsonType.Boolean)))
            {
                // command name/target and database are identifiers, never truncated or shaped. A document or array as the
                // first value (explain, applyOps, ...) is a body like any other: it goes through the value policy.
                w.WriteElement(e);
                continue;
            }

            // {$eq: ["$password", "hunter2"]}: the literals next to a secret field path are secrets too
            if (redactLiterals && e.Type is not (BsonType.Document or BsonType.Array) &&
                !(e.Type == BsonType.String && e.StringBytes.Length > 0 && e.StringBytes[0] == (byte)'$'))
            {
                w.WriteStringUtf8(e.Name, "$redacted"u8);
                continue;
            }

            WriteValue(w, e, depth, structural || IsStructuralName(e.Name), redactLiterals);
        }
        return elements.Truncated || elements.Malformed;
    }

    private static bool IsStructuralName(ReadOnlySpan<byte> name) =>
        name.SequenceEqual("sort"u8) || name.SequenceEqual("projection"u8) || name.SequenceEqual("hint"u8) || name.SequenceEqual("$sort"u8);

    private void WriteValue(BsonWriter w, in BsonElement e, int depth, bool structural, bool redactLiterals)
    {
        switch (e.Type)
        {
            case BsonType.Document:
            case BsonType.Array:
                if (depth >= MaxDepth)
                {
                    w.WriteString(e.Name, "$depth");
                    return;
                }
                if (e.Type == BsonType.Document) w.StartDocument(e.Name);
                else w.StartArray(e.Name);
                WriteElements(w, e.Value, depth + 1, topLevel: false, isHello: false, isArray: e.Type == BsonType.Array,
                    structural: structural, inheritRedact: redactLiterals);
                w.EndDocument();
                return;
        }

        if (_mode == ValueMode.Shape)
        {
            w.WriteStringUtf8(e.Name, ShapeTagUtf8(e));
            return;
        }

        if (e.Type == BsonType.JavaScriptWithScope)
        {
            WriteCodeWithScope(w, e, depth);
            return;
        }

        if (_mode == ValueMode.Truncated)
        {
            if (e.Type is BsonType.String or BsonType.JavaScript or BsonType.Symbol && e.StringBytes.Length > _truncateAt)
            {
                var cut = Cut(e.StringBytes);
                if (e.Type == BsonType.String) w.WriteStringUtf8(e.Name, cut);
                else WriteStringLike(w, e.Name, e.Type, cut);
                return;
            }
            if (e.Type == BsonType.Binary)
            {
                // subtype + length only; the collector reads {$binData:{subtype,length}} as a binData leaf
                w.StartDocument(e.Name);
                w.StartDocument("$binData");
                w.WriteInt32("subtype", e.Value[4]);
                w.WriteInt32("length", BinaryPrimitives.ReadInt32LittleEndian(e.Value));
                w.EndDocument();
                w.EndDocument();
                return;
            }
        }

        w.WriteElement(e);
    }

    /// <summary><paramref name="s"/> cut at <c>truncateStringsAt</c> and suffixed <c>…(+N)</c> (a view of a scratch buffer valid until the next call).</summary>
    private ReadOnlySpan<byte> Cut(ReadOnlySpan<byte> s)
    {
        if (s.Length <= _truncateAt) return s;
        var cut = Utf8Boundary(s, _truncateAt);
        s[..cut].CopyTo(_truncated);
        var n = cut;
        "…(+"u8.CopyTo(_truncated.AsSpan(n));
        n += "…(+"u8.Length;
        (s.Length - cut).TryFormat(_truncated.AsSpan(n), out var digits, provider: System.Globalization.CultureInfo.InvariantCulture);
        n += digits;
        _truncated[n++] = (byte)')';
        return _truncated.AsSpan(0, n);
    }

    /// <summary>A string-valued element of type <paramref name="type"/> (JavaScript, Symbol) with the given UTF-8 text.</summary>
    private static void WriteStringLike(BsonWriter w, ReadOnlySpan<byte> name, byte type, ReadOnlySpan<byte> utf8)
    {
        w.WriteElement(name, type, default);
        w.WriteInt32Raw(utf8.Length + 1);
        w.WriteRaw(utf8);
        w.WriteByteRaw(0);
    }

    /// <summary>JavaScriptWithScope: the code is cut like a string, the scope document is walked like any other.</summary>
    private void WriteCodeWithScope(BsonWriter w, in BsonElement e, int depth)
    {
        var v = e.Value;
        var codeLen = v.Length >= 8 ? BinaryPrimitives.ReadInt32LittleEndian(v[4..]) : -1;
        if (depth >= MaxDepth || codeLen < 1 || 8L + codeLen > v.Length)
        {
            w.WriteString(e.Name, depth >= MaxDepth ? "$depth" : "$redacted");
            return;
        }
        var code = v.Slice(8, codeLen - 1);
        if (_mode == ValueMode.Truncated) code = Cut(code);
        w.WriteElement(e.Name, BsonType.JavaScriptWithScope, default);
        var start = w.Length;
        w.WriteInt32Raw(0);
        w.WriteInt32Raw(code.Length + 1);
        w.WriteRaw(code);
        w.WriteByteRaw(0);
        w.StartDocument();
        WriteElements(w, v[(8 + codeLen)..], depth + 1, topLevel: false, isHello: false);
        w.EndDocument();
        w.PatchInt32(start, w.Length - start);
    }

    /// <summary>
    /// True when an expression array holds a field path (<c>"$password"</c>) whose name the pattern redacts, directly or inside
    /// an operator document of it (<c>{$toLower: "$password"}</c>). The literals of the whole expression are then redacted
    /// (<c>$eq</c>, <c>$in</c>, <c>$cond</c>, ...). Not covered: <c>$function</c> bodies and JavaScript that builds names.
    /// </summary>
    private bool HasSecretPath(ReadOnlySpan<byte> doc, int level)
    {
        foreach (var e in Bson.Elements(doc))
        {
            if (e.Type == BsonType.Document && level < 4 && HasSecretPath(e.Value, level + 1))
                return true;
            if (e.Type != BsonType.String) continue;
            var s = e.StringBytes;
            if (s.Length > 1 && s[0] == (byte)'$' && s[1] != (byte)'$' && NameMatches(s[1..]))
                return true;
        }
        return false;
    }

    /// <summary>Server-side JavaScript (<c>$where</c>) that names a secret field: <c>this.password == 'x'</c> carries the secret in the code.</summary>
    private bool TextMentionsSecret(ReadOnlySpan<byte> text)
    {
        if (text.Length > 2048) return true; // too long to scan cheaply: redact
        Span<char> chars = stackalloc char[text.Length];
        var n = Encoding.UTF8.GetChars(text, chars);
        return _fieldPattern.IsMatch(chars[..n]);
    }

    /// <summary>
    /// A server error message as exported (01 §10); the caller drops it in <c>shape</c> mode (<see cref="Mode"/>). Otherwise
    /// every <c>name: value</c> whose name the redaction pattern matches (the <c>dup key: { token: "…" }</c> of an E11000)
    /// has its value replaced by <c>"$redacted"</c>, and in <c>truncated</c> mode quoted values are cut like any string.
    /// The result lives in a per-redactor buffer, valid until the next call.
    /// </summary>
    public ReadOnlySpan<byte> ScrubErrmsg(ReadOnlySpan<byte> msg)
    {
        var o = 0;
        var copied = 0;
        var i = 0;
        var buf = _errmsg;
        bool? indexSecret = null;
        while (i < msg.Length)
        {
            var rel = msg[i..].IndexOf((byte)':');
            if (rel < 0) break;
            var p = i + rel;
            i = p + 1;
            var ts = p;
            var te = p;
            while (ts > copied && IsNameByte(msg[ts - 1])) ts--;
            if (ts == p && p > copied && msg[p - 1] is (byte)'"' or (byte)'\'')
            {
                // a quoted key: {"password": "x"}
                var q = msg[p - 1];
                var k = p - 2;
                while (k >= copied && msg[k] != q) k--;
                if (k >= copied) { ts = k + 1; te = p - 1; }
            }
            var vs = p + 1;
            while (vs < msg.Length && msg[vs] == (byte)' ') vs++;
            if (vs >= msg.Length) break;

            bool secret;
            if (ts < te) secret = NameMatches(msg[ts..te]);
            else
            {
                // `dup key: { : "v" }` (MongoDB < 4.2 leaves the key names out): the index name says which fields they are
                var b = p;
                while (b > copied && msg[b - 1] == (byte)' ') b--;
                secret = b > 0 && msg[b - 1] is (byte)'{' or (byte)',' && (indexSecret ??= IndexNameIsSecret(msg));
            }

            if (secret)
            {
                var end = ValueEnd(msg, vs, toClauseEnd: true);
                Put(buf, ref o, msg[copied..vs]);
                Put(buf, ref o, "\"$redacted\""u8);
                copied = i = end;
            }
            else if (_mode == ValueMode.Truncated && msg[vs] is (byte)'"' or (byte)'\'')
            {
                var end = ValueEnd(msg, vs);
                var closed = end > vs + 1 && msg[end - 1] == msg[vs];
                var content = msg[(vs + 1)..(closed ? end - 1 : end)];
                if (content.Length <= _truncateAt) continue;
                Put(buf, ref o, msg[copied..(vs + 1)]);
                Put(buf, ref o, Cut(content));
                if (closed) Put(buf, ref o, msg.Slice(vs, 1));
                copied = i = end;
            }
        }
        Put(buf, ref o, msg[copied..]);
        return buf.AsSpan(0, o);
    }

    /// <summary>The token after <c>index: </c> (<c>app.users.$apiKey_1</c>, <c>apiKey_1_user_1</c>) matches the redaction pattern.</summary>
    private bool IndexNameIsSecret(ReadOnlySpan<byte> msg)
    {
        var at = msg.IndexOf("index: "u8);
        if (at < 0) return false;
        var token = msg[(at + 7)..];
        var end = token.IndexOfAny((byte)' ', (byte)',');
        if (end >= 0) token = token[..end];
        return token.Length > 0 && NameMatches(token);
    }

    private static void Put(byte[] buf, ref int o, ReadOnlySpan<byte> b)
    {
        var n = Math.Min(b.Length, buf.Length - o);
        b[..n].CopyTo(buf.AsSpan(o));
        o += n;
    }

    private static bool IsNameByte(byte b) => b is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z' or >= (byte)'0' and <= (byte)'9'
        or (byte)'_' or (byte)'.' or (byte)'$' or (byte)'-' or >= 0x80;

    /// <summary>
    /// End (exclusive) of the value starting at <paramref name="vs"/> in a server message: a quoted string, or up to the next
    /// top-level separator. Quotes inside braces and brackets are skipped as strings. With <paramref name="toClauseEnd"/> (a value
    /// that is being redacted) a space does not end an unquoted value: <c>password: my pass phrase</c> goes whole.
    /// </summary>
    private static int ValueEnd(ReadOnlySpan<byte> m, int vs, bool toClauseEnd = false)
    {
        var j = vs;
        if (m[j] is (byte)'"' or (byte)'\'')
        {
            var q = m[j++];
            while (j < m.Length)
            {
                if (m[j] == (byte)'\\') j += 2;
                else if (m[j++] == q) return j;
            }
            return m.Length;
        }
        var depth = 0;
        var closed = false; // a bracketed value ended: a space after it ends the value even when redacting
        for (; j < m.Length; j++)
        {
            var c = m[j];
            if (c is (byte)'(' or (byte)'{' or (byte)'[') depth++;
            else if (c is (byte)')' or (byte)'}' or (byte)']')
            {
                if (depth == 0) break;
                if (--depth == 0) closed = true;
            }
            else if (depth > 0 && c is (byte)'"' or (byte)'\'')
            {
                for (j++; j < m.Length && m[j] != c; j++)
                    if (m[j] == (byte)'\\') j++;
                if (j >= m.Length) return m.Length;
            }
            else if (depth == 0 && (c == (byte)',' || (c == (byte)' ' && (!toClauseEnd || closed)))) break;
        }
        return j;
    }

    /// <summary>03 §4.1 leaf tags.</summary>
    internal static ReadOnlySpan<byte> ShapeTagUtf8(in BsonElement e) => e.Type switch
    {
        BsonType.Int32 or BsonType.Int64 or BsonType.Double or BsonType.Decimal128 => "?number"u8,
        BsonType.String => e.StringBytes.SequenceEqual("$redacted"u8) ? "?redacted"u8 : "?string"u8,
        BsonType.ObjectId => "?objectId"u8,
        BsonType.DateTime => "?date"u8,
        BsonType.Boolean => "?bool"u8,
        BsonType.Null or BsonType.Undefined => "?null"u8,
        BsonType.Binary => e.Value.Length >= 5 && e.Value[4] == 4 ? "?uuid"u8 : "?binData"u8,
        BsonType.Regex => "?regex"u8,
        BsonType.Timestamp => "?timestamp"u8,
        _ => "?other"u8,
    };

    /// <summary>
    /// The field pattern is tested against the whole name and against every segment of a dotted path, so
    /// <c>{"profile.password": …}</c> in a filter or <c>$set</c> is redacted like <c>{profile: {password: …}}</c>.
    /// </summary>
    private bool NameMatches(ReadOnlySpan<byte> name)
    {
        if (_verdicts.TryGetValue(name, out var verdict))
            return verdict;
        verdict = Evaluate(name);
        _verdicts.TryAdd(name, verdict);
        return verdict;
    }

    private bool Evaluate(ReadOnlySpan<byte> name)
    {
        if (name.Length > 512) return true; // absurd key: redact rather than run the regex on it
        Span<char> chars = stackalloc char[name.Length];
        var n = Encoding.UTF8.GetChars(name, chars);
        var s = chars[..n];
        if (_fieldPattern.IsMatch(s)) return true;
        if (s.IndexOf('.') < 0) return false;
        foreach (var range in s.Split('.'))
            if (_fieldPattern.IsMatch(s[range]))
                return true;
        return false;
    }

    private static int Utf8Boundary(ReadOnlySpan<byte> s, int max)
    {
        var cut = Math.Min(max, s.Length);
        while (cut > 0 && cut < s.Length && (s[cut] & 0xC0) == 0x80)
            cut--;
        return cut;
    }
}

internal static class BsonElementExtensions
{
    /// <summary>True for the first element of a document (offset 4): the command name.</summary>
    public static bool Index0(this in BsonElement e) => e.Offset == 4;
}
