using System.Diagnostics;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.Routing;

namespace ShareProxy;

// Every logged string is generated here or chosen from a closed set. Never pass an exception,
// request, response, URL, certificate, scope, filename or DSM body to ILogger.
public sealed class ProxyDiagnostics(ILogger<ProxyDiagnostics> logger, IHttpContextAccessor accessor, ProxyOptions options)
{
    public const string Category = "ShareProxy.ProxyDiagnostics";
    private static readonly HashSet<string> Codes = new(StringComparer.Ordinal)
    {
        "unavailable", "password_required", "invalid_password", "request_rejected", "protocol_error",
        "account_share_unsupported", "restricted_share_unsupported", "unsupported_share", "operation_rejected",
        "transfer_too_large", "size_mismatch", "invalid_filename", "invalid_uploader", "invalid_offset",
        "invalid_folder", "invalid_selection", "file_not_found", "upload_disabled", "upload_not_allowed",
        "download_not_allowed", "download_rejected", "password_login_disabled", "length_required",
        "request_too_large", "share_busy", "server_busy", "too_many_files", "too_many_shares",
        "upstream_redirect_rejected", "upstream_error", "upstream_unavailable", "timeout", "upload_failed"
    };
    public sealed class RequestTrace(string operation)
    {
        // Ignore caller-supplied X-Request-ID / trace headers; they could contain secrets or log injection.
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public string Operation { get; } = operation;
        public long Started { get; } = Stopwatch.GetTimestamp();
        public DsmStep Step { get; set; }
        public int? UpstreamStatus { get; set; }
        public int? DsmCode { get; set; }
        public string DsmDetailCodes { get; set; } = "-";
        public long? UploadBytesSent { get; set; }
        public long? UploadBytesExpected { get; set; }
        public DsmResponseKind ResponseKind { get; set; }
        public SslPolicyErrors TlsPolicy { get; set; }
        public X509ChainStatusFlags TlsChain { get; set; }
    }
    public sealed record Failure(int Status, string Code, string Reason);
    private RequestTrace? Current => accessor.HttpContext?.Features.Get<RequestTrace>();

    public static void ConfigureFilters(LoggerFilterOptions filters)
    {
        // Even Logging__LogLevel__Default=Trace must not enable framework URL/exception logging.
        filters.MinLevel = LogLevel.None;
        filters.Rules.Clear();
        filters.Rules.Add(new LoggerFilterRule(null, null, null,
            (_, category, level) => category == Category && level >= LogLevel.Information));
    }
    public RequestTrace Begin(HttpContext context)
    {
        var operation = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText switch
        {
            "/api/session" => "session",
            "/api/shares/{token}" => "share",
            "/api/shares/{token}/files" => "list",
            "/api/shares/{token}/unlock" => "unlock",
            "/api/shares/{token}/upload" => "upload",
            "/api/shares/{token}/download" => "download",
            "/healthz" => "health",
            "/" or "/s/{token}" or "/sharing/{token}" => "page",
            "/app.js" or "/app.css" or "/icon.svg" => "asset",
            _ => "other"
        };
        var trace = new RequestTrace(operation);
        context.Features.Set(trace);
        context.Response.Headers["X-Request-ID"] = trace.Id;
        return trace;
    }
    public void Started() => logger.LogInformation("Proxy started; upstream_transport={Transport}; verbose={Verbose}",
        new Uri(options.DsmOrigin).Scheme == "https" ? "https_verified" : "http_local", options.DiagnosticsVerbose);

