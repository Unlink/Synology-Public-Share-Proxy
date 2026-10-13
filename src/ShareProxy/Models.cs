using System.Text.RegularExpressions;

namespace ShareProxy;

public sealed class ProxyOptions
{
    public string DsmOrigin { get; set; } = "";
    public string PublicOrigin { get; set; } = "";
    public bool UploadsEnabled { get; set; } = true;
    public bool PasswordLoginEnabled { get; set; } = true;
    public bool DiagnosticsVerbose { get; set; }
    public long MaxUploadBytes { get; set; } = 1L << 30;
    public long MaxDownloadBytes { get; set; } = 10L << 30;
    public int MetadataTimeoutSeconds { get; set; } = 20;
    public int TransferTimeoutSeconds { get; set; } = 1800;
    public int IdleTimeoutSeconds { get; set; } = 60;
    public int SessionMinutes { get; set; } = 30;
    public int MaxSessions { get; set; } = 256;
    public int MaxConcurrentTransfers { get; set; } = 4;

    public Uri Validate(bool development)
    {
        if (!Uri.TryCreate(DsmOrigin, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
            throw new InvalidOperationException("Proxy:DsmOrigin must be an absolute address beginning with http:// or https://.");
        if (uri.AbsolutePath != "/" || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new InvalidOperationException("Proxy:DsmOrigin must contain only scheme, host and port; remove credentials, /sharing paths, query and fragment.");
        if (!Uri.TryCreate(PublicOrigin, UriKind.Absolute, out var pub) || pub.AbsolutePath != "/" ||
            pub.UserInfo.Length > 0 || pub.Query.Length > 0 || pub.Fragment.Length > 0 ||
            pub.Scheme is not ("https" or "http"))
            throw new InvalidOperationException("Proxy:PublicOrigin must be an HTTP or HTTPS origin without credentials, path, query or fragment.");
        if (MaxUploadBytes < 1 || MaxUploadBytes > 100L << 30 || MaxDownloadBytes < 1 || MaxDownloadBytes > 1L << 40 ||
            MetadataTimeoutSeconds is < 1 or > 120 || TransferTimeoutSeconds is < 1 or > 7200 ||
            IdleTimeoutSeconds is < 1 or > 300 || SessionMinutes is < 1 or > 120 || MaxSessions is < 1 or > 4096 ||
            MaxConcurrentTransfers is < 1 or > 32) throw new InvalidOperationException("Invalid proxy limits.");
        return uri;
    }

}

public sealed class ProxyException(int status, string code) : Exception(code)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}
public sealed record PublicFile(string Id, string Name, bool IsDirectory, long? Size, long? Modified);
public sealed record FilePage(IReadOnlyList<PublicFile> Files, int Offset, int Total);
public sealed record ShareInfo(string Title, string Description, string Mode, string? ExpiresAt, bool PasswordProtected,
    long MaxUploadBytes, IReadOnlyList<PublicFile> Files);
public sealed record ShareState(ShareInfo Public, string RootPath, string Filename, bool Folder);

public static partial class Input
{
    [GeneratedRegex("\\A[A-Za-z0-9_-]{6,128}\\z", RegexOptions.CultureInvariant)] private static partial Regex TokenPattern();
    [GeneratedRegex("\\A[A-Za-z0-9_-]{16,128}\\z", RegexOptions.CultureInvariant)] private static partial Regex IdPattern();
    public static string Token(string token) => TokenPattern().IsMatch(token) ? token : throw new ProxyException(404, "unavailable");
    public static bool Id(string? id) => id != null && IdPattern().IsMatch(id);
    public static string Filename(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 240 || name is "." or ".." || name.EndsWith('.') ||
            name.Any(c => char.IsControl(c) || "/\\:<>\"|?*".Contains(c)) || System.Text.Encoding.UTF8.GetByteCount(name) > 255)
            throw new ProxyException(400, "invalid_filename");
        return name;
    }
    public static string Uploader(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 80 || value.Any(c => char.IsControl(c) || "/\\".Contains(c)))
            throw new ProxyException(400, "invalid_uploader");
        return value.Trim();
    }
    public static string SafePath(string path)
    {
        if (!path.StartsWith('/') || path.Length > 4096 || path.Contains('\\') || path.Contains('%') ||
            path.Any(char.IsControl) || path.Split('/').Skip(1).Any(p => p is "." or ".." or ""))
            throw new ProxyException(502, "protocol_error");
        return path;
    }
    public static bool Within(string path, string root) => path == root || path.StartsWith(root + "/", StringComparison.Ordinal);
}
