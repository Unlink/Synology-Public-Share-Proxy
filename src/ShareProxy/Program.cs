using System.Net;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using ShareProxy;

var builder = WebApplication.CreateBuilder(args);
// Suppress request URLs, bodies and exception messages: share tokens occur in paths.
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o => o.SingleLine = true);
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.None);
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
builder.Services.AddSingleton(new SessionStore(options, dsmOrigin, development));
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
var transfers = new SemaphoreSlim(options.MaxConcurrentTransfers);
app.Lifetime.ApplicationStopped.Register(transfers.Dispose);
var demoUnlocks = new System.Collections.Concurrent.ConcurrentDictionary<string, bool>();

app.Use(async (context, next) =>
{
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
    if (!development) h.StrictTransportSecurity = "max-age=31536000";
    try
    {
        var expected = new Uri(options.PublicOrigin);
        // Bind the public host as well as Origin; no host-derived upstream addresses or redirects.
        if (context.Request.Path != "/healthz" && !string.Equals(context.Request.Host.Value, expected.Authority, StringComparison.OrdinalIgnoreCase))
            throw new ProxyException(400, "request_rejected");
        if (options.DemoMode && (context.Connection.RemoteIpAddress == null || !IPAddress.IsLoopback(context.Connection.RemoteIpAddress)))
            throw new ProxyException(403, "request_rejected");
        if (context.Request.Method == "POST" && context.Request.Headers.Origin.ToString().TrimEnd('/') != options.PublicOrigin.TrimEnd('/'))
            throw new ProxyException(403, "request_rejected");
        await next(context);
    }
    catch (Exception error)
    {
        if (context.Response.HasStarted) { context.Abort(); return; }
        var (status, code) = error switch
        {
            ProxyException p => (p.Status, p.Code),
            JsonException => (502, "protocol_error"),
            BadHttpRequestException b => (b.StatusCode, "request_rejected"),
            OperationCanceledException => (504, "timeout"),
            HttpRequestException => (502, "upstream_unavailable"),
            _ => (502, "protocol_error")
        };
        context.Response.ContentLength = null;
        context.Response.Headers.Remove("Content-Disposition");
        context.Response.StatusCode = status;
        if (!context.RequestAborted.IsCancellationRequested)
            await context.Response.WriteAsJsonAsync(new { code }, context.RequestAborted);
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
    return Results.Json(new { csrf = lease.Browser.Csrf, demo = options.DemoMode });
}).RequireRateLimiting("api");

app.MapGet("/api/shares/{token}", async (string token, HttpContext c, SessionStore store) =>
{
    Input.Token(token);
    using var lease = store.Acquire(c);
    if (options.DemoMode) return Results.Json(Demo.Info(token, demoUnlocks.ContainsKey(lease.Browser.Id), options));
    var gateway = store.Connection(lease.Browser, token);
    if (!await gateway.Gate.WaitAsync(0, c.RequestAborted)) throw new ProxyException(429, "share_busy");
    try { return Results.Json((await gateway.ValidateAsync(c.RequestAborted)).Public); }
    finally { gateway.Gate.Release(); }
}).RequireRateLimiting("api");

app.MapPost("/api/shares/{token}/unlock", async (string token, HttpContext c, SessionStore store) =>
{
    using var lease = store.Acquire(c);
    SessionStore.CheckCsrf(c.Request.Headers["X-Share-CSRF"].ToString(), lease.Browser);
    var payload = JsonSerializer.Deserialize<UnlockRequest>(await SmallBody(c, 8192), new JsonSerializerOptions(JsonSerializerDefaults.Web))
        ?? throw new ProxyException(400, "invalid_password");
    if (payload.Password == null) throw new ProxyException(400, "invalid_password");
    Input.Token(token);
    if (options.DemoMode)
    {
        if (token != "demo-password" || payload.Password != "atelier") throw new ProxyException(401, "invalid_password");
        demoUnlocks[lease.Browser.Id] = true; return Results.NoContent();
    }
    var gateway = store.Connection(lease.Browser, token);
    if (!await gateway.Gate.WaitAsync(0, c.RequestAborted)) throw new ProxyException(429, "share_busy");
    try { await gateway.UnlockAsync(payload.Password, c.RequestAborted); return Results.NoContent(); }
    finally { gateway.Gate.Release(); }
}).RequireRateLimiting("unlock");

app.MapGet("/api/shares/{token}/files", async (string token, string? folder, int? offset, HttpContext c, SessionStore store) =>
{
    using var lease = store.Acquire(c);
    Input.Token(token);
    if (options.DemoMode)
    {
        Demo.Info(token, demoUnlocks.ContainsKey(lease.Browser.Id), options);
        var files = folder == null ? Demo.Files : new[] { new PublicFile("demo_file_photo_0006", "Atelier — fotografia.txt", false, 128, 1791626400) };
        return Results.Json(new FilePage(files, 0, files.Length));
    }
    var gateway = store.Connection(lease.Browser, token);
    if (!await gateway.Gate.WaitAsync(0, c.RequestAborted)) throw new ProxyException(429, "share_busy");
    try { return Results.Json(await gateway.ListAsync(folder, offset ?? 0, c.RequestAborted)); }
    finally { gateway.Gate.Release(); }
}).RequireRateLimiting("api");

app.MapPost("/api/shares/{token}/download", async (string token, HttpContext c, SessionStore store) =>
{
    using var lease = store.Acquire(c);
    if (!c.Request.ContentType?.StartsWith("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase) ?? true)
        throw new ProxyException(415, "request_rejected");
    var form = QueryHelpers.ParseQuery(await SmallBody(c, 16384));
    SessionStore.CheckCsrf(form["csrf"].ToString(), lease.Browser);
    var ids = JsonSerializer.Deserialize<string[]>(form["ids"].ToString()) ?? throw new ProxyException(400, "invalid_selection");
    Input.Token(token);
    if (!await transfers.WaitAsync(0, c.RequestAborted)) throw new ProxyException(429, "server_busy");
    try
    {
        if (options.DemoMode)
        {
            Demo.Info(token, demoUnlocks.ContainsKey(lease.Browser.Id), options);
            if (ids.Length is < 1 or > 100) throw new ProxyException(400, "invalid_selection");
            c.Response.ContentType = "text/plain";
            c.Response.Headers.ContentDisposition = "attachment; filename=demo-download.txt";
            await c.Response.WriteAsync("Lokálny náhľad. Skutočné súbory bude streamovať DSM.", c.RequestAborted); return;
        }
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
        if (options.DemoMode)
        {
            if (Demo.Info(token, false, options).Mode != "upload") throw new ProxyException(403, "upload_not_allowed");
            await TransferIO.CopyAsync(c.Request.Body, Stream.Null, length, options.IdleTimeoutSeconds, c.RequestAborted, true);
            return Results.Json(new { status = "uploaded", demo = true });
        }
        var gateway = store.Connection(lease.Browser, token);
        if (!await gateway.Gate.WaitAsync(0, c.RequestAborted)) throw new ProxyException(429, "share_busy");
        try { await gateway.UploadAsync(c.Request.Body, length, name, uploader, c.RequestAborted); return Results.Json(new { status = "uploaded" }); }
        finally { gateway.Gate.Release(); }
    }
    finally { transfers.Release(); }
}).RequireRateLimiting("api");
app.MapFallback(() => Results.Json(new { code = "not_found" }, statusCode: 404));
app.Run();

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
