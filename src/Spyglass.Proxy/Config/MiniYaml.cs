using System.Text;

namespace Spyglass.Proxy.Config;

/// <summary>
/// The YAML subset the config file needs (01 §2): nested block mappings by indentation,
/// scalars (plain, single- or double-quoted), flow sequences of scalars (<c>[a, b]</c>),
/// <c>#</c> comments, <c>null</c>/<c>~</c>. Anything else is rejected with a line number
/// rather than guessed at. The result is flattened to dotted keys: <c>upstream.tls.caFile</c>.
/// Flow sequences are returned as a comma-joined string; the binder splits them.
/// </summary>
internal static class MiniYaml
{
    public static Dictionary<string, string?> Parse(string text)
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        // (indent, key) of the open mappings, innermost last
        var stack = new List<(int Indent, string Key)>();
        var lines = text.Replace("\r\n", "\n").Split('\n');

        for (var i = 0; i < lines.Length; i++)
        {
            var lineNo = i + 1;
            var raw = StripComment(lines[i], lineNo).TrimEnd();
            if (raw.Trim().Length == 0 || raw.Trim() == "---")
                continue;
            if (raw.Contains('\t'))
                throw new ConfigException($"line {lineNo}: tabs are not allowed for indentation");

            var indent = raw.Length - raw.TrimStart(' ').Length;
            var content = raw.Trim();
            if (content.StartsWith('-'))
                throw new ConfigException($"line {lineNo}: block sequences are not supported; use [a, b]");

            var colon = FindKeyColon(content);
            if (colon < 0)
                throw new ConfigException($"line {lineNo}: expected 'key: value'");

            var key = Unquote(content[..colon].Trim(), lineNo);
            var rest = content[(colon + 1)..].Trim();

            while (stack.Count > 0 && stack[^1].Indent >= indent)
                stack.RemoveAt(stack.Count - 1);

            var path = stack.Count == 0 ? key : string.Join('.', stack.Select(s => s.Key)) + "." + key;

            if (rest.Length == 0)
            {
                // Either a nested mapping or an empty value; decided by the next significant line.
                var next = NextSignificantIndent(lines, i + 1);
                if (next > indent)
                    stack.Add((indent, key));
                else
                    Set(result, path, null, lineNo);
                continue;
            }

            Set(result, path, ParseScalarOrFlow(rest, lineNo), lineNo);
        }

        return result;
    }

    private static void Set(Dictionary<string, string?> d, string path, string? value, int lineNo)
    {
        if (!d.TryAdd(path, value))
            throw new ConfigException($"line {lineNo}: duplicate key '{path}'");
    }

    private static int NextSignificantIndent(string[] lines, int from)
    {
        for (var j = from; j < lines.Length; j++)
        {
            var s = StripComment(lines[j], j + 1).TrimEnd();
            if (s.Trim().Length == 0) continue;
            return s.Length - s.TrimStart(' ').Length;
        }
        return -1;
    }

    private static string? ParseScalarOrFlow(string rest, int lineNo)
    {
        if (rest.StartsWith('['))
        {
            if (!rest.EndsWith(']'))
                throw new ConfigException($"line {lineNo}: unterminated flow sequence");
            var inner = rest[1..^1].Trim();
            if (inner.Length == 0) return "";
            return string.Join(',', SplitFlow(inner, lineNo).Select(x => Unquote(x.Trim(), lineNo)));
        }
        if (rest.StartsWith('{'))
            throw new ConfigException($"line {lineNo}: flow mappings are not supported");
        if (rest is "null" or "~" or "Null" or "NULL")
            return null;
        return Unquote(rest, lineNo);
    }

    private static IEnumerable<string> SplitFlow(string s, int lineNo)
    {
        var sb = new StringBuilder();
        char quote = '\0';
        foreach (var c in s)
        {
            if (quote != '\0')
            {
                sb.Append(c);
                if (c == quote) quote = '\0';
            }
            else if (c is '"' or '\'')
            {
                quote = c;
                sb.Append(c);
            }
            else if (c == ',')
            {
                yield return sb.ToString();
                sb.Clear();
            }
            else if (c is '[' or '{')
                throw new ConfigException($"line {lineNo}: nested flow collections are not supported");
            else
                sb.Append(c);
        }
        if (quote != '\0')
            throw new ConfigException($"line {lineNo}: unterminated quote");
        yield return sb.ToString();
    }

    private static string Unquote(string s, int lineNo)
    {
        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"')
        {
            var sb = new StringBuilder();
            for (var i = 1; i < s.Length - 1; i++)
            {
                if (s[i] == '\\' && i + 1 < s.Length - 1)
                {
                    i++;
                    sb.Append(s[i] switch
                    {
                        'n' => '\n',
                        't' => '\t',
                        '"' => '"',
                        '\\' => '\\',
                        _ => throw new ConfigException($"line {lineNo}: unsupported escape '\\{s[i]}'")
                    });
                }
                else sb.Append(s[i]);
            }
            return sb.ToString();
        }
        if (s.Length >= 2 && s[0] == '\'' && s[^1] == '\'')
            return s[1..^1].Replace("''", "'");
        if (s.Length > 0 && (s[0] == '"' || s[0] == '\''))
            throw new ConfigException($"line {lineNo}: unterminated quote");
        return s;
    }

    private static int FindKeyColon(string s)
    {
        char quote = '\0';
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (quote != '\0') { if (c == quote) quote = '\0'; continue; }
            if (c is '"' or '\'' && StartsToken(s, i)) { quote = c; continue; }
            if (c == ':' && (i + 1 == s.Length || s[i + 1] == ' ')) return i;
        }
        return -1;
    }

    // A quote only opens a quoted scalar at the start of a token, so "it's" stays plain.
    private static bool StartsToken(string s, int i) => i == 0 || s[i - 1] is ' ' or ':' or '[' or ',';

    private static string StripComment(string line, int lineNo)
    {
        char quote = '\0';
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quote != '\0') { if (c == quote) quote = '\0'; continue; }
            if (c is '"' or '\'' && StartsToken(line, i)) { quote = c; continue; }
            if (c == '#' && (i == 0 || line[i - 1] == ' ')) return line[..i];
        }
        return line;
    }
}

public sealed class ConfigException(string message) : Exception(message);
