using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.Logging;
using Xunit;

namespace ShareProxy.Tests;

public sealed class DiagnosticsTests
{
    private const string Secret = "SyntheticSecretDoNotLog123";
    private sealed class Capture : ILogger<ProxyDiagnostics>
    {
        public List<string> Lines { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Assert.Null(exception);
            Lines.Add(formatter(state, exception));
        }
    }
    private static (ProxyDiagnostics Diagnostics, ProxyDiagnostics.RequestTrace Trace, Capture Log) Setup(bool verbose = true)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/shares/" + Secret;
        context.Request.QueryString = new("?password=" + Secret);
        context.Request.Headers["X-Request-ID"] = Secret;
        context.Request.Headers.Cookie = "session=" + Secret;
        context.SetEndpoint(new RouteEndpoint(_ => Task.CompletedTask, RoutePatternFactory.Parse("/api/shares/{token}"), 0,
            EndpointMetadataCollection.Empty, Secret));
        var accessor = new HttpContextAccessor { HttpContext = context };
        var capture = new Capture();
        var diagnostics = new ProxyDiagnostics(capture, accessor, new ProxyOptions { DiagnosticsVerbose = verbose });
        return (diagnostics, diagnostics.Begin(context), capture);
    }
    [Theory]
    [InlineData(HttpRequestError.NameResolutionError, "dns_failed")]
    [InlineData(HttpRequestError.SecureConnectionError, "tls_failed")]
    [InlineData(HttpRequestError.ConnectionError, "connection_failed")]
    [InlineData(HttpRequestError.ResponseEnded, "response_interrupted")]
    public void TransportErrorsAreUsefulWithoutLoggingExceptionOrCallerData(HttpRequestError type, string reason)
    {
        var (diagnostics, trace, log) = Setup();
        diagnostics.Step(DsmStep.Bootstrap);
        var failure = ProxyDiagnostics.Classify(new HttpRequestException(type, "https://nas.example/sharing/" + Secret,
            new AuthenticationException("Cookie/password=" + Secret)));
        diagnostics.Failed(trace, failure, false);
        Assert.Equal("upstream_unavailable", failure.Code);
        Assert.Equal(reason, failure.Reason);
        Assert.Matches("^[a-f0-9]{32}$", trace.Id);
        Assert.Equal("share", trace.Operation);
        Assert.Contains(log.Lines, l => l.Contains("request=" + trace.Id) && l.Contains("reason=" + reason));
        Assert.DoesNotContain(Secret, string.Join('\n', log.Lines));
        Assert.DoesNotContain("nas.example", string.Join('\n', log.Lines));
    }
    [Fact] public void RecognizesRefusedConnectionAndClientCancellation()
    {
        var failure = ProxyDiagnostics.Classify(new HttpRequestException(HttpRequestError.ConnectionError, Secret,
            new SocketException((int)SocketError.ConnectionRefused)));
        Assert.Equal("connection_refused", failure.Reason);
        Assert.Equal("client_cancelled", ProxyDiagnostics.Classify(new OperationCanceledException(Secret), true).Reason);
        Assert.Equal(499, ProxyDiagnostics.Classify(new OperationCanceledException(), true).Status);
        Assert.Equal(504, ProxyDiagnostics.Classify(new OperationCanceledException()).Status);
    }
    [Fact] public void RecordsDsmStatusWithoutBodyOrHeadersAndLogsStreamingFailures()
    {
        var (diagnostics, trace, log) = Setup();
        diagnostics.Step(DsmStep.ListFiles);
        using var response = new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent(Secret) };
        response.Headers.Add("Set-Cookie", "sid=" + Secret);
        response.Headers.Location = new Uri("https://nas.example/" + Secret);
        diagnostics.Response(response); diagnostics.ApiError(119);
        diagnostics.Failed(trace, ProxyDiagnostics.Classify(new ProxyException(401, "password_required")), true);
        Assert.Equal(403, trace.UpstreamStatus);
        Assert.Equal(119, trace.DsmCode);
        Assert.Contains(log.Lines, l => l.Contains("dsm_code=119") && l.Contains("stream_started=True"));
        Assert.DoesNotContain(Secret, string.Join('\n', log.Lines));
    }
    [Fact] public void QuietModeStillReportsFailuresAndVerboseModeResetsPreviousStepStatus()
    {
        var (diagnostics, trace, log) = Setup(false);
        diagnostics.Step(DsmStep.Bootstrap);
        using var response = new HttpResponseMessage(HttpStatusCode.OK);
        diagnostics.Response(response); diagnostics.ApiError(119);
        diagnostics.Step(DsmStep.ShareSession); diagnostics.Completed(trace, 200);
        Assert.Empty(log.Lines); Assert.Null(trace.UpstreamStatus); Assert.Null(trace.DsmCode);
        diagnostics.Failed(trace, ProxyDiagnostics.Classify(new KeyNotFoundException(Secret)), false);
        Assert.Single(log.Lines); Assert.Contains("reason=missing_field", log.Lines[0]);
        Assert.DoesNotContain(Secret, log.Lines[0]);
    }
    [Fact] public void NestedDsmErrorsLogOnlyBoundedNumericCodesAndByteCounts()
    {
        var (diagnostics, trace, log) = Setup();
        diagnostics.Step(DsmStep.Upload);
        using var error = System.Text.Json.JsonDocument.Parse("{\"code\":401,\"errors\":[{\"code\":1805,\"path\":\"" + Secret
            + "\"},{\"code\":\"" + Secret + "\"},{\"code\":123456789}]}");
        diagnostics.ApiError(401, error.RootElement);
        diagnostics.UploadBytes(16, 32);
        diagnostics.Failed(trace, ProxyDiagnostics.Classify(new ProxyException(403, "operation_rejected")), false);
        Assert.Equal("1805", trace.DsmDetailCodes);
        Assert.Equal(16, trace.UploadBytesSent); Assert.Equal(32, trace.UploadBytesExpected);
        Assert.Contains(log.Lines, line => line.Contains("dsm_detail_codes=1805") && line.Contains("upload_sent=16") && line.Contains("upload_expected=32"));
        Assert.DoesNotContain(Secret, string.Join('\n', log.Lines));
        Assert.DoesNotContain("123456789", string.Join('\n', log.Lines));
    }
    [Fact] public async Task UploadCounterTracksCopiedBinaryAndPreservesLengthChecks()
    {
        using var source = new MemoryStream(new byte[70_000]);
        using var content = new UploadContent(source, source.Length, 10);
        await content.CopyToAsync(Stream.Null);
        Assert.Equal(70_000, content.BytesSent);
        using var shortSource = new MemoryStream(new byte[3]);
        using var shortContent = new UploadContent(shortSource, 5, 10);
        Assert.Equal("size_mismatch", (await Assert.ThrowsAsync<ProxyException>(() => shortContent.CopyToAsync(Stream.Null))).Code);
        Assert.Equal(3, shortContent.BytesSent);
    }
    [Fact] public void FilterCannotEnableFrameworkRequestLogs()
    {
        var filters = new LoggerFilterOptions();
        filters.Rules.Add(new(null, "Microsoft.AspNetCore", LogLevel.Trace, null));
        ProxyDiagnostics.ConfigureFilters(filters);
        var rule = Assert.Single(filters.Rules);
        Assert.False(rule.Filter!(null, "Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Critical));
        Assert.False(rule.Filter!(null, "System.Net.Http.HttpClient", LogLevel.Information));
        Assert.True(rule.Filter!(null, ProxyDiagnostics.Category, LogLevel.Warning));
    }
    [Theory]
    [InlineData(SslPolicyErrors.None, true)]
    [InlineData(SslPolicyErrors.RemoteCertificateChainErrors, false)]
    [InlineData(SslPolicyErrors.RemoteCertificateNameMismatch, false)]
    [InlineData(SslPolicyErrors.RemoteCertificateNotAvailable, false)]
    [InlineData(SslPolicyErrors.RemoteCertificateChainErrors | SslPolicyErrors.RemoteCertificateNameMismatch, false)]
    public void CertificateObserverPreservesValidationAndAttachesOnlyEnumFlags(SslPolicyErrors errors, bool accepted)
    {
        var (diagnostics, trace, log) = Setup();
        var observer = new DsmCertificateObserver();
        Assert.Equal(accepted, observer.Validate(null, errors));
        diagnostics.Certificate(observer.Observation);
        Assert.Equal(errors, trace.TlsPolicy);
        observer.Reset(); Assert.Null(observer.Observation);
        Assert.Empty(log.Lines);
    }
    [SandboxTlsFact] public async Task RealSelfSignedTlsConnectionIsRejectedAndCertificateFlagsAreRecorded()
    {
        // Real local TLS handshake: verifies the diagnostic callback still rejects invalid chains/names.
        var (diagnostics, trace, log) = Setup();
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=synthetic.invalid", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(5));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var server = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync(timeout.Token);
            await using var tls = new SslStream(socket.GetStream());
            try { await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate }, timeout.Token); }
            catch (AuthenticationException) { }
            catch (IOException) { }
        }, timeout.Token);
        var origin = new Uri("https://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port);
        using var store = new SessionStore(new ProxyOptions { MaxSessions = 2 }, origin, true, diagnostics);
        using var lease = store.Acquire(new DefaultHttpContext());
        var gateway = store.Connection(lease.Browser, Secret);
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => gateway.ValidateAsync(timeout.Token));
        await server;
        Assert.Equal(HttpRequestError.SecureConnectionError, error.HttpRequestError);
        Assert.Equal(DsmStep.Bootstrap, trace.Step);
        Assert.True(trace.TlsPolicy.HasFlag(SslPolicyErrors.RemoteCertificateChainErrors),
            $"TLS policy={trace.TlsPolicy}; cause={error.InnerException?.GetType().Name}; nested={error.InnerException?.InnerException?.GetType().Name}; native={(error.InnerException?.InnerException as System.ComponentModel.Win32Exception)?.NativeErrorCode}");
        Assert.True(trace.TlsPolicy.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch));
        diagnostics.Failed(trace, ProxyDiagnostics.Classify(error), false);
        Assert.DoesNotContain(Secret, string.Join('\n', log.Lines));
        Assert.Contains(log.Lines, l => l.Contains("reason=tls_failed") && l.Contains("RemoteCertificateChainErrors"));
    }
}

public sealed class SandboxTlsFactAttribute : FactAttribute
{
    public SandboxTlsFactAttribute()
    {
        // This Windows sandbox's Schannel cannot obtain credentials (SEC_E_NO_CREDENTIALS)
        // and fails before receiving a peer certificate. Run the real handshake elsewhere, including Linux CI.
        if (OperatingSystem.IsWindows() && Environment.UserName.StartsWith("CodexSandbox", StringComparison.OrdinalIgnoreCase))
            Skip = "Windows Codex sandbox blocks Schannel credentials before certificate validation; run in normal Windows or Linux CI.";
    }
}
