namespace Spyglass.Collector.Ui.Services;

public static class Redirect
{
    /// <summary>
    /// The login page's <c>?return=</c> (UD-4): only a base-relative path inside this app. Anything that could leave the origin
    /// (scheme, <c>//host</c>, backslashes, control characters) or loops back to the login page becomes the home page.
    /// </summary>
    public static string Safe(string? ret)
    {
        if (string.IsNullOrEmpty(ret) || ret.StartsWith("login", StringComparison.OrdinalIgnoreCase)) return "";
        // URL parsing strips C0/space at both ends, so " //evil" would be "//evil": no whitespace at either end
        if (ret[0] is '/' or '\\' || char.IsWhiteSpace(ret[0]) || char.IsWhiteSpace(ret[^1])) return "";
        foreach (var ch in ret) if (ch < 0x20 || (ch >= 0x7f && ch <= 0x9f) || ch == '\\') return "";
        if (Uri.TryCreate(ret, UriKind.Absolute, out _)) return "";
        var end = ret.IndexOfAny(['/', '?', '#']);
        return (end < 0 ? ret : ret[..end]).Contains(':') ? "" : ret; // "https:…" or "javascript:…" in the first segment
    }
}
