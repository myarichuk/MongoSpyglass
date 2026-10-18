using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.WebAssembly.Http;

namespace Spyglass.Collector.Ui.Services;

// DTOs mirror the collector's /api/v1 JSON (03 §8). Absent fields are null.

public sealed record OpSummary(
    string Id, long Ts, string ProxyId, string Member, string? App, string Command, string? Db, string? Coll, string Status,
    [property: JsonPropertyName("serverLatency_us")] long? ServerLatencyUs, int? NReturned, int ReqBytes, int RespBytes,
    string ShapeHash, int ReplyCount, string? CodeName, string[]? Alerts)
{
    public string Ns => Coll == null ? Db ?? "" : $"{Db}.{Coll}";
}

public sealed record OpPage(OpSummary[] Items, string? Next);

public sealed record Timings(
    [property: JsonPropertyName("reqFirst_us")] long ReqFirst, [property: JsonPropertyName("reqLast_us")] long ReqLast,
    [property: JsonPropertyName("respFirst_us")] long? RespFirst, [property: JsonPropertyName("respLast_us")] long? RespLast);

public sealed record CursorDto(string CursorId, string ProxyId, string Member, string OpenOpId, string ShapeHash, string? App, string? Db, string? Coll,
    long OpenedTs, long LastActivityTs, long Documents, int GetMores, string? ClosedReason, long? ClosedTs);

public sealed record OpDetailDto(
    string Id, long Ts, string ProxyId, string Member, long? ServerConnectionId, int RequestId, long ConnSeq, string OpCode, string Command,
    string? Db, string? Coll, string? App, string? Driver, string Status, bool Ack, bool Stream, int ReplyCount, int ReqBytes, int RespBytes,
    [property: JsonPropertyName("serverLatency_us")] long? ServerLatencyUs, Timings Timings, JsonElement? Reply, string? Lsid, long? TxnNumber,
    string? Comment, string ShapeHash, string? ValueHash, JsonElement? Shape, [property: JsonPropertyName("command_ejson")] string? CommandEjson,
    bool CmdTruncated, Dictionary<string, int>? SeqDocs, string? Compressor, string[]? Alerts, CursorDto? Cursor)
{
    public string Ns => Coll == null ? Db ?? "" : $"{Db}.{Coll}";
}

public sealed record AlertDto(string Id, string Rule, Dictionary<string, string> Scope, string Severity, string State, bool Muted,
    long FirstSeen, long LastSeen, long Occurrences, string[] SampleOpIds, string Summary, string? Details, long? ResolvedAt);

public sealed record OpWithAlerts(OpDetailDto Op, AlertDto[] Alerts);

public sealed record ShapeRow(string ShapeHash, string Command, string? Db, string? Coll, JsonElement Shape, long FirstSeen, long LastSeen,
    string[] Apps, long Count, long Errors, [property: JsonPropertyName("totalTime_us")] double TotalTimeUs,
    [property: JsonPropertyName("p50_us")] double P50Us, [property: JsonPropertyName("p95_us")] double P95Us,
    [property: JsonPropertyName("p99_us")] double P99Us, [property: JsonPropertyName("max_us")] double MaxUs,
    long RespBytes, long Returned, long MaxReturned, int? MaxInCardinality, double[][]? Series)
{
    public string Ns => Coll == null ? Db ?? "" : $"{Db}.{Coll}";
}

public sealed record ShapePage(long From, long To, ShapeRow[] Items);

public sealed record SeriesPoint(long Minute, long Count, long Errors, [property: JsonPropertyName("p50_us")] double P50Us, [property: JsonPropertyName("p95_us")] double P95Us);

public sealed record CursorStatsDto(long Closed, long Documents, long GetMores, [property: JsonPropertyName("meanTimeToExhaustion_us")] long? MeanTimeToExhaustionUs, Dictionary<string, long> Reasons);

public sealed record ShapeDetail(string ShapeHash, string Command, string? Db, string? Coll, JsonElement Shape, string[] Apps, long Count, long Errors,
    [property: JsonPropertyName("totalTime_us")] double TotalTimeUs, [property: JsonPropertyName("p50_us")] double P50Us,
    [property: JsonPropertyName("p95_us")] double P95Us, [property: JsonPropertyName("p99_us")] double P99Us, [property: JsonPropertyName("max_us")] double MaxUs,
    SeriesPoint[] Series, OpSummary[] Slowest, OpSummary[] Latest, CursorStatsDto? Cursors)
{
    public string Ns => Coll == null ? Db ?? "" : $"{Db}.{Coll}";
}

