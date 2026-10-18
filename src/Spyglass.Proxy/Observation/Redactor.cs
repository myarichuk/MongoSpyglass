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
    private LsidHasher _lsids = new(capacity: 64);

    public Redactor(CaptureConfig config)
    {
        _mode = config.Values;
        _truncateAt = config.TruncateStringsAt;
        _fieldPattern = new Regex(config.RedactFieldPattern, RegexOptions.CultureInvariant);
        _truncated = new byte[_truncateAt + 32];
    }

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
    private bool WriteElements(BsonWriter w, ReadOnlySpan<byte> doc, int depth, bool topLevel, bool isHello, bool isArray = false)
    {
        var elements = Bson.Elements(doc);
        var index = 0;
        Span<byte> scratch = stackalloc byte[12];
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

            if ((topLevel && isHello && e.NameIs("speculativeAuthenticate"u8)) || NameMatches(e.Name))
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

            if (topLevel && (e.NameIs("$db"u8) || e.Index0()))
            {
                // command name/target and database are identifiers, never truncated or shaped
                w.WriteElement(e);
                continue;
            }

            WriteValue(w, e, depth);
        }
        return elements.Truncated || elements.Malformed;
    }

    private void WriteValue(BsonWriter w, in BsonElement e, int depth)
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
                WriteElements(w, e.Value, depth + 1, topLevel: false, isHello: false, isArray: e.Type == BsonType.Array);
                w.EndDocument();
                return;
        }

        if (_mode == ValueMode.Shape)
        {
            w.WriteStringUtf8(e.Name, ShapeTagUtf8(e));
            return;
        }

        if (_mode == ValueMode.Truncated)
        {
            if (e.Type == BsonType.String && e.StringBytes.Length > _truncateAt)
            {
                var s = e.StringBytes;
                var cut = Utf8Boundary(s, _truncateAt);
                s[..cut].CopyTo(_truncated);
                var n = cut;
                "…(+"u8.CopyTo(_truncated.AsSpan(n));
                n += "…(+"u8.Length;
                (s.Length - cut).TryFormat(_truncated.AsSpan(n), out var digits, provider: System.Globalization.CultureInfo.InvariantCulture);
                n += digits;
                _truncated[n++] = (byte)')';
                w.WriteStringUtf8(e.Name, _truncated.AsSpan(0, n));
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
