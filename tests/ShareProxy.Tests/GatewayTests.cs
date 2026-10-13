using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using ShareProxy;
using Xunit;

namespace ShareProxy.Tests;

public sealed class GatewayTests
{
    private const string Token = "syntheticShare123";
    private static readonly ProxyOptions Options = new() { DsmOrigin = "https://nas.example:5001", PublicOrigin = "https://share.example", MaxUploadBytes = 128, MaxDownloadBytes = 256 };
    private static string Bootstrap(string status = "none", string? token = null) =>
        "<html><script src=\"webapi/entry.cgi?api=SYNO.Core.Sharing.Session&amp;sharing_id=&quot;" + (token ?? Token) + "&quot;&amp;sharing_status=&quot;" + status + "&quot;\"></script></html>";
    private static string Session(bool upload = false, bool folder = true, int status = 0) =>
        "SYNO.SDS.Session = " + JsonSerializer.Serialize(new { sharing = true, sharing_id = Token, isLogined = false }) + ";SYNO.SDS.ExtraSession = " +
        JsonSerializer.Serialize(new { status, filename = "Project", is_folder = folder, is_sharing_upload = upload }) + ";";
    private static string Init(bool upload = false, bool folder = true, bool enabled = true, string protect = "none", bool matchIp = false) =>
        JsonSerializer.Serialize(new { success = true, data = new { Sharing = new { enabled, protect_type = protect, redirect_type = "none", enable_match_ip = matchIp, expire_times = 0,
            app = new { enable_upload = upload, is_folder = folder, request_name = "Upload", request_info = "Hello" }, expire_at = "" }, Private = new { filename = "Project" } } });
    private static HttpResponseMessage Response(string body, string type = "application/json", HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(body, Encoding.UTF8, type) };
    private sealed class Mock(Func<HttpRequestMessage, string, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public List<(string Path, string Body)> Calls { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(ct);
            Calls.Add((request.RequestUri!.AbsolutePath, body));
            return await respond(request, body);
        }
    }
    private static DsmGateway Gateway(Mock mock) => new(new HttpClient(mock) { BaseAddress = new Uri(Options.DsmOrigin), Timeout = Timeout.InfiniteTimeSpan }, Options, Token);
    private static HttpResponseMessage Standard(HttpRequestMessage request, bool upload = false, bool folder = true, string protect = "none")
    {
        if (request.RequestUri!.AbsolutePath == "/sharing/" + Token) return Response(Bootstrap(protect == "password" ? "password" : "none"), "text/html");
        if (request.Method == HttpMethod.Get) return Response(Session(upload, folder), "application/javascript");
        return Response(Init(upload, folder, protect: protect));
    }

    [Theory]
    [InlineData("../../admin")][InlineData("http://nas.example")][InlineData("abcd")][InlineData("abc%2F123")]
    [InlineData("syntheticToken123\n")]
    public void RejectsMalformedTokens(string token) => Assert.Throws<ProxyException>(() => Input.Token(token));
    [Theory]
    [InlineData(null)]
    [InlineData("syntheticFileId123\n")]
    [InlineData("../syntheticFileId123")]
    public void RejectsMalformedFileIds(string? id) => Assert.False(Input.Id(id));

    [Fact] public void HttpsSessionRemainsSecureInDevelopment()
    {
        using var store = new SessionStore(Options, new Uri(Options.DsmOrigin), true);
        var context = new DefaultHttpContext();
        using var lease = store.Acquire(context);
        var cookie = context.Response.Headers.SetCookie.ToString();
        Assert.StartsWith("__Host-ShareSession=", cookie);
        Assert.Contains("secure", cookie);
        Assert.Contains("httponly", cookie);
    }

    [Fact] public void HttpSessionWorksInProduction()
    {
        var settings = new ProxyOptions { PublicOrigin = "http://share.example" };
        using var store = new SessionStore(settings, new Uri(Options.DsmOrigin), false);
        var context = new DefaultHttpContext();
        using var lease = store.Acquire(context);
        var cookie = context.Response.Headers.SetCookie.ToString();
        Assert.StartsWith("share-dev=", cookie);
        Assert.DoesNotContain("secure", cookie);
        Assert.Contains("httponly", cookie);
        Assert.Contains("samesite=strict", cookie);
    }

