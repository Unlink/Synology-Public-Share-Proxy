using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;
using ShareProxy;
using Xunit;

namespace ShareProxy.Tests;

public sealed class NetworkUploadTests
{
    [Fact]
    public async Task UnbufferedUploadHasBrowserMultipartOnActualHttpConnection()
    {
        const string token = "networkSyntheticShare123";
        const string filename = "Žiadosť.txt";
        var payload = Enumerable.Range(0, 70001).Select(i => (byte)(i % 251)).ToArray();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var origin = new Uri("http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port);
        var server = Task.Run(async () =>
        {
            for (var operation = 0; operation < 5; operation++)
            {
                using var socket = await listener.AcceptTcpClientAsync(timeout.Token);
                await using var stream = socket.GetStream();
                var headerBytes = new List<byte>(); var single = new byte[1];
                while (headerBytes.Count < 16384)
                {
                    await stream.ReadExactlyAsync(single, timeout.Token); headerBytes.Add(single[0]);
                    if (headerBytes.Count >= 4 && headerBytes.TakeLast(4).SequenceEqual(new byte[] { 13, 10, 13, 10 })) break;
                }
                var lines = Encoding.ASCII.GetString(headerBytes.ToArray()).Split("\r\n");
                var request = lines[0].Split(' ');
                var headers = lines.Skip(1).Where(l => l.Contains(':')).Select(l => l.Split(':', 2))
                    .ToDictionary(p => p[0], p => p[1].Trim(), StringComparer.OrdinalIgnoreCase);
                Assert.False(headers.ContainsKey("Transfer-Encoding"));
                var length = headers.TryGetValue("Content-Length", out var number) ? int.Parse(number) : 0;
                Assert.InRange(length, 0, 100_000);
                var body = new byte[length]; await stream.ReadExactlyAsync(body, timeout.Token);
                string response;
                if (operation == 0)
                    response = "<script src=\"webapi/entry.cgi?api=SYNO.Core.Sharing.Session&amp;sharing_id=&quot;" + token + "&quot;&amp;sharing_status=&quot;none&quot;\"></script>";
                else if (operation == 1)
                    response = "SYNO.SDS.Session = " + JsonSerializer.Serialize(new { sharing = true, sharing_id = token, isLogined = false })
                        + ";SYNO.SDS.ExtraSession = {\"status\":0};";
                else if (operation == 2)
                    response = JsonSerializer.Serialize(new { success = true, data = new { Sharing = new { enabled = true, protect_type = "none", redirect_type = "none",
                        app = new { enable_upload = true, is_folder = true, request_name = "Upload" } } } });
                else if (operation == 3) response = "{\"success\":true,\"data\":{}}";
                else
                {
                    Assert.StartsWith("/webapi/entry.cgi?", request[1]);
                    Assert.Equal(origin.GetLeftPart(UriPartial.Authority), headers["Origin"]);
                    Assert.Equal(new Uri(origin, "/sharing/" + token).AbsoluteUri, headers["Referer"]);
                    Assert.False(headers.ContainsKey("X-SYNO-SHARING"));
                    var query = QueryHelpers.ParseQuery(new Uri(origin, request[1]).Query);
                    Assert.Equal(token, query["_sharing_id"].ToString());
                    Assert.DoesNotContain("%22", request[1]);
                    var contentType = MediaTypeHeaderValue.Parse(headers["Content-Type"]);
                    Assert.False(contentType.Boundary.Value!.StartsWith('"'));
                    var rawFilePart = Encoding.UTF8.GetString(body).Split("--" + contentType.Boundary.Value)[^2];
                    Assert.StartsWith("\r\nContent-Disposition: form-data; name=\"file\"; filename=\"" + filename + "\"\r\nContent-Type:", rawFilePart);
                    var reader = new MultipartReader(contentType.Boundary.Value, new MemoryStream(body));
                    var names = new List<string>();
                    while (await reader.ReadNextSectionAsync(timeout.Token) is { } section)
                    {
                        var disposition = ContentDispositionHeaderValue.Parse(section.ContentDisposition);
                        var name = HeaderUtilities.RemoveQuotes(disposition.Name).Value!; names.Add(name);
                        if (name == "file")
                        {
                            Assert.Equal(filename, HeaderUtilities.RemoveQuotes(disposition.FileName).Value);
                            Assert.Equal(2, section.Headers!.Count); // Browser: Content-Disposition and Content-Type only.
                            using var actual = new MemoryStream(); await section.Body.CopyToAsync(actual, timeout.Token);
                            Assert.Equal(payload, actual.ToArray());
                        }
                        else
                        {
                            Assert.Single(section.Headers!); // No implicit per-field length or content type.
                            using var value = new StreamReader(section.Body, Encoding.UTF8);
                            var text = await value.ReadToEndAsync(timeout.Token);
                            if (name == "size") Assert.Equal(payload.Length.ToString(), text);
                            if (name == "sharing_id") Assert.Equal(token, text);
                            if (name == "overwrite") Assert.Equal("false", text);
                        }
                    }
                    Assert.Equal(new[] { "overwrite", "mtime", "sharing_id", "uploader_name", "size", "file" }, names);
                    response = "{\"success\":true}";
                }
                var result = Encoding.UTF8.GetBytes(response);
                var responseHeader = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: " + result.Length + "\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(responseHeader, timeout.Token); await stream.WriteAsync(result, timeout.Token);
            }
        }, timeout.Token);
        using var handler = new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { BaseAddress = origin, Timeout = Timeout.InfiniteTimeSpan };
        using var gateway = new DsmGateway(client, new ProxyOptions(), token);
        using var source = new NonSeekable(payload);
        await gateway.UploadAsync(source, payload.Length, filename, "Visitor", timeout.Token);
        await server;
    }

    private sealed class NonSeekable(byte[] bytes) : Stream
    {
        private readonly MemoryStream source = new(bytes);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => source.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => source.ReadAsync(buffer, ct);
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) source.Dispose(); base.Dispose(disposing); }
    }
}
