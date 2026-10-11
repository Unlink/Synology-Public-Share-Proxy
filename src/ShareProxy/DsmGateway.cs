using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebUtilities;

namespace ShareProxy;

public sealed partial class DsmGateway(HttpClient client, ProxyOptions options, string shareToken) : IDisposable
{
    private readonly string token = Input.Token(shareToken);
    private readonly Dictionary<string, Handle> handles = new(StringComparer.Ordinal);
    private static int globalHandles;
    private const int GlobalHandleLimit = 32768;
    private string? currentRoot;
    private bool unlocked;
    public SemaphoreSlim Gate { get; } = new(1, 1);
    private sealed record Handle(string Path, PublicFile File);
    private const string Entry = "/sharing/webapi/entry.cgi";
    public static string NewId() => WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(24));

    [GeneratedRegex("<script\\b[^>]*\\bsrc=[\"'](?<src>[^\"']+)[\"']", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Scripts();

    public async Task<ShareState> ValidateAsync(CancellationToken ct)
    {
        // Read only the bootstrap URL. Never execute or return DSM HTML, scripts, assets or URLs.
        using var page = Request(HttpMethod.Get, "/sharing/" + token);
        var html = await TextAsync(page, ct);
        var status = BootstrapStatus(html, token);
        if (status == "user") throw new ProxyException(403, "account_share_unsupported");
        if (status == "password" && !unlocked) throw new ProxyException(401, "password_required");
        if (status is not ("none" or "password")) throw new ProxyException(404, "unavailable");
        using var sessionReq = Request(HttpMethod.Get, Entry + "?" + Encode(new Dictionary<string, string>
        {
            ["api"] = "SYNO.Core.Sharing.Session", ["method"] = "get", ["version"] = "1",
            ["sharing_id"] = JsonSerializer.Serialize(token), ["sharing_status"] = JsonSerializer.Serialize(status)
        }));
        var (session, extra) = ParseSession(await TextAsync(sessionReq, ct));
        using (session) using (extra)
        {
            var s = session.RootElement; var e = extra.RootElement;
            if (!Bool(s, "sharing") || Str(s, "sharing_id") != token || Bool(s, "isLogined"))
                throw new ProxyException(502, "protocol_error");
            if (!e.TryGetProperty("status", out var statusValue) || !statusValue.TryGetInt32(out var statusCode))
                throw new ProxyException(502, "protocol_error");
            if (statusCode != 0) throw new ProxyException(410, "unavailable");
            using var init = await ApiAsync("SYNO.Core.Sharing.Initdata", "get", 1, [], ct);
            var data = init.RootElement.GetProperty("data");
            var sharing = data.GetProperty("Sharing");
            if (!Bool(sharing, "enabled")) throw new ProxyException(410, "unavailable");
            var protect = Str(sharing, "protect_type");
            if (protect == "password" && !unlocked) throw new ProxyException(401, "password_required");
            if (protect is not ("none" or "password")) throw new ProxyException(403, "account_share_unsupported");
            if (Str(sharing, "redirect_type") != "none") throw new ProxyException(403, "unsupported_share");
            // These features depend on visitor IP / visit counting; the proxy has its own upstream IP.
            // Deny rather than silently change their semantics or cache away DSM enforcement.
            if (Bool(sharing, "enable_match_ip") || Number(sharing, "expire_times") > 0)
                throw new ProxyException(403, "restricted_share_unsupported");
            var app = sharing.GetProperty("app");
            if (!app.TryGetProperty("enable_upload", out var uploadFlag) || uploadFlag.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                || !app.TryGetProperty("is_folder", out var folderFlag) || folderFlag.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new ProxyException(502, "protocol_error");
            var upload = Bool(app, "enable_upload");
            var folder = Bool(app, "is_folder");
            var privateData = data.TryGetProperty("Private", out var pd) ? pd : default;
            var filename = Str(privateData, "filename");
            if (filename.Length == 0) filename = Str(e, "filename");
            var title = upload ? Str(app, "request_name") : filename;
            if (!upload) Input.Filename(filename);
            var root = upload ? "" : Input.SafePath("/" + filename);
            if (currentRoot != root) { ClearHandles(); currentRoot = root; }
            var files = !upload && !folder ? new[] { Remember(root, filename, false, null, null) } : [];
            return new ShareState(new ShareInfo(title.Length > 0 ? title : "Zdieľané súbory", upload ? Str(app, "request_info") : "",
                upload ? "upload" : "download", NullIfEmpty(Str(sharing, "expire_at")), protect == "password", options.MaxUploadBytes, files), root, filename, folder);
        }
    }

    public async Task UnlockAsync(string password, CancellationToken ct)
    {
        if (!options.PasswordLoginEnabled) throw new ProxyException(403, "password_login_disabled");
        if (password.Length is < 1 or > 1024 || password.Any(char.IsControl)) throw new ProxyException(400, "invalid_password");
        // Only public share passwords; the request schema has no account/user/login fields.
        using var page = Request(HttpMethod.Get, "/sharing/" + token);
        var status = BootstrapStatus(await TextAsync(page, ct), token);
        if (status != "password") throw new ProxyException(403, "unsupported_share");
        using var encryption = await ApiAsync("SYNO.API.Encryption", "getinfo", 1,
            new() { ["format"] = "module" }, ct, Entry + "/SYNO.API.Encryption");
        var cipher = DsmEncryption.Encrypt(encryption.RootElement.GetProperty("data"), token, password);
        using var login = await ApiAsync("SYNO.Core.Sharing.Login", "login", 1,
            new() { ["__cIpHeRtExT"] = cipher }, ct, Entry + "/SYNO.Core.Sharing.Login", true);
        var sid = Str(login.RootElement.GetProperty("data"), "sharing_sid");
        if (sid.Length is < 8 or > 1024 || sid.Any(char.IsControl)) throw new ProxyException(502, "protocol_error");
        // Cookies are captured by this connection's CookieContainer, never exposed to the visitor.
        // A successful login alone is insufficient; Initdata must still authorize this share.
        unlocked = true;
        try { await ValidateAsync(ct); }
        catch { unlocked = false; ClearHandles(); throw; }
    }

    public async Task<FilePage> ListAsync(string? folderId, int offset, CancellationToken ct)
    {
        if (offset is < 0 or > 1000000) throw new ProxyException(400, "invalid_offset");
        var state = await ValidateAsync(ct);
        if (state.Public.Mode != "download") throw new ProxyException(403, "download_not_allowed");
        if (!state.Folder) return new FilePage(state.Public.Files, 0, state.Public.Files.Count);
        var path = state.RootPath;
        if (!string.IsNullOrEmpty(folderId))
        {
            var handle = Find(folderId, state.RootPath);
            if (!handle.File.IsDirectory) throw new ProxyException(400, "invalid_folder");
            path = handle.Path;
        }
        using var reply = await ApiAsync("SYNO.FolderSharing.List", "list", 2, new()
        {
            ["offset"] = offset.ToString(), ["limit"] = "200", ["sort_by"] = "\"name\"", ["sort_direction"] = "\"ASC\"",
            ["action"] = "\"enum\"", ["additional"] = "[\"size\",\"time\"]", ["filetype"] = "\"all\"",
            ["folder_path"] = JsonSerializer.Serialize(path), ["_sharing_id"] = JsonSerializer.Serialize(token)
        }, ct);
        var data = reply.RootElement.GetProperty("data");
        var total = checked((int)Number(data, "total"));
        if (total is < 0 or > 1000000) throw new ProxyException(502, "protocol_error");
        var list = new List<PublicFile>();
        foreach (var item in data.GetProperty("files").EnumerateArray())
        {
            if (list.Count >= 200) throw new ProxyException(502, "protocol_error");
            var itemPath = Input.SafePath(Str(item, "path"));
            var name = Input.Filename(Str(item, "name"));
            if (!Input.Within(itemPath, state.RootPath) || itemPath != path + "/" + name)
                throw new ProxyException(502, "protocol_error");
            var additional = item.TryGetProperty("additional", out var a) ? a : default;
            var time = additional.ValueKind == JsonValueKind.Object && additional.TryGetProperty("time", out var t) ? t : default;
            var isdir = Bool(item, "isdir");
            list.Add(Remember(itemPath, name, isdir, isdir ? null : Number(additional, "size"), Number(time, "mtime")));
        }
        return new FilePage(list, offset, total);
    }

    public async Task DownloadAsync(IReadOnlyList<string> ids, HttpResponse output, CancellationToken ct)
    {
        if (ids.Count is < 1 or > 100 || ids.Distinct().Count() != ids.Count) throw new ProxyException(400, "invalid_selection");
        var state = await ValidateAsync(ct);
        if (state.Public.Mode != "download") throw new ProxyException(403, "download_not_allowed");
        var selection = ids.Select(id => Find(id, state.RootPath)).ToArray();
        using var req = state.Folder
            ? Request(HttpMethod.Post, "/fsdownload/webapi/file_download.cgi/shared-files.zip", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["api"] = "SYNO.FolderSharing.Download", ["method"] = "download", ["version"] = "2",
                ["mode"] = "\"download\"", ["stdhtml"] = "false", ["dlname"] = "\"shared-files.zip\"",
                ["path"] = JsonSerializer.Serialize(selection.Select(x => x.Path)), ["_sharing_id"] = JsonSerializer.Serialize(token), ["codepage"] = "\"enu\""
            }))
            : Request(HttpMethod.Get, "/fsdownload/" + token + "/" + Uri.EscapeDataString(state.Filename));
        using var transfer = CancellationTokenSource.CreateLinkedTokenSource(ct);
        transfer.CancelAfter(TimeSpan.FromSeconds(options.TransferTimeoutSeconds));
        using var response = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, transfer.Token);
        CheckStatus(response);
        var media = response.Content.Headers.ContentType?.MediaType;
        // Require DSM's attachment response, so HTML/login/error pages cannot masquerade as files.
        if (response.Content.Headers.ContentDisposition?.DispositionType != "attachment" || media is "application/json" or "text/html")
            throw new ProxyException(502, "download_rejected");
        var length = response.Content.Headers.ContentLength;
        if (length > options.MaxDownloadBytes) throw new ProxyException(413, "transfer_too_large");
        output.ContentType = "application/octet-stream";
        var disposition = new Microsoft.Net.Http.Headers.ContentDispositionHeaderValue("attachment");
        disposition.SetHttpFileName(state.Folder ? "shared-files.zip" : state.Filename);
        output.Headers.ContentDisposition = disposition.ToString();
        if (length.HasValue) output.ContentLength = length;
        await using var stream = await response.Content.ReadAsStreamAsync(transfer.Token);
        await TransferIO.CopyAsync(stream, output.Body, options.MaxDownloadBytes, options.IdleTimeoutSeconds, transfer.Token);
    }

    public async Task UploadAsync(Stream body, long length, string filename, string uploader, CancellationToken ct)
    {
        Input.Filename(filename); Input.Uploader(uploader);
        if (!options.UploadsEnabled) throw new ProxyException(403, "upload_disabled");
        if (length < 0 || length > options.MaxUploadBytes) throw new ProxyException(413, "transfer_too_large");
        var state = await ValidateAsync(ct);
        if (state.Public.Mode != "upload") throw new ProxyException(403, "upload_not_allowed");
        using var permission = await ApiAsync("SYNO.FileStation.CheckPermission", "write", 3, new()
        {
            ["filename"] = JsonSerializer.Serialize(filename), ["size"] = length.ToString(), ["overwrite"] = "false",
            ["sharing_id"] = JsonSerializer.Serialize(token), ["uploader_name"] = JsonSerializer.Serialize(uploader)
        }, ct);
        using var form = new MultipartFormDataContent();
        // The multipart data fields in this API are raw strings. The query _sharing_id is JSON encoded.
        foreach (var pair in new Dictionary<string, string>
        {
            ["overwrite"] = "false", ["mtime"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(),
            ["sharing_id"] = token, ["uploader_name"] = uploader, ["size"] = length.ToString()
        }) form.Add(new StringContent(pair.Value, Encoding.UTF8), pair.Key);
        using var content = new UploadContent(body, length, options.IdleTimeoutSeconds);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(content, "file", filename); // Binary MUST be the final part.
        using var req = Request(HttpMethod.Post, "/webapi/entry.cgi?" + Encode(new Dictionary<string, string>
        {
            ["api"] = "SYNO.FileStation.Upload", ["method"] = "upload", ["version"] = "2", ["_sharing_id"] = JsonSerializer.Serialize(token)
        }), form);
        using var transfer = CancellationTokenSource.CreateLinkedTokenSource(ct);
        transfer.CancelAfter(TimeSpan.FromSeconds(options.TransferTimeoutSeconds));
        using var response = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, transfer.Token);
        CheckStatus(response);
        using var result = JsonDocument.Parse(await ReadLimitedAsync(response, transfer.Token));
        CheckApi(result.RootElement, false);
    }

    private PublicFile Remember(string path, string name, bool directory, long? size, long? modified)
    {
        var existing = handles.FirstOrDefault(x => x.Value.Path == path);
        var id = existing.Key ?? NewId();
        if (handles.Count >= 4096 && existing.Key == null) throw new ProxyException(429, "too_many_files");
        if (existing.Key == null && Interlocked.Increment(ref globalHandles) > GlobalHandleLimit)
        {
            Interlocked.Decrement(ref globalHandles);
            throw new ProxyException(429, "too_many_files");
        }
        var file = new PublicFile(id, name, directory, size, modified);
        handles[id] = new Handle(path, file);
        return file;
    }
    private Handle Find(string id, string root) => Input.Id(id) && handles.TryGetValue(id, out var handle) && Input.Within(handle.Path, root)
        ? handle : throw new ProxyException(404, "file_not_found");

    private HttpRequestMessage Request(HttpMethod method, string path, HttpContent? body = null)
    {
        // Only callsites in this class choose the path; no URL or endpoint comes from a visitor/DSM response.
        var request = new HttpRequestMessage(method, new Uri(client.BaseAddress!, path)) { Content = body };
        request.Headers.Add("X-SYNO-SHARING", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
        return request;
    }
    private async Task<JsonDocument> ApiAsync(string api, string method, int version, Dictionary<string, string> args,
        CancellationToken ct, string path = Entry, bool login = false)
    {
        // A closed set of operations, never SYNO.API.Auth, generic API discovery or arbitrary forwarding.
        if ((api, method, version) is not (("SYNO.Core.Sharing.Initdata", "get", 1) or ("SYNO.API.Encryption", "getinfo", 1)
            or ("SYNO.Core.Sharing.Login", "login", 1) or ("SYNO.FolderSharing.List", "list", 2)
            or ("SYNO.FileStation.CheckPermission", "write", 3))) throw new ProxyException(404, "unavailable");
        args["api"] = api; args["method"] = method; args["version"] = version.ToString();
        using var req = Request(HttpMethod.Post, path, new FormUrlEncodedContent(args));
        var document = JsonDocument.Parse(await TextAsync(req, ct));
        try { CheckApi(document.RootElement, login); return document; }
        catch { document.Dispose(); throw; }
    }
    private async Task<string> TextAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.MetadataTimeoutSeconds));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        CheckStatus(response);
        return await ReadLimitedAsync(response, timeout.Token);
    }
    private static async Task<string> ReadLimitedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength > 4 * 1024 * 1024) throw new ProxyException(502, "protocol_error");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        await TransferIO.CopyAsync(stream, buffer, 4 * 1024 * 1024, 20, ct);
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }
    private static void CheckStatus(HttpResponseMessage response)
    {
        if ((int)response.StatusCode is >= 300 and < 400) throw new ProxyException(502, "upstream_redirect_rejected");
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone) throw new ProxyException(410, "unavailable");
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) throw new ProxyException(401, "password_required");
        if (response.StatusCode != HttpStatusCode.OK) throw new ProxyException(502, "upstream_error");
        if (response.Headers.TryGetValues("X-Request-Error", out var values) && values.Contains("errorhash")) throw new ProxyException(502, "upstream_error");
    }
    private static void CheckApi(JsonElement json, bool login)
    {
        if (!Bool(json, "success"))
        {
            if (login) throw new ProxyException(401, "invalid_password");
            var error = json.TryGetProperty("error", out var e) ? e : default;
            var code = Number(error, "code");
            throw code is 106 or 107 or 119 ? new ProxyException(401, "password_required") : new ProxyException(403, "operation_rejected");
        }
    }
    public static string BootstrapStatus(string html, string token)
    {
        foreach (Match match in Scripts().Matches(html))
        {
            var src = WebUtility.HtmlDecode(match.Groups["src"].Value);
            var queryAt = src.IndexOf('?');
            if (queryAt < 0) continue;
            var q = QueryHelpers.ParseQuery(src[(queryAt + 1)..]);
            if (q["api"] != "SYNO.Core.Sharing.Session") continue;
            if (JsonSerializer.Deserialize<string>(q["sharing_id"].ToString()) != token) throw new ProxyException(502, "protocol_error");
            return JsonSerializer.Deserialize<string>(q["sharing_status"].ToString()) ?? throw new ProxyException(502, "protocol_error");
        }
        throw new ProxyException(410, "unavailable");
    }
    public static (JsonDocument Session, JsonDocument Extra) ParseSession(string text)
    {
        var cursor = 0;
        var first = Assignment(text, "SYNO.SDS.Session", ref cursor);
        try
        {
            var second = Assignment(text, "SYNO.SDS.ExtraSession", ref cursor);
            if (!string.IsNullOrWhiteSpace(text[cursor..])) { second.Dispose(); throw new ProxyException(502, "protocol_error"); }
            return (first, second);
        }
        catch { first.Dispose(); throw; }
    }
    private static JsonDocument Assignment(string text, string name, ref int cursor)
    {
        while (cursor < text.Length && char.IsWhiteSpace(text[cursor])) cursor++;
        if (!text.AsSpan(cursor).StartsWith(name, StringComparison.Ordinal)) throw new ProxyException(502, "protocol_error");
        cursor += name.Length;
        while (cursor < text.Length && char.IsWhiteSpace(text[cursor])) cursor++;
        if (cursor >= text.Length || text[cursor++] != '=') throw new ProxyException(502, "protocol_error");
        var bytes = Encoding.UTF8.GetBytes(text[cursor..]);
        var reader = new Utf8JsonReader(bytes);
        var document = JsonDocument.ParseValue(ref reader);
        cursor += Encoding.UTF8.GetString(bytes, 0, (int)reader.BytesConsumed).Length;
        while (cursor < text.Length && char.IsWhiteSpace(text[cursor])) cursor++;
        if (cursor >= text.Length || text[cursor++] != ';') { document.Dispose(); throw new ProxyException(502, "protocol_error"); }
        return document;
    }
    private static string Encode(Dictionary<string, string> args) => string.Join('&', args.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));
    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;
    internal static string Str(JsonElement e, string key) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString()! : "";
    internal static bool Bool(JsonElement e, string key) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.True;
    internal static long Number(JsonElement e, string key) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var p) && p.TryGetInt64(out var n) ? n : 0;
    private void ClearHandles()
    {
        Interlocked.Add(ref globalHandles, -handles.Count);
        handles.Clear();
    }
    public void Dispose() { ClearHandles(); client.Dispose(); Gate.Dispose(); }
}
