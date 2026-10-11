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
    public void RejectsMalformedTokens(string token) => Assert.Throws<ProxyException>(() => Input.Token(token));
    [Theory]
    [InlineData("../private.txt")][InlineData("x\\private.txt")][InlineData("x:stream")][InlineData("file\r\nHeader")][InlineData("..")] [InlineData("unsafe.")]
    public void RejectsUnsafeFilenames(string filename) => Assert.Throws<ProxyException>(() => Input.Filename(filename));
    [Fact] public void RejectsNonTlsOrCredentialOrigins()
    {
        Assert.Throws<InvalidOperationException>(() => new ProxyOptions { DsmOrigin = "http://nas:5000", PublicOrigin = Options.PublicOrigin }.Validate(false));
        Assert.Throws<InvalidOperationException>(() => new ProxyOptions { DsmOrigin = "https://user:password@nas", PublicOrigin = Options.PublicOrigin }.Validate(false));
        Assert.Throws<InvalidOperationException>(() => new ProxyOptions { DsmOrigin = "https://nas/admin", PublicOrigin = Options.PublicOrigin }.Validate(false));
    }
    [Fact] public void ParsesJsonAssignmentsWithoutExecutingScript()
    {
        var (s, e) = DsmGateway.ParseSession(Session()); using (s) using (e) Assert.Equal("Project", e.RootElement.GetProperty("filename").GetString());
        Assert.Throws<ProxyException>(() => DsmGateway.ParseSession(Session() + "fetch('/admin')"));
        Assert.Throws<ProxyException>(() => DsmGateway.ParseSession("eval('anything')"));
    }
    [Fact] public void RejectsBootstrapForAnotherShare() => Assert.Throws<ProxyException>(() => DsmGateway.BootstrapStatus(Bootstrap(token: "otherSynthetic123"), Token));
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
    [Fact] public async Task ZipDownloadUsesOnlyEnumeratedPathsAndStreamsAttachment()
    {
        var mock = new Mock((r, body) =>
        {
            if (r.RequestUri!.AbsolutePath.Contains("fsdownload"))
            {
                Assert.Equal("[\"/Project/one.txt\"]", QueryHelpers.ParseQuery(body)["path"].ToString());
                var response = Response("zip-bytes", "application/octet-stream");
                response.Content.Headers.ContentDisposition = new("attachment"); return Task.FromResult(response);
            }
            return Task.FromResult(body.Contains("SYNO.FolderSharing.List") ? Response("{\"success\":true,\"data\":{\"total\":1,\"files\":[{\"path\":\"/Project/one.txt\",\"name\":\"one.txt\",\"isdir\":false}]}}") : Standard(r));
        });
        using var gateway = Gateway(mock); var file = (await gateway.ListAsync(null, 0, default)).Files.Single();
        var context = new DefaultHttpContext(); context.Response.Body = new MemoryStream();
        await gateway.DownloadAsync([file.Id], context.Response, default);
        Assert.Equal("zip-bytes", Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray()));
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
                Assert.True(body.IndexOf("name=file", StringComparison.Ordinal) > body.IndexOf("name=size", StringComparison.Ordinal));
                Assert.DoesNotContain("dest_folder_path", body); return Task.FromResult(Response("{\"success\":true}"));
            }
            return Task.FromResult(Standard(r, upload: true));
        });
        using var gateway = Gateway(mock); var bytes = Encoding.UTF8.GetBytes("binary-payload");
        await gateway.UploadAsync(new MemoryStream(bytes), bytes.Length, "one.txt", "Visitor", default);
        Assert.Contains(mock.Calls, c => c.Body.Contains("SYNO.FileStation.CheckPermission"));
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
