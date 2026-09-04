using System.Buffers;
using System.Diagnostics;
using System.Security.Cryptography;

namespace NzbWebDAV.Benchmarks;

/// <summary>
/// Applies a bounded response-copy shape while discarding output so timed
/// benchmark passes do not include hashing unless explicitly requested.
/// </summary>
internal sealed class HttpLikeCountingSink(
    int bufferBytes,
    long copyStartedTimestamp,
    ArrayPool<byte>? bufferPool = null)
{
    private readonly int _bufferBytes = bufferBytes > 0
        ? bufferBytes
        : throw new ArgumentOutOfRangeException(nameof(bufferBytes));
    private readonly ArrayPool<byte> _bufferPool = bufferPool ?? ArrayPool<byte>.Shared;

    public long BytesWritten { get; private set; }

    // Measured from whole-path execution start so this matches client-observed
    // latency, including stream construction and the first segment fetch.
    public TimeSpan? TimeToFirstByte { get; private set; }

    public async Task<string?> CopyFromAsync(
        Stream source,
        bool verifyHash,
        CancellationToken cancellationToken)
    {
        using var hash = verifyHash ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256) : null;
        var buffer = _bufferPool.Rent(_bufferBytes);
        try
        {
            while (true)
            {
                var read = await source.ReadAsync(buffer.AsMemory(0, _bufferBytes), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                    return hash is null
                        ? null
                        : Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
                TimeToFirstByte ??= Stopwatch.GetElapsedTime(copyStartedTimestamp);
                hash?.AppendData(buffer, 0, read);
                BytesWritten += read;
            }
        }
        finally
        {
            _bufferPool.Return(buffer);
        }
    }
}
