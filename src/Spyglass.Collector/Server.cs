using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Spyglass.Collector.Analysis;
using Spyglass.Collector.Api;
using Spyglass.Collector.Model;
using Spyglass.Collector.Storage;

namespace Spyglass.Collector;

/// <summary>
/// The collector's HTTP host (03 §8, §9): <c>POST /ingest/v0</c>, the REST + SSE API under <c>/api/v1</c>,
/// <c>/healthz</c>, and the static UI. Auth: a bearer token for ingest; a password, a trusted header or
/// nothing (loopback only) for the API and UI.
/// </summary>
public sealed class Server : IAsyncDisposable
{
    private const string SessionCookie = "spyglass_session";
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(12);

    private readonly WebApplication _app;
    private readonly object _store;
    private readonly ConcurrentDictionary<string, DateTime> _sessions = new();

    public Collector Collector { get; }
    public CollectorOptions Options { get; }
    public Uri BaseAddress { get; private set; } = null!;

    private Server(CollectorOptions options, object store, Collector collector, WebApplication app)
    {
        Options = options;
        _store = store;
        Collector = collector;
        _app = app;
    }

    /// <param name="uiRoot">directory with the built UI (index.html); null serves none.</param>
    public static async Task<Server> StartAsync(CollectorOptions options, string? uiRoot = null)
    {
        CollectorOptions.Validate(options);
        object store = options.Data == null ? new MemoryStore() : new SqliteStore(options.Data);
        var collector = new Collector(options, (IOperationStore)store, (IAggregateStore)store, (IAlertStore)store, callsites: (ICallsiteStore)store);

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.Listen(options.Bind);
            k.Limits.MaxRequestBodySize = options.MaxBodyBytes;
            k.AddServerHeader = false;
        });
        var app = builder.Build();
        var server = new Server(options, store, collector, app);
        server.Map(uiRoot);
        collector.Start();
        await app.StartAsync().ConfigureAwait(false);
        // the bound address (port 0 → the one Kestrel picked)
        var addr = app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!.Addresses.First();
        server.BaseAddress = new Uri(addr.Replace("0.0.0.0", "127.0.0.1").Replace("[::]", "127.0.0.1"));
        return server;
    }

    private void Map(string? uiRoot)
    {
        var app = _app;
        // no auth (03 §8): states and counts only
        app.MapGet("/healthz", () => Health(live: true));
        app.MapGet("/readyz", () => Health(live: false));
        app.MapPost("/ingest/v0", Ingest);

        var api = app.MapGroup("/api/v1");
        api.AddEndpointFilter(async (ctx, next) => Authorized(ctx.HttpContext) ? await next(ctx) : Results.Unauthorized());
        app.MapPost("/api/v1/login", (Delegate)Login); // outside the filter: it is how one gets authorized
        app.MapPost("/api/v1/logout", Logout);
        api.MapGet("/whoami", (HttpContext c) => Obj(("auth", Options.Auth.ToString().ToLowerInvariant()), ("user", User(c))));
        api.MapGet("/ops", Ops);
        api.MapGet("/ops/{id}", Op);
        api.MapGet("/shapes", Shapes);
        api.MapGet("/shapes/{hash}", Shape);
        api.MapGet("/alerts", Alerts);
        api.MapPost("/alerts/{id}/mute", (string id) => Mute(id, true));
        api.MapPost("/alerts/{id}/unmute", (string id) => Mute(id, false));
        api.MapGet("/cursors", Cursors);
        api.MapGet("/connections", Connections);
        api.MapGet("/apps", Apps);
        api.MapGet("/producers", Producers);
        api.MapGet("/live", Live);
        api.MapPost("/export", Export);
        api.MapGet("/metrics", () => Results.Text(Collector.RenderMetrics(), "text/plain; version=0.0.4"));
        api.MapGet("/uows", () => Raw(Json.Write(w =>
        {
            w.WriteStartObject();
            w.WriteStartArray("items");
            w.WriteEndArray();
            w.WriteString("note", "units of work are M3 (03 §5.3); joined ops carry uowId and filter with /ops?uow=");
            w.WriteEndObject();
        })));

        if (uiRoot != null && File.Exists(Path.Combine(uiRoot, "index.html")))
            MapUi(app, Path.GetFullPath(uiRoot));
    }

    private const string Csp =
        "default-src 'self'; script-src 'self' 'wasm-unsafe-eval' 'unsafe-eval'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self'";

    /// <summary>Blazor's fingerprinted assets (<c>name.0123456789.wasm</c>): their content never changes under that name.</summary>
    private static readonly System.Text.RegularExpressions.Regex Fingerprinted = new(@"^_framework/.+\.[a-z0-9]{10}\.(wasm|js|dat|json)$",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static void UiHeaders(HttpResponse r, string relativePath)
    {
        r.Headers["X-Content-Type-Options"] = "nosniff";
        r.Headers["Content-Security-Policy"] = Csp;
        r.Headers.CacheControl = Fingerprinted.IsMatch(relativePath) ? "public, max-age=31536000, immutable" : "no-cache";
    }

    /// <summary>
    /// The SPA (03 §10): static files, the build's precompressed <c>.br</c>/<c>.gz</c> siblings when the browser takes
    /// them (the runtime is 6.5 MB raw, 2.3 MB brotli), immutable caching for fingerprinted assets, and
    /// <c>index.html</c> for client-side routes.
    /// </summary>
    private static void MapUi(WebApplication app, string root)
    {
        var files = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(root);
        var types = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();
        app.Use(async (c, next) =>
        {
            var req = c.Request;
            if ((HttpMethods.IsGet(req.Method) || HttpMethods.IsHead(req.Method)) && req.Path.Value is { Length: > 1 } path &&
                !req.Path.StartsWithSegments("/api") && !req.Path.StartsWithSegments("/ingest"))
            {
                var rel = path[1..];
                var accepted = req.GetTypedHeaders().AcceptEncoding;
                foreach (var (token, ext) in (ReadOnlySpan<(string, string)>)[("br", ".br"), ("gzip", ".gz")])
                {
                    if (!accepted.Any(a => string.Equals(a.Value.Value, token, StringComparison.OrdinalIgnoreCase) && (a.Quality ?? 1) > 0)) continue;
                    var f = files.GetFileInfo(rel + ext);
                    if (!f.Exists || f.IsDirectory) continue;
                    var res = c.Response;
                    UiHeaders(res, rel);
                    res.Headers.Vary = "Accept-Encoding";
                    var etag = $"\"{f.LastModified.UtcTicks:x}-{f.Length:x}-{token}\"";
                    res.Headers.ETag = etag;
                    if (req.Headers.IfNoneMatch.Contains(etag)) { res.StatusCode = StatusCodes.Status304NotModified; return; }
                    res.Headers.ContentEncoding = token;
                    res.ContentType = types.TryGetContentType(rel, out var ct) ? ct : "application/octet-stream";
                    res.ContentLength = f.Length;
                    if (HttpMethods.IsGet(req.Method)) await res.SendFileAsync(f).ConfigureAwait(false);
                    return;
                }
            }
            await next(c).ConfigureAwait(false);
        });
        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = files,
            ServeUnknownFileTypes = true, // .wasm, .dat, .blat
            OnPrepareResponse = c => UiHeaders(c.Context.Response, c.Context.Request.Path.Value?.TrimStart('/') ?? ""),
        });
        app.MapFallback(async c =>
        {
            if (c.Request.Path.StartsWithSegments("/api")) { c.Response.StatusCode = 404; return; }
            UiHeaders(c.Response, "index.html");
            c.Response.ContentType = "text/html; charset=utf-8";
            await c.Response.SendFileAsync(Path.Combine(root, "index.html")).ConfigureAwait(false);
        });
    }

    private IResult Health(bool live)
    {
        var h = Collector.Health();
        var ok = live ? h.Live : h.Ready;
        var body = Json.Write(w =>
        {
            w.WriteStartObject();
            w.WriteString("status", ok ? "ok" : "failing");
            w.WriteBoolean("live", h.Live);
            w.WriteBoolean("ready", h.Ready);
            w.WriteStartObject("checks");
            foreach (var c in h.Checks)
            {
                w.WriteStartObject(c.Name);
                w.WriteBoolean("ok", c.Ok);
                w.WriteString("detail", c.Detail);
                w.WriteEndObject();
            }
            w.WriteEndObject();
            w.WriteEndObject();
        });
        return Results.Text(Encoding.UTF8.GetString(body), "application/json", Encoding.UTF8, ok ? 200 : 503);
    }

    // ---- auth (03 §9) -----------------------------------------------------------------------

    private bool Authorized(HttpContext c) => User(c) != null;

    private string? User(HttpContext c)
    {
        switch (Options.Auth)
        {
            case AuthMode.None:
                return "anonymous";
            case AuthMode.Header:
                return c.Request.Headers[Options.AuthHeader].FirstOrDefault() is { Length: > 0 } u ? u : null;
            default:
                if (c.Request.Cookies[SessionCookie] is { } sid && _sessions.TryGetValue(sid, out var exp) && exp > DateTime.UtcNow)
                    return "admin";
                var h = c.Request.Headers.Authorization.ToString();
                if (h.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
                {
                    string decoded;
                    try { decoded = Encoding.UTF8.GetString(Convert.FromBase64String(h[6..].Trim())); }
                    catch (FormatException) { return null; }
                    var colon = decoded.IndexOf(':');
                    if (colon >= 0 && Same(decoded[(colon + 1)..], Options.AdminPassword!)) return "admin";
                }
                return null;
        }
    }

    private static bool Same(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(a)), SHA256.HashData(Encoding.UTF8.GetBytes(b)));

    private async Task<IResult> Login(HttpContext c)
    {
        if (Options.Auth != AuthMode.Password) return Obj(("auth", Options.Auth.ToString().ToLowerInvariant()));
        string? password = null;
        if (c.Request.HasFormContentType) password = (await c.Request.ReadFormAsync().ConfigureAwait(false))["password"];
        else
        {
            try
            {
                using var doc = await JsonDocument.ParseAsync(c.Request.Body).ConfigureAwait(false);
                password = doc.RootElement.TryGetProperty("password", out var p) ? p.GetString() : null;
            }
            catch (JsonException) { }
        }
        if (password == null || !Same(password, Options.AdminPassword!))
        {
            await Task.Delay(250).ConfigureAwait(false); // blunt the online guessing rate
            return Results.Unauthorized();
        }
        foreach (var (k, v) in _sessions) if (v < DateTime.UtcNow) _sessions.TryRemove(k, out _);
        var sid = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        _sessions[sid] = DateTime.UtcNow + SessionLifetime;
        c.Response.Cookies.Append(SessionCookie, sid, new CookieOptions
        {
            HttpOnly = true, SameSite = SameSiteMode.Strict, Secure = c.Request.IsHttps, Path = "/", MaxAge = SessionLifetime,
        });
        return Obj(("ok", true));
    }

    private IResult Logout(HttpContext c)
    {
        if (c.Request.Cookies[SessionCookie] is { } sid) _sessions.TryRemove(sid, out _);
        c.Response.Cookies.Delete(SessionCookie);
        return Obj(("ok", true));
    }

    // ---- ingest (02 §5) ---------------------------------------------------------------------

    private async Task Ingest(HttpContext c)
    {
        if (!Options.IngestInsecure)
        {
            var h = c.Request.Headers.Authorization.ToString();
            if (!h.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) { c.Response.StatusCode = 401; return; }
            if (!Same(h[7..].Trim(), Options.Token!)) { c.Response.StatusCode = 403; return; }
        }
        var length = c.Request.ContentLength;
        if (length > Options.MaxBodyBytes) { c.Response.StatusCode = 413; return; }
        using var ms = new MemoryStream(length is { } l ? (int)l : 64 * 1024);
        try
        {
            var buf = new byte[64 * 1024];
            int n;
            while ((n = await c.Request.Body.ReadAsync(buf, c.RequestAborted).ConfigureAwait(false)) > 0)
            {
                if (ms.Length + n > Options.MaxBodyBytes) { c.Response.StatusCode = 413; return; }
                ms.Write(buf, 0, n);
            }
        }
        catch (BadHttpRequestException) { c.Response.StatusCode = 413; return; }

        var r = Collector.Ingest(ms.GetBuffer().AsSpan(0, (int)ms.Length), c.Request.Headers.ContentEncoding.ToString(),
            c.Request.Headers["X-Spyglass-Producer"].FirstOrDefault());
        c.Response.StatusCode = r.Status;
        if (r.Rejected > 0) c.Response.Headers["X-Spyglass-Rejected"] = r.Rejected.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (r.Status == 429) c.Response.Headers.RetryAfter = "1";
        if (r.Error != null && r.Status >= 400) await c.Response.WriteAsync(r.Error).ConfigureAwait(false);
    }

    // ---- API (03 §8) ------------------------------------------------------------------------

    private static long? L(HttpRequest r, string name) => long.TryParse(r.Query[name], out var v) ? v : null;
    private static string? S(HttpRequest r, string name) => r.Query[name] is { Count: > 0 } v && v[0] is { Length: > 0 } s ? s : null;

    private static IResult Raw(byte[] json) => Results.Bytes(json, "application/json");

    /// <summary>A flat JSON object of strings and booleans, without reflection (the container build is trimmed).</summary>
    private static IResult Obj(params (string Name, object? Value)[] fields) => Raw(Json.Write(w =>
    {
        w.WriteStartObject();
        foreach (var (name, value) in fields)
            switch (value)
            {
                case null: w.WriteNull(name); break;
                case bool b: w.WriteBoolean(name, b); break;
                default: w.WriteString(name, value.ToString()); break;
            }
        w.WriteEndObject();
    }));

    private IResult Ops(HttpRequest req)
    {
        var q = new OpQuery
        {
            From = L(req, "from"), To = L(req, "to"), App = S(req, "app"), Coll = S(req, "coll"), Command = S(req, "command"),
            Shape = S(req, "shape"), Status = S(req, "status"), MinLatencyUs = L(req, "minLatencyUs"), Uow = S(req, "uow"),
            Limit = (int)Math.Clamp(L(req, "limit") ?? 100, 1, 1000), Cursor = S(req, "cursor"),
        };
        var ops = Collector.Ops.Query(q);
        return Raw(Json.Write(w =>
        {
            w.WriteStartObject();
            w.WriteStartArray("items");
            foreach (var o in ops) Json.OpSummary(w, o);
            w.WriteEndArray();
            if (ops.Count == q.Limit) w.WriteString("next", ops[^1].Id); else w.WriteNull("next");
            w.WriteEndObject();
        }));
    }

    private IResult Op(string id)
    {
        var o = Collector.Ops.Get(id) ?? Collector.Analysis.Waiting(id); // live but still waiting for context (02 §6.2)
        if (o == null) return Results.NotFound();
        var callsite = o.Ctx?.Callsite is { } cs ? Collector.Callsites.Callsite(cs) : null;
        o.ShapeJson = Collector.Aggregates.Shape(o.ShapeHash)?.ShapeJson;
        var a = Collector.Analysis;
        var cursorId = o.Op.Command == "getMore" ? CursorIdOf(o) : o.Op.Reply?.CursorId;
        var cursor = cursorId is { } cid and not 0
            ? a.OpenCursors.Concat(a.ClosedCursors).FirstOrDefault(c => c.CursorId == cid && c.ProxyId == o.Op.ProxyId && c.Member == o.Op.Member)
            : null;
        var alerts = a.Alerts.Snapshot().Where(x => x.SampleOpIds.Contains(o.Id) || (o.Alerts?.Contains(x.Rule) == true && x.Scope.GetValueOrDefault("shapeHash") == o.ShapeHash)).ToList();
        return Raw(Json.Write(w =>
        {
            w.WriteStartObject();
            w.WritePropertyName("op");
            Json.OpDetail(w, o, cursor, callsite);
            w.WriteStartArray("alerts");
            foreach (var x in alerts) Json.Alert(w, x);
            w.WriteEndArray();
            w.WriteEndObject();
        }));
    }

    private static long? CursorIdOf(Operation o)
    {
        if (o.Op.Cmd == null) return null;
        try
        {
            var d = Spyglass.Collector.Shapes.FastBson.Decode(o.Op.Cmd);
            return d.TryGetValue("getMore", out var v) && v.IsNumeric ? v.ToInt64() : null;
        }
        catch (Exception) { return null; }
    }

    private (long From, long To) Range(HttpRequest req)
    {
        var now = Collector.NowUs();
        var to = L(req, "to") ?? now;
        var from = L(req, "from") ?? to - (long)TimeSpan.FromHours(1).TotalMicroseconds;
        return (from, to);
    }

    /// <summary>Shape leaderboard: buckets in range merged per shape, sorted by totalTime | p95 | count | errors.</summary>
    private IResult Shapes(HttpRequest req)
    {
        var (from, to) = Range(req);
        var app = S(req, "app");
        var coll = S(req, "coll");
        var sort = S(req, "sort") ?? "totalTime";
        var limit = (int)Math.Clamp(L(req, "limit") ?? 100, 1, 1000);
        var shapes = Collector.Aggregates.Shapes().ToDictionary(s => s.ShapeHash);
        var rows = Collector.Aggregates.Buckets(from / 60_000_000, to / 60_000_000)
            .GroupBy(b => b.ShapeHash)
            .Select(g =>
            {
                var sum = new MinuteBucket { ShapeHash = g.Key, Minute = 0 };
                foreach (var b in g) sum.Merge(b);
                return (Shape: shapes.GetValueOrDefault(g.Key), Sum: sum, Series: g.OrderBy(b => b.Minute).ToList());
            })
            .Where(x => x.Shape != null && (app == null || x.Shape.Apps.Contains(app)) && (coll == null || x.Shape.Coll == coll))
            .ToList();
        IEnumerable<(ShapeInfo? Shape, MinuteBucket Sum, List<MinuteBucket> Series)> sorted = sort switch
        {
            "p95" => rows.OrderByDescending(x => x.Sum.Latency.Quantile(0.95)),
            "count" => rows.OrderByDescending(x => x.Sum.Count),
            "errors" => rows.OrderByDescending(x => x.Sum.Errors),
            _ => rows.OrderByDescending(x => x.Sum.Latency.Sum),
        };
        return Raw(Json.Write(w =>
        {
            w.WriteStartObject();
            w.WriteNumber("from", from);
            w.WriteNumber("to", to);
            w.WriteStartArray("items");
            foreach (var (s, b, series) in sorted.Take(limit))
            {
                w.WriteStartObject();
                ShapeFields(w, s!);
                Stats(w, b);
                w.WriteStartArray("series"); // sparkline: [minute, count, p95_us]
                foreach (var m in series)
                {
                    w.WriteStartArray();
                    w.WriteNumberValue(m.Minute);
                    w.WriteNumberValue(m.Count);
                    w.WriteNumberValue(Math.Round(m.Latency.Quantile(0.95)));
                    w.WriteEndArray();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }));
    }

    private static void ShapeFields(Utf8JsonWriter w, ShapeInfo s)
    {
        w.WriteString("shapeHash", s.ShapeHash);
        w.WriteString("command", s.Command);
        if (s.Db != null) w.WriteString("db", s.Db);
        if (s.Coll != null) w.WriteString("coll", s.Coll);
        w.WritePropertyName("shape");
        w.WriteRawValue(s.ShapeJson);
        w.WriteNumber("firstSeen", s.FirstSeen);
        w.WriteNumber("lastSeen", s.LastSeen);
        w.WriteStartArray("apps");
        foreach (var a in s.Apps.Order()) w.WriteStringValue(a);
        w.WriteEndArray();
        if (s.MaxInCardinality > 0) w.WriteNumber("maxInCardinality", s.MaxInCardinality);
    }

    private static void Stats(Utf8JsonWriter w, MinuteBucket b)
    {
        w.WriteNumber("count", b.Count);
        w.WriteNumber("errors", b.Errors);
        w.WriteNumber("totalTime_us", Math.Round(b.Latency.Sum));
        w.WriteNumber("p50_us", Math.Round(b.Latency.Quantile(0.5)));
        w.WriteNumber("p95_us", Math.Round(b.Latency.Quantile(0.95)));
        w.WriteNumber("p99_us", Math.Round(b.Latency.Quantile(0.99)));
        w.WriteNumber("max_us", Math.Round(b.Latency.MaxValue));
        w.WriteNumber("respBytes", b.SumRespBytes);
        w.WriteNumber("reqBytes", b.SumReqBytes);
        w.WriteNumber("returned", b.SumReturned);
        w.WriteNumber("maxReturned", b.MaxReturned);
    }

    private IResult Shape(string hash, HttpRequest req)
    {
        var s = Collector.Aggregates.Shape(hash);
        if (s == null) return Results.NotFound();
        var (from, to) = Range(req);
        var series = Collector.Aggregates.Buckets(from / 60_000_000, to / 60_000_000, hash).OrderBy(b => b.Minute).ToList();
        var sum = new MinuteBucket { ShapeHash = hash, Minute = 0 };
        foreach (var b in series) sum.Merge(b);
        var examples = s.Slowest.Concat(s.Latest).Select(e => e.OpId).Distinct().Select(Collector.Ops.Get).Where(o => o != null).ToDictionary(o => o!.Id);
        var cursorStats = Collector.Analysis.CursorStats.GetValueOrDefault(hash);
        return Raw(Json.Write(w =>
        {
            w.WriteStartObject();
            ShapeFields(w, s);
            Stats(w, sum);
            w.WriteStartArray("series");
            foreach (var m in series)
            {
                w.WriteStartObject();
                w.WriteNumber("minute", m.Minute);
                w.WriteNumber("count", m.Count);
                w.WriteNumber("errors", m.Errors);
                w.WriteNumber("p50_us", Math.Round(m.Latency.Quantile(0.5)));
                w.WriteNumber("p95_us", Math.Round(m.Latency.Quantile(0.95)));
                w.WriteEndObject();
            }
            w.WriteEndArray();
            foreach (var (name, list) in new[] { ("slowest", s.Slowest), ("latest", s.Latest) })
            {
                w.WriteStartArray(name);
                foreach (var e in list)
                    if (examples.TryGetValue(e.OpId, out var o)) Json.OpSummary(w, o!);
                w.WriteEndArray();
            }
            if (cursorStats != null)
            {
                w.WriteStartObject("cursors");
                w.WriteNumber("closed", cursorStats.Closed);
                w.WriteNumber("documents", cursorStats.Documents);
                w.WriteNumber("getMores", cursorStats.GetMores);
                if (cursorStats.Exhausted > 0) w.WriteNumber("meanTimeToExhaustion_us", cursorStats.ExhaustionUsSum / cursorStats.Exhausted);
                w.WriteStartObject("reasons");
                foreach (var (k, v) in cursorStats.Reasons) w.WriteNumber(k, v);
                w.WriteEndObject();
                w.WriteEndObject();
            }
            w.WriteEndObject();
        }));
    }

    private IResult Alerts(HttpRequest req)
    {
        var state = S(req, "state");
        var rule = S(req, "rule");
        var list = Collector.Analysis.Alerts.Snapshot()
            .Where(a => state == null || (state == "open") == a.Open)
            .Where(a => rule == null || a.Rule == rule)
            .OrderByDescending(a => a.Open).ThenByDescending(a => a.LastSeen).ToList();
        return Raw(Json.Write(w =>
        {
            w.WriteStartObject();
            w.WriteStartArray("items");
            foreach (var a in list) Json.Alert(w, a);
            w.WriteEndArray();
            w.WriteEndObject();
        }));
    }

    private async Task<IResult> Mute(string id, bool muted) =>
        await Collector.Analysis.Invoke(() => Collector.Analysis.Alerts.SetMuted(id, muted)).ConfigureAwait(false)
            ? Obj(("id", id), ("muted", muted))
            : Results.NotFound();

    private IResult Cursors(HttpRequest req)
    {
        var state = S(req, "state") ?? "open";
        var reason = S(req, "reason");
        var a = Collector.Analysis;
        var list = (state == "closed" ? a.ClosedCursors.Reverse() : a.OpenCursors.OrderBy(c => c.OpenedTs))
            .Where(c => reason == null || c.ClosedReason == reason).Take(1000).ToList();
        return Raw(Json.Write(w =>
        {
            w.WriteStartObject();
            w.WriteStartArray("items");
            foreach (var c in list) Json.Cursor(w, c);
            w.WriteEndArray();
            w.WriteEndObject();
        }));
    }

    private IResult Connections() => Raw(Json.Write(w =>
    {
        w.WriteStartObject();
        w.WriteStartArray("items");
        foreach (var c in Collector.Connections.All.OrderBy(c => c.OpenedTs).Take(5000))
        {
            w.WriteStartObject();
            w.WriteString("proxyId", c.ProxyId);
            w.WriteNumber("connSeq", c.ConnSeq);
            w.WriteString("member", c.Member);
            if (c.ServerConnectionId is { } sid) w.WriteNumber("serverConnectionId", sid);
            foreach (var (k, v) in new[] { ("client", c.Client), ("app", c.App), ("driver", c.Driver), ("driverVersion", c.DriverVersion), ("exporter", c.Exporter), ("os", c.Os), ("platform", c.Platform), ("closeReason", c.CloseReason) })
                if (v != null) w.WriteString(k, v);
            w.WriteBoolean("tls", c.Tls);
            w.WriteNumber("openedTs", c.OpenedTs);
            if (c.ClosedTs is { } ct) w.WriteNumber("closedTs", ct);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
    }));

    private IResult Apps() => Raw(Json.Write(w =>
    {
        w.WriteStartObject();
        w.WriteStartArray("items");
        foreach (var g in Collector.Connections.All.GroupBy(c => c.App ?? c.Driver ?? "(unknown)").OrderBy(g => g.Key))
        {
            w.WriteStartObject();
            w.WriteString("app", g.Key);
            w.WriteNumber("connections", g.Count(c => c.ClosedTs == null));
            w.WriteStartArray("drivers");
            foreach (var d in g.Select(c => $"{c.Driver} {c.DriverVersion}".Trim()).Distinct().Order()) w.WriteStringValue(d);
            w.WriteEndArray();
            w.WriteBoolean("exporter", g.Any(c => c.Exporter != null));
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
    }));

    private IResult Producers() => Raw(Json.Write(w =>
    {
        w.WriteStartObject();
        w.WriteStartArray("items");
        foreach (var p in Collector.Producers.All.OrderBy(p => p.Src))
        {
            w.WriteStartObject();
            w.WriteString("src", p.Src);
            w.WriteString("kind", p.Kind);
            if (p.Version != null) w.WriteString("version", p.Version);
            w.WriteNumber("schema", p.Schema);
            if (p.Topology != null) w.WriteString("topology", p.Topology);
            if (p.SetName != null) w.WriteString("setName", p.SetName);
            w.WriteStartArray("members");
            foreach (var (m, u) in p.Members) { w.WriteStartObject(); w.WriteString("member", m); w.WriteString("upstream", u); w.WriteEndObject(); }
            w.WriteEndArray();
            if (p.Values != null) w.WriteString("values", p.Values);
            if (p.Compression != null) w.WriteString("compression", p.Compression);
            w.WriteNumber("firstSeen", p.FirstSeen);
            w.WriteNumber("lastSeen", p.LastSeen);
            w.WriteNumber("records", p.Records);
            w.WriteNumber("rejected", p.Rejected);
            w.WriteNumber("clockSkewed", p.ClockSkewed);
            w.WriteStartObject("dropped");
            foreach (var (k, v) in p.Dropped) w.WriteNumber(k, v);
            w.WriteEndObject();
            foreach (var (k, v) in new[] { ("runtime", p.Runtime), ("driver", p.Driver), ("host", p.Host), ("app", p.App), ("appVersion", p.AppVersion) })
                if (v != null) w.WriteString(k, v);
            if (p.Pid is { } pid) w.WriteNumber("pid", pid);
            if (p.Kind != "proxy" && Volatile.Read(ref p.Cmds) > 0)
            {
                w.WriteStartObject("join");
                w.WriteNumber("cmds", Volatile.Read(ref p.Cmds));
                w.WriteNumber("joined", Volatile.Read(ref p.Joined));
                w.WriteNumber("orphans", Volatile.Read(ref p.Orphans));
                w.WriteNumber("conflicts", Volatile.Read(ref p.Conflicts));
                if (p.JoinRate is { } jr) w.WriteNumber("rate", Math.Round(jr, 5));
                if (p.SkewUs is { } sk) w.WriteNumber("skew_us", sk);
                w.WriteEndObject();
            }
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
    }));

    /// <summary>SSE (03 §8): <c>op.created</c>, <c>op.updated</c>, <c>alert.opened</c>, <c>alert.updated</c>.</summary>
    private async Task Live(HttpContext c)
    {
        var app = S(c.Request, "app");
        var coll = S(c.Request, "coll");
        var command = S(c.Request, "command");
        var status = S(c.Request, "status");
        var minLat = L(c.Request, "minLatencyUs");
        var uow = S(c.Request, "uow");
        bool Filter(LiveEvent e) => e.Op is not { } o ||
            ((app == null || o.App == app) && (coll == null || o.Op.Coll == coll) && (command == null || o.Op.Command == command) &&
             (status == null || o.Op.Status == status) && (minLat == null || o.ServerLatencyUs >= minLat) &&
             (uow == null || Volatile.Read(ref o.Ctx)?.UowId == uow));

        c.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        c.Response.ContentType = "text/event-stream";
        c.Response.Headers.CacheControl = "no-cache";
        c.Response.Headers["X-Accel-Buffering"] = "no";
        using var sub = Collector.Live.Subscribe(Filter);
        var ct = c.RequestAborted;
        await c.Response.WriteAsync(": connected\n\n", ct).ConfigureAwait(false);
        await c.Response.Body.FlushAsync(ct).ConfigureAwait(false);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                using var heartbeat = CancellationTokenSource.CreateLinkedTokenSource(ct);
                heartbeat.CancelAfter(TimeSpan.FromSeconds(15));
                bool any;
                try { any = await sub.Reader.WaitToReadAsync(heartbeat.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    await c.Response.WriteAsync(": ping\n\n", ct).ConfigureAwait(false);
                    await c.Response.Body.FlushAsync(ct).ConfigureAwait(false);
                    continue;
                }
                if (!any) break;
                while (sub.Reader.TryRead(out var e))
                {
                    await c.Response.WriteAsync($"event: {e.Name}\ndata: ", ct).ConfigureAwait(false);
                    await c.Response.Body.WriteAsync(e.Payload, ct).ConfigureAwait(false);
                    await c.Response.WriteAsync("\n\n", ct).ConfigureAwait(false);
                }
                await c.Response.Body.FlushAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
    }

    /// <summary>NDJSON download of ops and alerts (03 §8, §9: the header names the proxies' redaction modes).</summary>
    private async Task Export(HttpContext c)
    {
        var (from, to) = Range(c.Request);
        c.Response.ContentType = "application/x-ndjson";
        c.Response.Headers.ContentDisposition = $"attachment; filename=\"spyglass-export-{from}-{to}.ndjson\"";
        var header = Json.Write(w =>
        {
            w.WriteStartObject();
            w.WriteString("type", "export");
            w.WriteNumber("from", from);
            w.WriteNumber("to", to);
            w.WriteStartObject("redaction");
            foreach (var p in Collector.Producers.All.Where(p => p.Kind == "proxy"))
                w.WriteString(p.Src, p.Values ?? "unknown");
            w.WriteEndObject();
            w.WriteEndObject();
        });
        await c.Response.Body.WriteAsync(header).ConfigureAwait(false);
        await c.Response.WriteAsync("\n").ConfigureAwait(false);
        string? cursor = null;
        var written = 0;
        while (written < 100_000)
        {
            var page = Collector.Ops.Query(new OpQuery { From = from, To = to, Limit = 1000, Cursor = cursor });
            foreach (var o in page)
            {
                await c.Response.Body.WriteAsync(Json.Write(w => Json.OpDetail(w, o, null))).ConfigureAwait(false);
                await c.Response.WriteAsync("\n").ConfigureAwait(false);
            }
            written += page.Count;
            if (page.Count < 1000) break;
            cursor = page[^1].Id;
        }
        foreach (var a in Collector.Analysis.Alerts.Snapshot().Where(a => a.LastSeen >= from && a.FirstSeen <= to))
        {
            await c.Response.Body.WriteAsync(Json.Write(w => Json.Alert(w, a))).ConfigureAwait(false);
            await c.Response.WriteAsync("\n").ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync().ConfigureAwait(false);
        await _app.DisposeAsync().ConfigureAwait(false);
        await Collector.DisposeAsync().ConfigureAwait(false);
        (_store as IDisposable)?.Dispose();
    }
}