    public void Step(DsmStep step)
    {
        var trace = Current;
        if (trace == null) return;
        trace.Step = step; trace.UpstreamStatus = null; trace.DsmCode = null; trace.DsmDetailCodes = "-"; trace.ResponseKind = DsmResponseKind.None;
        trace.TlsPolicy = SslPolicyErrors.None; trace.TlsChain = X509ChainStatusFlags.NoError;
        if (options.DiagnosticsVerbose)
            logger.LogInformation("DSM start request={RequestId} operation={Operation} step={Step}", trace.Id, trace.Operation, step);
    }
    public void Response(HttpResponseMessage response)
    {
        var trace = Current;
        if (trace == null) return;
        trace.UpstreamStatus = (int)response.StatusCode;
        trace.ResponseKind = response.Content.Headers.ContentType?.MediaType switch
        {
            "text/html" => DsmResponseKind.Html,
            "application/json" => DsmResponseKind.Json,
            "application/javascript" or "text/javascript" => DsmResponseKind.Script,
            _ when response.Content.Headers.ContentDisposition?.DispositionType == "attachment" => DsmResponseKind.Attachment,
            "application/octet-stream" or "application/zip" => DsmResponseKind.Binary,
            _ => DsmResponseKind.Other
        };
        if (options.DiagnosticsVerbose)
            logger.LogInformation("DSM headers request={RequestId} step={Step} status={Status} response_kind={ResponseKind}", trace.Id, trace.Step, trace.UpstreamStatus, trace.ResponseKind);
    }
    public void ApiError(long code, JsonElement error = default)
    {
        if (Current is not { } trace) return;
        trace.DsmCode = code is >= 0 and <= 9999 ? (int)code : null;
        if (error.ValueKind == JsonValueKind.Object && error.TryGetProperty("errors", out var details) && details.ValueKind == JsonValueKind.Array)
        {
            var codes = new List<int>();
            foreach (var detail in details.EnumerateArray().Take(8))
                if (detail.ValueKind == JsonValueKind.Object && detail.TryGetProperty("code", out var number)
                    && number.ValueKind == JsonValueKind.Number && number.TryGetInt32(out var value) && value is >= 0 and <= 9999) codes.Add(value);
            trace.DsmDetailCodes = codes.Count == 0 ? "-" : string.Join(',', codes);
        }
    }
    public void UploadBytes(long sent, long expected)
    {
        if (Current is not { } trace) return;
        trace.UploadBytesSent = sent; trace.UploadBytesExpected = expected;
        if (options.DiagnosticsVerbose)
            logger.LogInformation("DSM upload bytes request={RequestId} sent={Sent} expected={Expected}", trace.Id, sent, expected);
    }
    public void Certificate(DsmCertificateObserver.Result? observation)
    {
        if (Current is { } trace && observation != null)
        {
            trace.TlsPolicy = observation.Policy;
            trace.TlsChain = observation.Chain;
        }
    }
    public static Failure Classify(Exception error, bool clientCancelled = false) => error switch
    {
        OperationCanceledException when clientCancelled => new(499, "timeout", "client_cancelled"),
        OperationCanceledException => new(504, "timeout", "timeout"),
        HttpRequestException h => new(502, "upstream_unavailable", NetworkReason(h.HttpRequestError, h.InnerException)),
        HttpIOException h => new(502, "upstream_unavailable", NetworkReason(h.HttpRequestError, h.InnerException)),
        ProxyException p when Codes.Contains(p.Code) => new(p.Status, p.Code, p.Code),
        JsonException => new(502, "protocol_error", "invalid_json"),
        BadHttpRequestException b => new(b.StatusCode, "request_rejected", "invalid_request"),
        KeyNotFoundException => new(502, "protocol_error", "missing_field"),
        InvalidOperationException => new(502, "protocol_error", "invalid_response_shape"),
        FormatException => new(502, "protocol_error", "invalid_value_format"),
        NotSupportedException => new(502, "protocol_error", "unsupported_response"),
        ArgumentException => new(502, "protocol_error", "invalid_argument"),
        OverflowException => new(502, "protocol_error", "value_out_of_range"),
        IOException => new(502, "upstream_unavailable", "stream_io_error"),
        _ => new(502, "protocol_error", "unexpected_error")
    };
    private static string NetworkReason(HttpRequestError error, Exception? inner) => error switch
    {
        HttpRequestError.NameResolutionError => "dns_failed",
        HttpRequestError.SecureConnectionError => "tls_failed",
        HttpRequestError.ConnectionError when inner is SocketException { SocketErrorCode: SocketError.ConnectionRefused } => "connection_refused",
        HttpRequestError.ConnectionError when inner is SocketException { SocketErrorCode: SocketError.TimedOut } => "connect_timeout",
        HttpRequestError.ConnectionError => "connection_failed",
        HttpRequestError.ResponseEnded => "response_interrupted",
        HttpRequestError.InvalidResponse or HttpRequestError.HttpProtocolError => "invalid_http_response",
        _ => "http_transport_failed"
    };
    public void Failed(RequestTrace trace, Failure failure, bool streamStarted)
        => logger.LogWarning("Proxy failure request={RequestId} operation={Operation} step={Step} status={Status} code={Code} reason={Reason} upstream_status={UpstreamStatus} response_kind={ResponseKind} dsm_code={DsmCode} dsm_detail_codes={DsmDetailCodes} upload_sent={UploadSent} upload_expected={UploadExpected} tls_policy={TlsPolicy} tls_chain={TlsChain} stream_started={StreamStarted} duration_ms={DurationMs}",
            trace.Id, trace.Operation, trace.Step, failure.Status, failure.Code, failure.Reason, trace.UpstreamStatus, trace.ResponseKind, trace.DsmCode,
            trace.DsmDetailCodes, trace.UploadBytesSent, trace.UploadBytesExpected, trace.TlsPolicy, trace.TlsChain, streamStarted, (long)Stopwatch.GetElapsedTime(trace.Started).TotalMilliseconds);
    public void Completed(RequestTrace trace, int status)
    {
        if (options.DiagnosticsVerbose && trace.Operation is not ("asset" or "health"))
            logger.LogInformation("Proxy completed request={RequestId} operation={Operation} status={Status} duration_ms={DurationMs}",
                trace.Id, trace.Operation, status, (long)Stopwatch.GetElapsedTime(trace.Started).TotalMilliseconds);
    }
}

public enum DsmStep { None, Bootstrap, ShareSession, InitData, EncryptionInfo, Unlock, ListFiles, UploadPermission, Download, Upload }
public enum DsmResponseKind { None, Html, Json, Script, Attachment, Binary, Other }

public sealed class DsmCertificateObserver
{
    // SocketsHttpHandler runs TLS callbacks without the caller's ExecutionContext. Capture only enum flags,
    // then attach them to the request in DsmGateway's awaited SendAsync. Gate serializes this connection.
    public sealed record Result(SslPolicyErrors Policy, X509ChainStatusFlags Chain);
    private Result? result;
    public Result? Observation => Volatile.Read(ref result);
    public void Reset() => Volatile.Write(ref result, null);
    public bool Validate(X509Chain? chain, SslPolicyErrors errors)
    {
        var flags = chain?.ChainStatus.Aggregate(X509ChainStatusFlags.NoError, (aggregate, status) => aggregate | status.Status)
            ?? X509ChainStatusFlags.NoError;
        Volatile.Write(ref result, new(errors, flags));
        // Preserve platform chain + hostname validation. Never accept an invalid certificate for diagnostics.
        return errors == SslPolicyErrors.None;
    }
}
