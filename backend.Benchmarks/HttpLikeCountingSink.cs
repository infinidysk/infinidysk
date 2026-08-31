using System.Buffers;

namespace NzbWebDAV.Benchmarks;

/// <summary>
/// Applies the bounded 64 KiB response-copy shape used by the WebDAV handler,
/// while discarding output so timed benchmark passes do not include hashing.
/// </summary>
internal sealed class HttpLikeCountingSink
{
    private const int BufferBytes = 64 * 1024;

    public long BytesWritten { get; private set; }

    public async Task CopyFromAsync(Stream source, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(BufferBytes);
        try
        {
            while (true)
            {
                var read = await source.ReadAsync(buffer.AsMemory(0, BufferBytes), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                    return;
                BytesWritten += read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
