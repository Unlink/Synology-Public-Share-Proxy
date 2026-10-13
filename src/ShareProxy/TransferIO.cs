using System.Net;

namespace ShareProxy;

public static class TransferIO
{
    // Bounded copying with backpressure; a stalled source OR destination cancels the transfer.
    public static async Task<long> CopyAsync(Stream source, Stream destination, long limit, int idleSeconds,
        CancellationToken ct, bool exactLength = false, Action<long>? progress = null)
    {
        var buffer = new byte[64 * 1024];
        long total = 0;
        while (true)
        {
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
            idle.CancelAfter(TimeSpan.FromSeconds(idleSeconds));
            var read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, limit - total + 1)), idle.Token);
            if (read == 0) break;
            total += read;
            if (total > limit) throw new ProxyException(413, "transfer_too_large");
            await destination.WriteAsync(buffer.AsMemory(0, read), idle.Token);
            progress?.Invoke(total);
        }
        if (exactLength && total != limit) throw new ProxyException(400, "size_mismatch");
        return total;
    }
}

public sealed class UploadContent(Stream source, long length, int idleSeconds) : HttpContent
{
    private long bytesSent;
    public long BytesSent => Interlocked.Read(ref bytesSent);
    protected override bool TryComputeLength(out long result) { result = length; return true; }
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => SerializeToStreamAsync(stream, context, CancellationToken.None);
    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        => await TransferIO.CopyAsync(source, stream, length, idleSeconds, cancellationToken, true, total => Interlocked.Exchange(ref bytesSent, total));
}
