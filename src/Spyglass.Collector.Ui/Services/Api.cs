using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.WebAssembly.Http;

namespace Spyglass.Collector.Ui.Services;

public sealed record LoginResult(bool Ok, string? Auth, string? Error);

/// <summary>
/// Talks only to <c>/api/v1</c> on the page's own origin (03 §10). A 401 on a read or a write sends the user to the login page;
/// any other failure throws <see cref="HttpRequestException"/> for the page to show. Every response also calibrates
/// <see cref="Fmt.ServerOffsetUs"/> from its <c>Date</c> header.
/// </summary>
public sealed class Api(HttpClient http, NavigationManager nav)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private Task? _clock;

    /// <summary>One cheap request before the first time-relative query, so ranges are computed on the server's clock.</summary>
    public Task EnsureClock() => _clock ??= Calibrate();

    private async Task Calibrate()
    {
        try { using var r = await http.GetAsync("api/v1/whoami"); Observe(r); }
        catch (HttpRequestException) { }
    }

    private static void Observe(HttpResponseMessage r)
    {
        if (r.Headers.Date is { } d) Fmt.ServerOffsetUs = Fmt.OffsetFromDate(d, DateTime.UtcNow);
    }

    private void ToLogin()
    {
        if (!nav.ToBaseRelativePath(nav.Uri).StartsWith("login", StringComparison.Ordinal))
            nav.NavigateTo("login?return=" + Uri.EscapeDataString(nav.ToBaseRelativePath(nav.Uri)));
    }

    private async Task<T?> Get<T>(string path, CancellationToken ct)
    {
        using var r = await http.GetAsync(path, ct);
        Observe(r);
        if (r.StatusCode == HttpStatusCode.Unauthorized) { ToLogin(); return default; }
        if (r.StatusCode == HttpStatusCode.NotFound) return default;
        r.EnsureSuccessStatusCode();
        return await r.Content.ReadFromJsonAsync<T>(Json, ct);
    }

    private async Task<T[]> List<T>(string path, CancellationToken ct)
    {
        var doc = await Get<JsonElement>(path, ct);
        return doc.ValueKind == JsonValueKind.Object && doc.TryGetProperty("items", out var items)
            ? items.Deserialize<T[]>(Json) ?? []
            : [];
    }

    public Task<OpPage?> Ops(string query, CancellationToken ct = default) => Get<OpPage>("api/v1/ops?" + query, ct);
    public Task<OpWithAlerts?> Op(string id, CancellationToken ct = default) => Get<OpWithAlerts>($"api/v1/ops/{Uri.EscapeDataString(id)}", ct);
    public Task<ShapePage?> Shapes(string query, CancellationToken ct = default) => Get<ShapePage>("api/v1/shapes?" + query, ct);
    public Task<ShapeDetail?> Shape(string hash, string query, CancellationToken ct = default) => Get<ShapeDetail>($"api/v1/shapes/{Uri.EscapeDataString(hash)}?{query}", ct);
    public Task<AlertDto[]> Alerts(string query = "", CancellationToken ct = default) => List<AlertDto>("api/v1/alerts?" + query, ct);
    public Task<CursorDto[]> Cursors(string state, CancellationToken ct = default) => List<CursorDto>("api/v1/cursors?state=" + state, ct);
    public Task<ProducerDto[]> Producers(CancellationToken ct = default) => List<ProducerDto>("api/v1/producers", ct);
    public Task<JsonElement> Whoami(CancellationToken ct = default) => Get<JsonElement>("api/v1/whoami", ct);

    /// <summary>False when the alert is gone (404); other failures throw.</summary>
    public async Task<bool> Mute(string id, bool muted, CancellationToken ct = default)
    {
        using var r = await http.PostAsync($"api/v1/alerts/{Uri.EscapeDataString(id)}/{(muted ? "mute" : "unmute")}", null, ct);
        Observe(r);
        if (r.StatusCode == HttpStatusCode.Unauthorized) { ToLogin(); return false; }
        if (r.StatusCode == HttpStatusCode.NotFound) return false;
        r.EnsureSuccessStatusCode();
        return true;
    }

    /// <summary>Wrong password (401) is a result, not an exception; a network failure throws.</summary>
    public async Task<LoginResult> Login(string password, CancellationToken ct = default)
    {
        using var r = await http.PostAsJsonAsync("api/v1/login", new { password }, ct);
        Observe(r);
        if (r.IsSuccessStatusCode)
        {
            string? auth = null;
            try
            {
                var body = await r.Content.ReadFromJsonAsync<JsonElement>(Json, ct);
                if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty("auth", out var a)) auth = a.GetString();
            }
            catch (JsonException) { }
            return new(true, auth, null);
        }
        return r.StatusCode == HttpStatusCode.Unauthorized ? new(false, null, null) : new(false, null, $"{(int)r.StatusCode} {r.ReasonPhrase}");
    }

    public async Task Logout() { using var _ = await http.PostAsync("api/v1/logout", null); }

    /// <summary>
    /// The SSE stream (03 §8 /live), read through fetch streaming: no EventSource interop needed. Yields <c>("open", "")</c> once the
    /// response headers are in (the subscription exists), then the server's events.
    /// </summary>
    public async IAsyncEnumerable<(string Event, string Data)> Live(string query, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "api/v1/live?" + query);
        req.SetBrowserResponseStreamingEnabled(true);
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        Observe(resp);
        if (resp.StatusCode == HttpStatusCode.Unauthorized) { ToLogin(); yield break; }
        resp.EnsureSuccessStatusCode();
        yield return ("open", "");
        using var reader = new StreamReader(await resp.Content.ReadAsStreamAsync(ct));
        string? ev = null;
        while (!ct.IsCancellationRequested && await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.StartsWith("event: ", StringComparison.Ordinal)) ev = line[7..];
            else if (line.StartsWith("data: ", StringComparison.Ordinal) && ev != null) { yield return (ev, line[6..]); ev = null; }
        }
    }
}
