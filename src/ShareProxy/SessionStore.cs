using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace ShareProxy;

public sealed class SessionStore(ProxyOptions options, Uri dsmOrigin, bool development, ProxyDiagnostics? diagnostics = null) : IDisposable
{
    private readonly object sync = new();
    private readonly Dictionary<string, BrowserSession> sessions = new(StringComparer.Ordinal);
    private readonly bool secureCookies = SecureCookies(options, development);
    private readonly string cookieName = SecureCookies(options, development) ? "__Host-ShareSession" : "share-dev";
    private static bool SecureCookies(ProxyOptions settings, bool dev) =>
        (Uri.TryCreate(settings.PublicOrigin, UriKind.Absolute, out var publicUri) && publicUri.Scheme == "https");
    public sealed class BrowserSession
    {
        public string Id { get; } = DsmGateway.NewId();
        public string Csrf { get; } = DsmGateway.NewId();
        public DateTimeOffset Expires { get; init; }
        public int Active { get; set; }
        public Dictionary<string, DsmGateway> Connections { get; } = new(StringComparer.Ordinal);
    }
    public sealed class Lease(SessionStore store, BrowserSession browser) : IDisposable
    {
        public BrowserSession Browser { get; } = browser;
        public void Dispose() { lock (store.sync) { Browser.Active--; } }
    }
    public Lease Acquire(HttpContext context)
    {
        lock (sync)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var key in sessions.Where(x => x.Value.Active == 0 && x.Value.Expires <= now).Select(x => x.Key).ToArray())
            {
                foreach (var connection in sessions[key].Connections.Values) connection.Dispose();
                sessions.Remove(key);
            }
            var id = context.Request.Cookies[cookieName];
            if (id == null || !sessions.TryGetValue(id, out var browser) || browser.Expires <= now)
            {
                if (sessions.Count >= options.MaxSessions) throw new ProxyException(429, "server_busy");
                browser = new BrowserSession { Expires = now.AddMinutes(options.SessionMinutes) };
                sessions.Add(browser.Id, browser);
                context.Response.Cookies.Append(cookieName, browser.Id, new CookieOptions
                {
                    Secure = secureCookies, HttpOnly = true, SameSite = SameSiteMode.Strict, Path = "/", Expires = browser.Expires, IsEssential = true
                });
            }
            browser.Active++;
            return new Lease(this, browser);
        }
    }
    public DsmGateway Connection(BrowserSession browser, string token)
    {
        Input.Token(token);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
        lock (sync)
        {
            if (browser.Connections.TryGetValue(key, out var gateway)) return gateway;
            if (browser.Connections.Count >= 8) throw new ProxyException(429, "too_many_shares");
            if (sessions.Values.Sum(s => s.Connections.Count) >= options.MaxSessions) throw new ProxyException(429, "server_busy");
            // No system HTTP proxy, ambient credentials, redirect following or TLS validation bypass.
            var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false, UseProxy = false, UseCookies = true,
                CookieContainer = new CookieContainer(64, 32, 4096), Credentials = null,
                ConnectTimeout = TimeSpan.FromSeconds(10), PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                MaxConnectionsPerServer = 2, AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            };
            var certificate = diagnostics == null ? null : new DsmCertificateObserver();
            if (certificate != null)
                handler.SslOptions.RemoteCertificateValidationCallback = (_, _, chain, errors) => certificate.Validate(chain, errors);
            var client = new HttpClient(handler) { BaseAddress = dsmOrigin, Timeout = Timeout.InfiniteTimeSpan };
            gateway = new DsmGateway(client, options, token, diagnostics, certificate);
            browser.Connections.Add(key, gateway);
            return gateway;
        }
    }
    public static void CheckCsrf(string supplied, BrowserSession browser)
    {
        if (supplied.Length != browser.Csrf.Length || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(browser.Csrf)))
            throw new ProxyException(403, "request_rejected");
    }
    public void Dispose()
    {
        lock (sync) foreach (var browser in sessions.Values) foreach (var gateway in browser.Connections.Values) gateway.Dispose();
    }
}
