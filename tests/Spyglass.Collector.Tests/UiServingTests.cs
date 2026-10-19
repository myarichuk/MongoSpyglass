using System.Net;
using System.Net.Http.Headers;

namespace Spyglass.Collector.Tests;

/// <summary>03 §10 static UI: precompressed siblings by Accept-Encoding, caching by fingerprint, CSP everywhere, SPA routes.</summary>
public sealed class UiServingTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "spyglass-ui-" + Guid.NewGuid().ToString("N")[..8]);
    private Server _s = null!;
    private HttpClient _http = null!;
    private const string Wasm = "_framework/app.0123456789.wasm", Loader = "_framework/blazor.webassembly.js";

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(Path.Combine(_root, "_framework"));
        await File.WriteAllTextAsync(Path.Combine(_root, "index.html"), "<!doctype html><title>spyglass</title>");
        foreach (var f in (string[])[Wasm, Loader])
        {
            await File.WriteAllTextAsync(Path.Combine(_root, f), "raw");
            await File.WriteAllTextAsync(Path.Combine(_root, f + ".br"), "brotli");
            await File.WriteAllTextAsync(Path.Combine(_root, f + ".gz"), "gzip");
        }
        _s = await Server.StartAsync(new CollectorOptions { Bind = new IPEndPoint(IPAddress.Loopback, 0), IngestInsecure = true, Auth = AuthMode.None, Data = null, Workers = 1 }, _root);
        _http = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.None }) { BaseAddress = _s.BaseAddress };
    }

    public async ValueTask DisposeAsync()
    {
        _http.Dispose();
        await _s.DisposeAsync();
        Directory.Delete(_root, true);
    }

    private async Task<HttpResponseMessage> Get(string path, string? acceptEncoding = null, string? ifNoneMatch = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, path);
        if (acceptEncoding != null) req.Headers.TryAddWithoutValidation("Accept-Encoding", acceptEncoding);
        if (ifNoneMatch != null) req.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
        return await _http.SendAsync(req);
    }

    [Theory]
    [InlineData("gzip, deflate, br, zstd", "br", "brotli")]
    [InlineData("gzip, deflate", "gzip", "gzip")]          // what browsers send over plain HTTP
    [InlineData("br;q=0, gzip", "gzip", "gzip")]
    [InlineData(null, null, "raw")]
    public async Task Precompressed_siblings_follow_accept_encoding(string? accept, string? encoding, string body)
    {
        var r = await Get("/" + Wasm, accept);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(body, await r.Content.ReadAsStringAsync());
        Assert.Equal(encoding, r.Content.Headers.ContentEncoding.SingleOrDefault());
        Assert.Equal("application/wasm", r.Content.Headers.ContentType?.MediaType);
        Assert.Equal("public, max-age=31536000, immutable", r.Headers.CacheControl?.ToString());
        Assert.Contains("default-src 'self'", r.Headers.GetValues("Content-Security-Policy").Single());
    }

    [Fact]
    public async Task Unfingerprinted_assets_revalidate()
    {
        var r = await Get("/" + Loader, "br");
        Assert.Equal("no-cache", r.Headers.CacheControl?.ToString());
        var etag = r.Headers.ETag!.ToString();
        var again = await Get("/" + Loader, "br", etag);
        Assert.Equal(HttpStatusCode.NotModified, again.StatusCode);
        Assert.Equal(0, (await again.Content.ReadAsByteArrayAsync()).Length);
    }

    [Fact]
    public async Task Client_routes_get_index_html_with_the_csp_and_the_api_does_not()
    {
        var r = await Get("/ops/01JABCDEF");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains("<title>spyglass</title>", await r.Content.ReadAsStringAsync());
        Assert.Contains("default-src 'self'", r.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal(HttpStatusCode.NotFound, (await Get("/api/v1/no-such-thing")).StatusCode);
    }
}