public sealed record ProducerDto(string Src, string Kind, string? Version, int Schema, string? Topology, string? SetName, JsonElement Members,
    string? Values, string? Compression, long FirstSeen, long LastSeen, long Records, long Rejected, long ClockSkewed, Dictionary<string, long> Dropped);

/// <summary>Talks only to <c>/api/v1</c> on the page's own origin (03 §10). A 401 sends the user to the login page.</summary>
public sealed class Api(HttpClient http, NavigationManager nav)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private async Task<T?> Get<T>(string path)
    {
        using var r = await http.GetAsync(path);
        if (r.StatusCode == HttpStatusCode.Unauthorized)
        {
            nav.NavigateTo("login?return=" + Uri.EscapeDataString(nav.ToBaseRelativePath(nav.Uri)));
            return default;
        }
        if (r.StatusCode == HttpStatusCode.NotFound) return default;
        r.EnsureSuccessStatusCode();
        return await r.Content.ReadFromJsonAsync<T>(Json);
    }

    private async Task<T[]> List<T>(string path)
    {
        var doc = await Get<JsonElement>(path);
        return doc.ValueKind == JsonValueKind.Object && doc.TryGetProperty("items", out var items)
            ? items.Deserialize<T[]>(Json) ?? []
            : [];
    }

    public Task<OpPage?> Ops(string query) => Get<OpPage>("api/v1/ops?" + query);
    public Task<OpWithAlerts?> Op(string id) => Get<OpWithAlerts>($"api/v1/ops/{Uri.EscapeDataString(id)}");
    public Task<ShapePage?> Shapes(string query) => Get<ShapePage>("api/v1/shapes?" + query);
    public Task<ShapeDetail?> Shape(string hash, string query) => Get<ShapeDetail>($"api/v1/shapes/{Uri.EscapeDataString(hash)}?{query}");
    public Task<AlertDto[]> Alerts(string query = "") => List<AlertDto>("api/v1/alerts?" + query);
    public Task<CursorDto[]> Cursors(string state) => List<CursorDto>("api/v1/cursors?state=" + state);
    public Task<ProducerDto[]> Producers() => List<ProducerDto>("api/v1/producers");
    public Task<JsonElement> Whoami() => Get<JsonElement>("api/v1/whoami");

    public async Task<bool> Mute(string id, bool muted) =>
        (await http.PostAsync($"api/v1/alerts/{Uri.EscapeDataString(id)}/{(muted ? "mute" : "unmute")}", null)).IsSuccessStatusCode;

    public async Task<bool> Login(string password) =>
        (await http.PostAsJsonAsync("api/v1/login", new { password })).IsSuccessStatusCode;

    public Task Logout() => http.PostAsync("api/v1/logout", null);

    /// <summary>The SSE stream (03 §8 /live), read through fetch streaming: no EventSource interop needed.</summary>
    public async IAsyncEnumerable<(string Event, string Data)> Live(string query, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "api/v1/live?" + query);
        req.SetBrowserResponseStreamingEnabled(true);
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (resp.StatusCode == HttpStatusCode.Unauthorized) { nav.NavigateTo("login?return="); yield break; }
        resp.EnsureSuccessStatusCode();
        using var reader = new StreamReader(await resp.Content.ReadAsStreamAsync(ct));
        string? ev = null;
        while (!ct.IsCancellationRequested && await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.StartsWith("event: ", StringComparison.Ordinal)) ev = line[7..];
            else if (line.StartsWith("data: ", StringComparison.Ordinal) && ev != null) { yield return (ev, line[6..]); ev = null; }
        }
    }
}

public static class Fmt
{
    public static string Us(double? us) => us switch
    {
        null => "–",
        < 1000 => $"{us:F0} µs",
        < 1_000_000 => $"{us / 1000:F1} ms",
        _ => $"{us / 1_000_000:F2} s",
    };

    public static string Bytes(long b) => b switch
    {
        < 1024 => $"{b} B",
        < 1024 * 1024 => $"{b / 1024.0:F1} KiB",
        _ => $"{b / 1048576.0:F1} MiB",
    };

    public static DateTime Time(long us) => DateTime.UnixEpoch.AddTicks(us * 10).ToLocalTime();
    public static string Clock(long us) => Time(us).ToString("HH:mm:ss.fff");
    public static string Stamp(long us) => Time(us).ToString("yyyy-MM-dd HH:mm:ss");

    public static string Ago(long us, long nowUs)
    {
        var s = (nowUs - us) / 1_000_000;
        return s switch { < 60 => $"{s}s", < 3600 => $"{s / 60}m", < 86400 => $"{s / 3600}h", _ => $"{s / 86400}d" };
    }

    public static long NowUs() => (DateTime.UtcNow - DateTime.UnixEpoch).Ticks / 10;
}
