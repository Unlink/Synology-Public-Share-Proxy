using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using ShareProxy;

var builder = WebApplication.CreateBuilder(args);
// Only our safe diagnostic category is allowed, irrespective of Logging configuration.
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.IncludeScopes = false; o.UseUtcTimestamp = true; o.TimestampFormat = "yyyy-MM-dd HH:mm:ss'Z' "; });
builder.Logging.AddDebug();
builder.Services.PostConfigure<LoggerFilterOptions>(ProxyDiagnostics.ConfigureFilters);
var options = builder.Configuration.GetSection("Proxy").Get<ProxyOptions>() ?? new();
var development = builder.Environment.IsDevelopment();
var dsmOrigin = options.Validate(development);
builder.WebHost.ConfigureKestrel(k =>
{
    k.AddServerHeader = false;
    k.Limits.MaxRequestBodySize = options.MaxUploadBytes;
    k.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(15);
    k.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(60);
    k.Limits.MaxConcurrentConnections = 128;
});
builder.Services.AddSingleton(options);
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<ProxyDiagnostics>();
builder.Services.AddSingleton(s => new SessionStore(options, dsmOrigin, development, s.GetRequiredService<ProxyDiagnostics>()));
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    // Native remote IP only: forwarded headers from arbitrary clients are deliberately ignored.
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(c => RateLimitPartition.GetFixedWindowLimiter(
        c.Request.Path.StartsWithSegments("/api") ? "api-global" : "page-global", _ => new()
        { PermitLimit = 600, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    o.AddPolicy("api", c => RateLimitPartition.GetFixedWindowLimiter(c.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new()
        { PermitLimit = 90, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    o.AddPolicy("unlock", c => RateLimitPartition.GetFixedWindowLimiter(c.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new()
        { PermitLimit = 6, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    o.OnRejected = async (context, ct) =>
    {
        context.HttpContext.Response.Headers.RetryAfter = "60";
        await context.HttpContext.Response.WriteAsJsonAsync(new { code = "rate_limited" }, ct);
    };
});
var app = builder.Build();
var diagnostics = app.Services.GetRequiredService<ProxyDiagnostics>();
app.Lifetime.ApplicationStarted.Register(diagnostics.Started);
var transfers = new SemaphoreSlim(options.MaxConcurrentTransfers);
app.Lifetime.ApplicationStopped.Register(transfers.Dispose);

app.Use(async (context, next) =>
{
    var trace = diagnostics.Begin(context);
    var h = context.Response.Headers;
    var downloadFrame = context.GetEndpoint()?.Metadata.GetMetadata<DownloadFramePolicy>() != null;
    h.ContentSecurityPolicy = "default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; form-action 'self'; frame-src 'self'; frame-ancestors "
        + (downloadFrame ? "'self'" : "'none'") + "; base-uri 'none'; object-src 'none'";
    h.XContentTypeOptions = "nosniff"; h.XFrameOptions = downloadFrame ? "SAMEORIGIN" : "DENY";
    // Origin-only referrers keep share tokens private and preserve the native POST form's Origin header.
    h["Referrer-Policy"] = "strict-origin";
    h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
    h["X-Robots-Tag"] = "noindex, nofollow, noarchive";
    h["Cross-Origin-Resource-Policy"] = "same-origin";
    h.CacheControl = "no-store";
    if (!development && new Uri(options.PublicOrigin).Scheme == "https") h.StrictTransportSecurity = "max-age=31536000";
    try
    {
        var expected = new Uri(options.PublicOrigin);
        // Bind the public host as well as Origin; no host-derived upstream addresses or redirects.
        if (context.Request.Path != "/healthz" && !string.Equals(context.Request.Host.Value, expected.Authority, StringComparison.OrdinalIgnoreCase))
            throw new ProxyException(400, "request_rejected");
        if (context.Request.Method == "POST" && context.Request.Headers.Origin.ToString().TrimEnd('/') != options.PublicOrigin.TrimEnd('/'))
            throw new ProxyException(403, "request_rejected");
        await next(context);
        if (context.Response.StatusCode == 429)
            diagnostics.Failed(trace, new(429, "rate_limited", "rate_limited"), false);
        else diagnostics.Completed(trace, context.Response.StatusCode);
    }
    catch (Exception error)
    {
        var failure = ProxyDiagnostics.Classify(error, context.RequestAborted.IsCancellationRequested);
        diagnostics.Failed(trace, failure, context.Response.HasStarted);
        if (context.Response.HasStarted) { context.Abort(); return; }
        context.Response.ContentLength = null;
        context.Response.Headers.Remove("Content-Disposition");
        context.Response.StatusCode = failure.Status;
        if (!context.RequestAborted.IsCancellationRequested)
            await context.Response.WriteAsJsonAsync(new { code = failure.Code, requestId = trace.Id }, context.RequestAborted);
    }
});
app.UseRateLimiter();
// Map only our three known static assets. No general static-file or SPA fallback exposes unknown routes.
app.MapGet("/app.css", () => Results.File(Path.Combine(app.Environment.WebRootPath, "app.css"), "text/css"));
app.MapGet("/app.js", () => Results.File(Path.Combine(app.Environment.WebRootPath, "app.js"), "text/javascript"));
app.MapGet("/icon.svg", () => Results.File(Path.Combine(app.Environment.WebRootPath, "icon.svg"), "image/svg+xml"));
IResult Index() => Results.File(Path.Combine(app.Environment.WebRootPath, "index.html"), "text/html");
app.MapGet("/", Index);
app.MapGet("/sharing/{token}", (string token) => { Input.Token(token); return Index(); });
app.MapGet("/s/{token}", (string token) => { Input.Token(token); return Index(); });
app.MapGet("/healthz", () => Results.Json(new { status = "ok" }));

app.MapGet("/api/session", (HttpContext c, SessionStore store) =>
{
    using var lease = store.Acquire(c);
    return Results.Json(new { csrf = lease.Browser.Csrf });
}).RequireRateLimiting("api");

app.MapGet("/api/shares/{token}", async (string token, HttpContext c, SessionStore store) =>
{
    Input.Token(token);
    using var lease = store.Acquire(c);
    var gateway = store.Connection(lease.Browser, token);
    if (!await gateway.Gate.WaitAsync(0, c.RequestAborted)) throw new ProxyException(429, "share_busy");
    try { return Results.Json(await gateway.ReadPublicInfoAsync(c.RequestAborted)); }
    finally { gateway.Gate.Release(); }
}).RequireRateLimiting("api");

app.MapPost("/api/shares/{token}/unlock", async (string token, HttpContext c, SessionStore store) =>
{
    using var lease = store.Acquire(c);
    SessionStore.CheckCsrf(c.Request.Headers["X-Share-CSRF"].ToString(), lease.Browser);
    if (!string.Equals(c.Request.ContentType?.Split(';')[0].Trim(), "application/json", StringComparison.OrdinalIgnoreCase))
        throw new ProxyException(415, "request_rejected");
    var password = ReadPassword(await SmallBody(c, 8192));
    Input.Token(token);
    var gateway = store.Connection(lease.Browser, token);
    if (!await gateway.Gate.WaitAsync(0, c.RequestAborted)) throw new ProxyException(429, "share_busy");
    try { await gateway.UnlockAsync(password, c.RequestAborted); return Results.NoContent(); }
    finally { gateway.Gate.Release(); }
}).RequireRateLimiting("unlock");

app.MapGet("/api/shares/{token}/files", async (string token, string? folder, int? offset, HttpContext c, SessionStore store) =>
{
    using var lease = store.Acquire(c);
    Input.Token(token);
    var gateway = store.Connection(lease.Browser, token);
    if (!await gateway.Gate.WaitAsync(0, c.RequestAborted)) throw new ProxyException(429, "share_busy");
    try { return Results.Json(await gateway.ListAsync(folder, offset ?? 0, c.RequestAborted)); }
    finally { gateway.Gate.Release(); }
}).RequireRateLimiting("api");

app.MapPost("/api/shares/{token}/download", async (string token, HttpContext c, SessionStore store) =>
{
    using var lease = store.Acquire(c);
    if (!string.Equals(c.Request.ContentType?.Split(';')[0].Trim(), "application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
        throw new ProxyException(415, "request_rejected");
    var form = QueryHelpers.ParseQuery(await SmallBody(c, 16384));
    if (form.Any(p => p.Value.Count != 1 || p.Key is not ("csrf" or "ids" or "receipt")) ||
        !form.TryGetValue("csrf", out var csrf) || !form.TryGetValue("ids", out var selection))
        throw new ProxyException(400, "request_rejected");
    SessionStore.CheckCsrf(csrf.ToString(), lease.Browser);
    string[] ids;
    try { ids = JsonSerializer.Deserialize<string[]>(selection.ToString()) ?? throw new ProxyException(400, "invalid_selection"); }
    catch (JsonException) { throw new ProxyException(400, "invalid_selection"); }
    if (ids.Length is < 1 or > 100 || ids.Any(id => !Input.Id(id)) || ids.Distinct().Count() != ids.Length)
        throw new ProxyException(400, "invalid_selection");
    var receipt = form.TryGetValue("receipt", out var receiptValue) ? receiptValue.ToString() : "";
    if (receipt.Length > 0 && (receipt.Length != 32 || receipt.Any(c => !char.IsAsciiHexDigit(c) || char.IsUpper(c))))
        throw new ProxyException(400, "request_rejected");
    if (receipt.Length > 0) c.Response.OnStarting(() =>
    {
        // One-bit, non-authentication marker for native download readiness. Never includes file/token data.
        // Error responses have their attachment header removed and do not create this cookie.
        if (c.Response.StatusCode == 200 && c.Response.Headers.ContentDisposition.ToString().StartsWith("attachment", StringComparison.OrdinalIgnoreCase))
            c.Response.Cookies.Append("download-ready-" + receipt, "1", new CookieOptions
            { HttpOnly = false, Secure = new Uri(options.PublicOrigin).Scheme == "https", SameSite = SameSiteMode.Strict, Path = "/", MaxAge = TimeSpan.FromMinutes(2) });
        return Task.CompletedTask;
    });
    Input.Token(token);
    if (!await transfers.WaitAsync(0, c.RequestAborted)) throw new ProxyException(429, "server_busy");
    try
    {
        var gateway = store.Connection(lease.Browser, token);
        if (!await gateway.Gate.WaitAsync(0, c.RequestAborted)) throw new ProxyException(429, "share_busy");
        try { await gateway.DownloadAsync(ids, c.Response, c.RequestAborted); }
        finally { gateway.Gate.Release(); }
    }
    finally { transfers.Release(); }
}).WithMetadata(new DownloadFramePolicy()).RequireRateLimiting("api");

app.MapPost("/api/shares/{token}/upload", async (string token, HttpContext c, SessionStore store) =>
{
    using var lease = store.Acquire(c);
    SessionStore.CheckCsrf(c.Request.Headers["X-Share-CSRF"].ToString(), lease.Browser);
    var length = c.Request.ContentLength ?? throw new ProxyException(411, "length_required");
    if (length > options.MaxUploadBytes) throw new ProxyException(413, "transfer_too_large");
    if (c.Request.ContentType != "application/octet-stream") throw new ProxyException(415, "request_rejected");
    var name = Input.Filename(Uri.UnescapeDataString(c.Request.Headers["X-File-Name"].ToString()));
    var uploader = Input.Uploader(Uri.UnescapeDataString(c.Request.Headers["X-Uploader-Name"].ToString()));
    Input.Token(token);
    if (!await transfers.WaitAsync(0, c.RequestAborted)) throw new ProxyException(429, "server_busy");
    try
    {
        var gateway = store.Connection(lease.Browser, token);
        if (!await gateway.Gate.WaitAsync(0, c.RequestAborted)) throw new ProxyException(429, "share_busy");
        try { await gateway.UploadAsync(c.Request.Body, length, name, uploader, c.RequestAborted); return Results.Json(new { status = "uploaded" }); }
        finally { gateway.Gate.Release(); }
    }
    finally { transfers.Release(); }
}).RequireRateLimiting("api");
app.MapFallback(() => Results.Json(new { code = "not_found" }, statusCode: 404));
app.Run();

static string ReadPassword(string body)
{
    try
    {
        using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 4 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 1 ||
            !root.TryGetProperty("password", out var value) || value.ValueKind != JsonValueKind.String)
            throw new ProxyException(400, "invalid_password");
        return value.GetString()!;
    }
    catch (JsonException) { throw new ProxyException(400, "invalid_password"); }
}

static async Task<string> SmallBody(HttpContext context, int max)
{
    var feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
    if (feature is { IsReadOnly: false }) feature.MaxRequestBodySize = max;
    if (context.Request.ContentLength > max) throw new ProxyException(413, "request_too_large");
    using var buffer = new MemoryStream();
    await TransferIO.CopyAsync(context.Request.Body, buffer, max, 20, context.RequestAborted);
    return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
}

public partial class Program;
internal sealed record DownloadFramePolicy;
