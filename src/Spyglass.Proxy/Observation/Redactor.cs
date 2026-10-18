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

    public Redactor(CaptureConfig config)
    {
        _mode = config.Values;
        _truncateAt = config.TruncateStringsAt;
        _fieldPattern = new Regex(config.RedactFieldPattern, RegexOptions.CultureInvariant);
    }

    public (byte[] Bson, bool Truncated, bool WholeCommandRedacted) Redact(
        string command, ReadOnlySpan<byte> doc, List<(string Identifier, byte[] FirstDoc)>? firstSeqDocs, bool isHello, bool captureTruncated)
    {
        var w = new BsonWriter(Math.Min(doc.Length + 64, 64 * 1024));
        if (RedactedCommands.Contains(command))
        {
            w.StartDocument();
            w.WriteString("$redacted", command);
            w.EndDocument();
            return (w.ToArray(), false, true);
        }

        w.StartDocument();
        var truncated = WriteElements(w, doc, depth: 0, topLevel: true, isHello) || captureTruncated;
        if (firstSeqDocs != null)
        {
            Span<byte> scratch = stackalloc byte[12];
            foreach (var (id, first) in firstSeqDocs)
            {
                w.StartArray(id);
                w.StartDocument(BsonWriter.IndexName(0, scratch));
                truncated |= WriteElements(w, first, 1, topLevel: false, isHello: false);
                w.EndDocument();
                w.EndDocument();
            }
        }
        w.EndDocument();
        return (w.ToArray(), truncated, false);
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
                w.WriteString(e.Name, "$redacted");
                continue;
            }

            if (topLevel && e.NameIs("lsid"u8) && e.Type == BsonType.Document)
            {
                w.StartDocument(e.Name);
                w.WriteString("id", RequestParser.HashLsid(e.Value) ?? "$redacted");
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
            w.WriteString(e.Name, ShapeTag(e));
            return;
        }

        if (_mode == ValueMode.Truncated)
        {
            if (e.Type == BsonType.String && e.StringBytes.Length > _truncateAt)
            {
                var s = e.StringBytes;
                var cut = Utf8Boundary(s, _truncateAt);
                var sb = new StringBuilder(Encoding.UTF8.GetString(s[..cut]));
                sb.Append("…(+").Append(s.Length - cut).Append(')');
                w.WriteString(e.Name, sb.ToString());
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
    internal static string ShapeTag(in BsonElement e) => e.Type switch
    {
        BsonType.Int32 or BsonType.Int64 or BsonType.Double or BsonType.Decimal128 => "?number",
        BsonType.String => e.StringBytes.SequenceEqual("$redacted"u8) ? "?redacted" : "?string",
        BsonType.ObjectId => "?objectId",
        BsonType.DateTime => "?date",
        BsonType.Boolean => "?bool",
        BsonType.Null or BsonType.Undefined => "?null",
        BsonType.Binary => e.Value.Length >= 5 && e.Value[4] == 4 ? "?uuid" : "?binData",
        BsonType.Regex => "?regex",
        BsonType.Timestamp => "?timestamp",
        _ => "?other",
    };

    /// <summary>
    /// The field pattern is tested against the whole name and against every segment of a dotted path, so
    /// <c>{"profile.password": …}</c> in a filter or <c>$set</c> is redacted like <c>{profile: {password: …}}</c>.
    /// </summary>
    private bool NameMatches(ReadOnlySpan<byte> name)
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
