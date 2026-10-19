using System.Text.Json;

namespace Spyglass.Proxy.Tests.Infrastructure;

/// <summary>Reads wire records the way 02 §4 defines them: an absent field is null (or its default).</summary>
public static class Rec
{
    public static long? Long(JsonElement r, string name) =>
        r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : null;

    public static bool Bool(JsonElement r, string name) =>
        r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    public static string? Str(JsonElement r, string name) =>
        r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static bool Has(JsonElement r, string name) =>
        r.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null;

    /// <summary>02 §4.2: derived, respFirst − reqLast.</summary>
    public static long? ServerLatency(JsonElement op) =>
        op.TryGetProperty("t", out var t) && Long(t, "respFirst_us") is { } rf && Long(t, "reqLast_us") is { } rl ? rf - rl : null;
}
