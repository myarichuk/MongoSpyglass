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
        if (ret[0] is '/' or '\\') return "";
        foreach (var ch in ret) if (ch < 0x20 || ch == 0x7f || ch == '\\') return "";
        var end = ret.IndexOfAny(['/', '?', '#']);
        return (end < 0 ? ret : ret[..end]).Contains(':') ? "" : ret; // "https:…" or "javascript:…" in the first segment
    }
}