    [Fact] public async Task MetadataConcurrencyIsBoundedAndSlotsRecoverAfterCompletion()
    {
        var allStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        var gateways = Enumerable.Range(0, 9).Select(_ => Gateway(new Mock(async (request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/sharing/" + Token)
            {
                if (Interlocked.Increment(ref count) == 8) allStarted.TrySetResult();
                await release.Task;
            }
            return Standard(request);
        }))).ToArray();
        var pending = gateways.Take(8).Select(g => g.ValidateAsync(default)).ToArray();
        try
        {
            await allStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var error = await Assert.ThrowsAsync<ProxyException>(() => gateways[8].ValidateAsync(default));
            Assert.Equal(429, error.Status);
            Assert.Equal("server_busy", error.Code);
            release.TrySetResult();
            await Task.WhenAll(pending);
            Assert.Equal("download", (await gateways[8].ValidateAsync(default)).Public.Mode);
        }
        finally
        {
            release.TrySetResult();
            await Task.WhenAll(pending);
            foreach (var gateway in gateways) gateway.Dispose();
        }
    }
    [Theory]
    [InlineData("../private.txt")][InlineData("x\\private.txt")][InlineData("x:stream")][InlineData("file\r\nHeader")][InlineData("..")] [InlineData("unsafe.")]
    public void RejectsUnsafeFilenames(string filename) => Assert.Throws<ProxyException>(() => Input.Filename(filename));
    [Fact] public void RejectsUnsupportedSchemesAndCredentialOrigins()
    {
        Assert.Throws<InvalidOperationException>(() => new ProxyOptions { DsmOrigin = "http://nas.example:5000/sharing/token", PublicOrigin = Options.PublicOrigin }.Validate(false));
        Assert.Throws<InvalidOperationException>(() => new ProxyOptions { DsmOrigin = "ftp://nas", PublicOrigin = Options.PublicOrigin }.Validate(false));
        Assert.Throws<InvalidOperationException>(() => new ProxyOptions { DsmOrigin = "https://user:password@nas", PublicOrigin = Options.PublicOrigin }.Validate(false));
        Assert.Throws<InvalidOperationException>(() => new ProxyOptions { DsmOrigin = "https://nas/admin", PublicOrigin = Options.PublicOrigin }.Validate(false));
    }
    [Theory]
    [InlineData("http://localhost:5000")]
    [InlineData("http://host.docker.internal:5000")]
    [InlineData("http://HOST.DOCKER.INTERNAL:5000")]
    [InlineData("http://127.0.0.1:5000")]
    [InlineData("http://192.168.1.20:5000")]
    [InlineData("http://10.1.0.2:5000")]
    [InlineData("http://172.16.1.20:5000")]
    [InlineData("http://100.64.0.1:5000")]
    [InlineData("http://[::1]:5000")]
    [InlineData("http://[fd00::1234]:5000")]
    public void SupportsExplicitlyConfiguredLocalHttpDsmInProduction(string origin)
        => Assert.Equal("http", new ProxyOptions { DsmOrigin = origin, PublicOrigin = Options.PublicOrigin }.Validate(false).Scheme);
    [Theory]
    [InlineData("http://8.8.8.8")]
    [InlineData("http://host.docker.internal.example:5000")]
    [InlineData("http://docker.host.internal:5000")]
    [InlineData("http://nas.example:5000")]
    [InlineData("http://172.32.0.1")]
    [InlineData("http://100.128.0.1")]
    [InlineData("http://[2001:4860:4860::8888]")]
    public void SupportsConfiguredPublicHttpAddresses(string origin)
        => Assert.Equal("http", new ProxyOptions { DsmOrigin = origin, PublicOrigin = origin }.Validate(false).Scheme);
    [Fact] public void ParsesJsonAssignmentsWithoutExecutingScript()
    {
        var (s, e) = DsmGateway.ParseSession(Session()); using (s) using (e) Assert.Equal("Project", e.RootElement.GetProperty("filename").GetString());
        Assert.Throws<ProxyException>(() => DsmGateway.ParseSession(Session() + "fetch('/admin')"));
        Assert.Throws<ProxyException>(() => DsmGateway.ParseSession("eval('anything')"));
    }
    [Fact] public void RejectsBootstrapForAnotherShare() => Assert.Throws<ProxyException>(() => DsmGateway.BootstrapStatus(Bootstrap(token: "otherSynthetic123"), Token));
    [Fact] public void IgnoresVersionedAssetsBeforeBootstrapSessionScript()
    {
        var html = "<script src=\"/scripts/jquery.js?v=123\"></script><script src=\"/other.js?module=share\"></script>" + Bootstrap();
        Assert.Equal("none", DsmGateway.BootstrapStatus(html, Token));
    }
    [Theory]
    [InlineData("api=SYNO.Core.Sharing.Session")]
    [InlineData("api=SYNO.Core.Sharing.Session&sharing_id=%22sampleShare123%22")]
    [InlineData("api=SYNO.Core.Sharing.Session&sharing_id=%22sampleShare123%22&sharing_id=%22sampleShare123%22&sharing_status=%22none%22")]
    public void MissingOrDuplicateBootstrapFieldsFailWithProtocolError(string query)
    {
        var error = Assert.Throws<ProxyException>(() => DsmGateway.BootstrapStatus("<script src=\"/sharing/webapi/entry.cgi?" + query + "\"></script>", Token));
        Assert.Equal("protocol_error", error.Code);
    }
    [Fact] public async Task PasswordShareCannotReachMetadataBeforeUnlock()
    {
        var mock = new Mock((r, _) => Task.FromResult(Response(Bootstrap("password"), "text/html")));
        using var gateway = Gateway(mock);
        var error = await Assert.ThrowsAsync<ProxyException>(() => gateway.ValidateAsync(default));
        Assert.Equal(401, error.Status); Assert.Single(mock.Calls);
    }
    [Fact] public async Task AccountProtectedShareIsDenied()
    {
        var mock = new Mock((r, _) => Task.FromResult(Response(Bootstrap("user"), "text/html")));
        using var gateway = Gateway(mock);
        Assert.Equal("account_share_unsupported", (await Assert.ThrowsAsync<ProxyException>(() => gateway.ValidateAsync(default))).Code);
    }
    [Fact] public async Task ExpiredShareDeniedBySynology()
    {
        var mock = new Mock((r, _) => Task.FromResult(r.Method == HttpMethod.Post ? Response(Init(enabled: false)) : Standard(r)));
        using var gateway = Gateway(mock);
        Assert.Equal(410, (await Assert.ThrowsAsync<ProxyException>(() => gateway.ValidateAsync(default))).Status);
    }
    [Fact] public async Task OrdinaryPublicShareWithCapturedMatchIpFlagIsAccepted()
    {
        var mock = new Mock((r, _) => Task.FromResult(r.Method == HttpMethod.Post ? Response(Init(matchIp: true)) : Standard(r)));
        using var gateway = Gateway(mock);
        Assert.Equal("download", (await gateway.ValidateAsync(default)).Public.Mode);
    }
    [Theory]
    [InlineData("enable_upload")][InlineData("is_folder")]
    public async Task MissingModeFlagsCannotDefaultToDownload(string flag)
    {
        var invalid = Init().Replace("\"" + flag + "\":false,", "").Replace("\"" + flag + "\":true,", "");
        var mock = new Mock((r, _) => Task.FromResult(r.Method == HttpMethod.Post ? Response(invalid) : Standard(r)));
        using var gateway = Gateway(mock);
        Assert.Equal("protocol_error", (await Assert.ThrowsAsync<ProxyException>(() => gateway.ValidateAsync(default))).Code);
    }
    [Fact] public void ConnectionBudgetIsSharedAcrossBrowsers()
    {
        using var store = new SessionStore(new ProxyOptions { MaxSessions = 2 }, new Uri(Options.DsmOrigin), true);
        using var one = store.Acquire(new DefaultHttpContext());
        using var two = store.Acquire(new DefaultHttpContext());
        var first = store.Connection(one.Browser, Token);
        store.Connection(two.Browser, Token);
        Assert.Same(first, store.Connection(one.Browser, Token));
        Assert.Equal("server_busy", Assert.Throws<ProxyException>(() => store.Connection(one.Browser, "differentSynthetic123")).Code);
    }
    [Fact] public async Task RedirectNeverFollowed()
    {
        var mock = new Mock((r, _) => { var response = Response("", status: HttpStatusCode.Redirect); response.Headers.Location = new Uri("http://169.254.169.254/latest/meta-data"); return Task.FromResult(response); });
        using var gateway = Gateway(mock);
        Assert.Equal("upstream_redirect_rejected", (await Assert.ThrowsAsync<ProxyException>(() => gateway.ValidateAsync(default))).Code);
        Assert.Single(mock.Calls);
    }
    [Fact] public async Task ListIsScopedAndDoesNotExposeDsmPathsOrOwners()
    {
        var mock = new Mock((r, body) => Task.FromResult(body.Contains("SYNO.FolderSharing.List") ? Response("{\"success\":true,\"data\":{\"total\":1,\"files\":[{\"path\":\"/Project/one.txt\",\"name\":\"one.txt\",\"isdir\":false,\"additional\":{\"size\":12,\"owner\":{\"user\":\"private-owner\"},\"time\":{\"mtime\":1}}}]}}") : Standard(r)));
        using var gateway = Gateway(mock);
        var page = await gateway.ListAsync(null, 0, default);
        Assert.Single(page.Files); Assert.True(Input.Id(page.Files[0].Id));
        var json = JsonSerializer.Serialize(page); Assert.DoesNotContain("/Project", json); Assert.DoesNotContain("private-owner", json);
        var p = QueryHelpers.ParseQuery(mock.Calls.Last().Body); Assert.Equal("\"/Project\"", p["folder_path"].ToString());
    }
    [Fact] public async Task MaliciousUpstreamPathRejected()
    {
        var mock = new Mock((r, body) => Task.FromResult(body.Contains("SYNO.FolderSharing.List") ? Response("{\"success\":true,\"data\":{\"total\":1,\"files\":[{\"path\":\"/private/secret\",\"name\":\"secret\",\"isdir\":false}]}}") : Standard(r)));
        using var gateway = Gateway(mock);
        Assert.Equal("protocol_error", (await Assert.ThrowsAsync<ProxyException>(() => gateway.ListAsync(null, 0, default))).Code);
    }
    [Fact] public async Task ForgedFileIdCannotTriggerDownload()
    {
        var mock = new Mock((r, _) => Task.FromResult(Standard(r))); using var gateway = Gateway(mock);
        var context = new DefaultHttpContext(); context.Response.Body = new MemoryStream();
        Assert.Equal(404, (await Assert.ThrowsAsync<ProxyException>(() => gateway.DownloadAsync(["forgedFileId12345678"], context.Response, default))).Status);
        Assert.DoesNotContain(mock.Calls, c => c.Path.Contains("fsdownload"));
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ZipDownloadUsesOnlyEnumeratedPathsAndStreamsAttachment(bool directory)
    {
        var mock = new Mock((r, body) =>
        {
            if (r.RequestUri!.AbsolutePath.Contains("fsdownload"))
            {
                Assert.Equal(HttpMethod.Post, r.Method);
                Assert.Equal(directory ? "[\"/Project/one.txt\"]" : "[\"/Project/one.txt\",\"/Project/two.txt\"]", QueryHelpers.ParseQuery(body)["path"].ToString());
                var response = Response("zip-bytes", "application/octet-stream");
                response.Content.Headers.ContentDisposition = new("attachment"); return Task.FromResult(response);
            }
            var files = directory
                ? new[] { new { path = "/Project/one.txt", name = "one.txt", isdir = true } }
                : new[] { new { path = "/Project/one.txt", name = "one.txt", isdir = false }, new { path = "/Project/two.txt", name = "two.txt", isdir = false } };
            return Task.FromResult(body.Contains("SYNO.FolderSharing.List") ? Response(JsonSerializer.Serialize(new { success = true, data = new { total = files.Length, files } })) : Standard(r));
        });
        using var gateway = Gateway(mock); var page = await gateway.ListAsync(null, 0, default);
        var context = new DefaultHttpContext(); context.Response.Body = new MemoryStream();
        await gateway.DownloadAsync(page.Files.Select(f => f.Id).ToArray(), context.Response, default);
        Assert.Equal("zip-bytes", Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray()));
        Assert.Contains("shared-files.zip", context.Response.Headers.ContentDisposition.ToString());
    }
    [Theory]
    [InlineData("one.txt")]
    [InlineData("žltý súbor.txt")]
    public async Task OneFileFromFolderUsesDirectDownloadWithServerOwnedUtf8Path(string filename)
    {
        var path = "/Project/Subfolder/" + filename;
        var mock = new Mock((request, body) =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("fsdownload"))
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                Assert.Null(request.Content);
                var query = QueryHelpers.ParseQuery(request.RequestUri.Query);
                var hex = JsonSerializer.Deserialize<string>(query["dlink"].ToString())!;
                Assert.Equal(path, Encoding.UTF8.GetString(Convert.FromHexString(hex)));
                Assert.Equal(JsonSerializer.Serialize(Token), query["_sharing_id"].ToString());
                Assert.Equal("download", query["mode"].ToString());
                Assert.Equal("SYNO.FolderSharing.Download", query["api"].ToString());
                return Task.FromResult(Response("raw-file-bytes", "application/octet-stream"));
            }
            if (body.Contains("SYNO.FolderSharing.List"))
            {
                var folder = QueryHelpers.ParseQuery(body)["folder_path"].ToString() == "\"/Project\"";
                return Task.FromResult(Response(JsonSerializer.Serialize(new { success = true, data = new {
                    total = 1, files = new[] { new { path = folder ? "/Project/Subfolder" : path,
                        name = folder ? "Subfolder" : filename, isdir = folder } } } })));
            }
            return Task.FromResult(Standard(request));
        });
        using var gateway = Gateway(mock);
        var folder = (await gateway.ListAsync(null, 0, default)).Files.Single();
        var file = (await gateway.ListAsync(folder.Id, 0, default)).Files.Single();
        var context = new DefaultHttpContext(); context.Response.Body = new MemoryStream();
        await gateway.DownloadAsync([file.Id], context.Response, default);
        Assert.Equal("raw-file-bytes", Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray()));
        Assert.DoesNotContain("shared-files.zip", context.Response.Headers.ContentDisposition.ToString());
    }
    [Fact] public async Task DownloadRevalidatesRevocationAfterListing()
    {
        var revoked = false;
        var mock = new Mock((r, body) => Task.FromResult(body.Contains("SYNO.FolderSharing.List") ? Response("{\"success\":true,\"data\":{\"total\":1,\"files\":[{\"path\":\"/Project/one.txt\",\"name\":\"one.txt\",\"isdir\":false}]}}") : body.Contains("SYNO.Core.Sharing.Initdata") && revoked ? Response(Init(enabled: false)) : Standard(r)));
        using var gateway = Gateway(mock); var file = (await gateway.ListAsync(null, 0, default)).Files.Single(); revoked = true;
        Assert.Equal(410, (await Assert.ThrowsAsync<ProxyException>(() => gateway.DownloadAsync([file.Id], new DefaultHttpContext().Response, default))).Status);
        Assert.DoesNotContain(mock.Calls, c => c.Path.Contains("fsdownload"));
    }
    [Fact] public async Task UploadChecksPermissionAndPlacesBinaryLastWithoutBufferingToDisk()
    {
        var mock = new Mock((r, body) =>
        {
            if (body.Contains("SYNO.FileStation.CheckPermission"))
            { Assert.Equal("false", QueryHelpers.ParseQuery(body)["overwrite"].ToString()); return Task.FromResult(Response("{\"success\":true,\"data\":{}}")); }
            if (r.RequestUri!.AbsolutePath == "/webapi/entry.cgi")
            {
                Assert.Contains("_sharing_id", r.RequestUri.Query); Assert.Contains("binary-payload", body);
                Assert.True(body.IndexOf("name=\"file\"", StringComparison.Ordinal) > body.IndexOf("name=\"size\"", StringComparison.Ordinal));
                Assert.DoesNotContain("dest_folder_path", body); return Task.FromResult(Response("{\"success\":true}"));
            }
            return Task.FromResult(Standard(r, upload: true));
        });
        using var gateway = Gateway(mock); var bytes = Encoding.UTF8.GetBytes("binary-payload");
        await gateway.UploadAsync(new MemoryStream(bytes), bytes.Length, "one.txt", "Visitor", default);
        Assert.Contains(mock.Calls, c => c.Body.Contains("SYNO.FileStation.CheckPermission"));
    }
    [Theory]
    [InlineData("one.txt", "binary-payload")]
    [InlineData("Žiadosť – súbor.txt", "")]
    public async Task UploadMatchesCapturedBrowserMultipartWireFormat(string filename, string payload)
    {
        var started = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var checkedUpload = false;
        var mock = new Mock(async (r, body) =>
        {
            if (body.Contains("SYNO.FileStation.CheckPermission")) return Response("{\"success\":true,\"data\":{}}");
            if (r.RequestUri!.AbsolutePath != "/webapi/entry.cgi") return Standard(r, upload: true);
            checkedUpload = true;
            Assert.False(r.Headers.Contains("X-SYNO-SHARING"));
            Assert.Equal(new Uri(Options.DsmOrigin).GetLeftPart(UriPartial.Authority), Assert.Single(r.Headers.GetValues("Origin")));
            Assert.Equal(new Uri(new Uri(Options.DsmOrigin), "/sharing/" + Token), r.Headers.Referrer);
            Assert.Equal(Token, QueryHelpers.ParseQuery(r.RequestUri.Query)["_sharing_id"].ToString());
            Assert.DoesNotContain("%22", r.RequestUri.Query);
            var content = r.Content!;
            var boundary = content.Headers.ContentType!.Parameters.Single(p => p.Name == "boundary").Value!;
            Assert.DoesNotContain('"', boundary);
            Assert.StartsWith("--" + boundary + "\r\n", body);
            Assert.EndsWith("\r\n--" + boundary + "--\r\n", body);
            var bytes = await content.ReadAsByteArrayAsync();
            Assert.Equal(bytes.LongLength, content.Headers.ContentLength);
            Assert.Contains("filename=\"" + filename + "\"", Encoding.UTF8.GetString(bytes));
            Assert.DoesNotContain("filename*=", body);
            Assert.StartsWith("\r\nContent-Disposition: form-data; name=\"file\"; filename=\"" + filename + "\"\r\nContent-Type:", body.Split("--" + boundary)[^2]);
            Assert.DoesNotContain("=?utf-8?", body, StringComparison.OrdinalIgnoreCase);
            var reader = new Microsoft.AspNetCore.WebUtilities.MultipartReader(boundary, new MemoryStream(bytes));
            var names = new List<string>();
            while (await reader.ReadNextSectionAsync() is { } section)
            {
                var disposition = Microsoft.Net.Http.Headers.ContentDispositionHeaderValue.Parse(section.ContentDisposition);
                var name = Microsoft.Net.Http.Headers.HeaderUtilities.RemoveQuotes(disposition.Name).Value!;
                names.Add(name);
                Assert.Contains("name=\"" + name + "\"", section.ContentDisposition!);
                using var text = new StreamReader(section.Body, Encoding.UTF8);
                var value = await text.ReadToEndAsync();
                if (name == "file") { Assert.Equal("application/octet-stream", section.ContentType); Assert.Equal(payload, value); }
                else Assert.Null(section.ContentType);
                if (name == "mtime") Assert.InRange(long.Parse(value), started, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                if (name == "overwrite") Assert.Equal("false", value);
                if (name == "sharing_id") Assert.Equal(Token, value);
                if (name == "size") Assert.Equal(Encoding.UTF8.GetByteCount(payload).ToString(), value);
            }
            Assert.Equal(new[] { "overwrite", "mtime", "sharing_id", "uploader_name", "size", "file" }, names);
            return Response("{\"success\":true}", "text/plain");
        });
        using var gateway = Gateway(mock);
        var data = Encoding.UTF8.GetBytes(payload);
        await gateway.UploadAsync(new MemoryStream(data), data.Length, filename, "Visitor", default);
        Assert.True(checkedUpload);
    }
    [Fact] public async Task UploadQueueReusesBootstrapButChecksDsmForEveryFile()
    {
        var mock = new Mock((r, body) => Task.FromResult(r.RequestUri!.AbsolutePath == "/webapi/entry.cgi"
            || body.Contains("SYNO.FileStation.CheckPermission") ? Response("{\"success\":true,\"data\":{}}") : Standard(r, upload: true)));
        using var gateway = Gateway(mock);
        await gateway.ValidateAsync(default); // Opening the UI initializes this visitor's share connection.
        var callsBeforeUploads = mock.Calls.Count;
        await gateway.UploadAsync(new MemoryStream([1]), 1, "first.txt", "Visitor", default);
        await gateway.UploadAsync(new MemoryStream([2]), 1, "second.txt", "Visitor", default);
        Assert.Equal(6, mock.Calls.Count - callsBeforeUploads); // Initdata + preflight + native upload, per file.
        Assert.Single(mock.Calls, c => c.Path == "/sharing/" + Token);
        Assert.Single(mock.Calls, c => c.Path == "/sharing/webapi/entry.cgi" && c.Body == "");
        Assert.Equal(3, mock.Calls.Count(c => c.Body.Contains("SYNO.Core.Sharing.Initdata")));
        Assert.Equal(2, mock.Calls.Count(c => c.Body.Contains("SYNO.FileStation.CheckPermission")));
    }
    [Fact] public async Task ReusedUploadSessionStillRejectsRevocationBeforeReadingNextFile()
    {
        var revoked = false;
        var mock = new Mock((r, body) => Task.FromResult(r.RequestUri!.AbsolutePath == "/webapi/entry.cgi"
            || body.Contains("SYNO.FileStation.CheckPermission") ? Response("{\"success\":true,\"data\":{}}")
            : body.Contains("SYNO.Core.Sharing.Initdata") ? Response(Init(upload: true, enabled: !revoked)) : Standard(r, upload: true)));
        using var gateway = Gateway(mock);
        await gateway.UploadAsync(new MemoryStream([1]), 1, "first.txt", "Visitor", default);
        revoked = true;
        using var next = new MemoryStream([2]);
        var error = await Assert.ThrowsAsync<ProxyException>(() => gateway.UploadAsync(next, 1, "second.txt", "Visitor", default));
        Assert.Equal(410, error.Status); Assert.Equal(0, next.Position);
        Assert.Single(mock.Calls, c => c.Path == "/webapi/entry.cgi");
    }
    [Fact] public async Task ExpiredDsmSessionIsReinitializedOnTheNextAttempt()
    {
        var initCalls = 0;
        var mock = new Mock((r, body) => Task.FromResult(body.Contains("SYNO.Core.Sharing.Initdata") && ++initCalls == 1
            ? Response("{\"success\":false,\"error\":{\"code\":106}}") : Standard(r)));
        using var gateway = Gateway(mock);
        Assert.Equal("password_required", (await Assert.ThrowsAsync<ProxyException>(() => gateway.ValidateAsync(default))).Code);
        Assert.Equal("download", (await gateway.ValidateAsync(default)).Public.Mode);
        Assert.Equal(2, mock.Calls.Count(c => c.Path == "/sharing/" + Token));
    }
    [Fact] public async Task NativeUploadFileErrorIsNotMisreportedAsAnHttpAuthenticationFailure()
    {
        var mock = new Mock((r, body) => Task.FromResult(r.RequestUri!.AbsolutePath == "/webapi/entry.cgi"
            ? Response("{\"success\":false,\"error\":{\"code\":401}}", "text/plain")
            : body.Contains("SYNO.FileStation.CheckPermission") ? Response("{\"success\":true,\"data\":{}}") : Standard(r, upload: true)));
        using var gateway = Gateway(mock);
        var error = await Assert.ThrowsAsync<ProxyException>(() => gateway.UploadAsync(new MemoryStream([1]), 1, "one.txt", "Visitor", default));
        Assert.Equal(502, error.Status); Assert.Equal("upload_failed", error.Code);
    }
    [Fact] public async Task UploadDenialDoesNotReadBinary()
    {
        var mock = new Mock((r, body) => Task.FromResult(body.Contains("SYNO.FileStation.CheckPermission") ? Response("{\"success\":false,\"error\":{\"code\":105}}") : Standard(r, upload: true)));
        using var gateway = Gateway(mock); using var body = new MemoryStream(Encoding.UTF8.GetBytes("untouched"));
        await Assert.ThrowsAsync<ProxyException>(() => gateway.UploadAsync(body, 9, "one.txt", "Visitor", default)); Assert.Equal(0, body.Position);
        Assert.DoesNotContain(mock.Calls, c => c.Path == "/webapi/entry.cgi");
    }
    [Fact] public async Task UploadToDownloadShareRejected()
    {
        var mock = new Mock((r, _) => Task.FromResult(Standard(r))); using var gateway = Gateway(mock);
        Assert.Equal("upload_not_allowed", (await Assert.ThrowsAsync<ProxyException>(() => gateway.UploadAsync(Stream.Null, 0, "one.txt", "Visitor", default))).Code);
    }
    [Fact] public async Task TransferLimitAndTruncatedBodyEnforced()
    {
        await Assert.ThrowsAsync<ProxyException>(() => TransferIO.CopyAsync(new MemoryStream(new byte[11]), Stream.Null, 10, 10, default));
        Assert.Equal("size_mismatch", (await Assert.ThrowsAsync<ProxyException>(() => TransferIO.CopyAsync(new MemoryStream(new byte[9]), Stream.Null, 10, 10, default, true))).Code);
    }
    [Fact] public void CsrfIsBoundToBrowserSession()
    {
        var browser = new SessionStore.BrowserSession(); SessionStore.CheckCsrf(browser.Csrf, browser);
        Assert.Throws<ProxyException>(() => SessionStore.CheckCsrf(new SessionStore.BrowserSession().Csrf, browser));
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PasswordBootstrapUsesCapturedEncryptionEndpointWithoutPrematureShareHeader(bool accepted)
    {
        using var rsa = RSA.Create(4096);
        var encryption = JsonSerializer.Serialize(new { success = true, data = new {
            cipherkey = "__cIpHeRtExT", ciphertoken = "__testTime",
            public_key = Convert.ToHexString(rsa.ExportParameters(false).Modulus!), server_time = 1234567 } });
        var mock = new Mock((request, body) =>
        {
            var fields = QueryHelpers.ParseQuery(body);
            fields.TryGetValue("api", out var api);
            if (api == "SYNO.API.Encryption")
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Equal("/sharing/webapi/encryption.cgi/SYNO.API.Encryption", request.RequestUri!.AbsolutePath);
                Assert.False(request.Headers.Contains("X-SYNO-SHARING"));
                Assert.Equal("module", fields["format"].ToString());
                return Task.FromResult(Response(encryption));
            }
            if (api == "SYNO.Core.Sharing.Login")
            {
                Assert.Equal("/sharing/webapi/entry.cgi/SYNO.Core.Sharing.Login", request.RequestUri!.AbsolutePath);
                Assert.False(request.Headers.Contains("X-SYNO-SHARING"));
                Assert.False(fields.ContainsKey("password"));
                using var envelope = JsonDocument.Parse(fields["__cIpHeRtExT"].ToString());
                Assert.True(envelope.RootElement.TryGetProperty("rsa", out _));
                Assert.True(envelope.RootElement.TryGetProperty("aes", out _));
                return Task.FromResult(Response(accepted
                    ? "{\"success\":true,\"data\":{\"sharing_sid\":\"syntheticSession123\"}}"
                    : "{\"success\":false,\"error\":{\"code\":400}}"));
            }
            if (api == "SYNO.Core.Sharing.Initdata")
                Assert.Equal(Token, request.Headers.GetValues("X-SYNO-SHARING").Single());
            return Task.FromResult(Standard(request, folder: false, protect: "password"));
        });
        using var gateway = Gateway(mock);
        if (accepted)
        {
            await gateway.UnlockAsync("sample-password", default);
            var state = await gateway.ValidateAsync(default);
            Assert.True(state.Public.PasswordProtected);
            Assert.Single(state.Public.Files);
        }
        else
        {
            Assert.Equal(401, (await Assert.ThrowsAsync<ProxyException>(() => gateway.UnlockAsync("sample-password", default))).Status);
            Assert.Equal("password_required", (await Assert.ThrowsAsync<ProxyException>(() => gateway.ValidateAsync(default))).Code);
            Assert.DoesNotContain(mock.Calls, call => call.Body.Contains("SYNO.Core.Sharing.Initdata"));
        }
    }

    [Fact] public void EncryptionEnvelopeRoundTripsAsDsmProtocol()
    {
        using var rsa = RSA.Create(4096); var modulus = Convert.ToHexString(rsa.ExportParameters(false).Modulus!);
        using var info = JsonDocument.Parse(JsonSerializer.Serialize(new { cipherkey = "__cIpHeRtExT", ciphertoken = "__testTime", public_key = modulus, server_time = 1234567 }));
        using var envelope = JsonDocument.Parse(DsmEncryption.Encrypt(info.RootElement, Token, "sample-password"));
        var secret = rsa.Decrypt(Convert.FromBase64String(envelope.RootElement.GetProperty("rsa").GetString()!), RSAEncryptionPadding.Pkcs1);
        var aesBody = Convert.FromBase64String(envelope.RootElement.GetProperty("aes").GetString()!); Assert.Equal("Salted__", Encoding.ASCII.GetString(aesBody[..8]));
        // Independent implementation of the OpenSSL EVP_BytesToKey passphrase derivation.
        byte[] prev = [], material = []; while (material.Length < 48) { prev = MD5.HashData([.. prev, .. secret, .. aesBody[8..16]]); material = [.. material, .. prev]; }
        using var aes = Aes.Create(); aes.Key = material[..32]; var decrypted = Encoding.UTF8.GetString(aes.DecryptCbc(aesBody[16..], material[32..48], PaddingMode.PKCS7));
        var parameters = QueryHelpers.ParseQuery(decrypted);
        Assert.Equal("\"sample-password\"", parameters["password"].ToString()); Assert.Equal(JsonSerializer.Serialize(Token), parameters["sharing_id"].ToString()); Assert.Equal("1234567", parameters["__testTime"].ToString());
    }
}
