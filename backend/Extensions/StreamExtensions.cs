using System.Buffers;
using NzbWebDAV.Streams;

namespace NzbWebDAV.Extensions;

public static class StreamExtensions
{
    public static Stream LimitLength(this Stream stream, long length)
    {
        return new LimitedLengthStream(stream, length);
    }

    public static async Task DiscardBytesAsync(this Stream stream, long count, CancellationToken ct = default)
    {
        await DiscardBytesAsync(stream, count, requireExact: false, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Discards exactly <paramref name="count"/> bytes, throwing when the stream
    /// ends first. Use this whenever the discarded prefix positions a stream at a
    /// requested byte offset: a partial discard leaves the stream at an offset that
    /// does not match what the caller believes it is reading.
    /// </summary>
    public static async Task DiscardExactBytesAsync(this Stream stream, long count, CancellationToken ct = default)
    {
        await DiscardBytesAsync(stream, count, requireExact: true, ct).ConfigureAwait(false);
    }

    private static async Task DiscardBytesAsync(
        Stream stream,
        long count,
        bool requireExact,
        CancellationToken ct)
    {
        if (count == 0) return;
        var remaining = count;
        var throwaway = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            while (remaining > 0)
            {
                var toRead = (int)Math.Min(remaining, throwaway.Length);
                var read = await stream.ReadAsync(throwaway.AsMemory(0, toRead), ct).ConfigureAwait(false);
                if (read == 0) break;
                remaining -= read;
            }

            if (requireExact && remaining > 0)
            {
                throw new EndOfStreamException(
                    $"Stream ended {remaining} bytes before {count} bytes could be skipped.");
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(throwaway);
        }
    }

    public static Stream OnDispose(this Stream stream, Action onDispose)
    {
        return new DisposableCallbackStream(stream, onDispose, async () => onDispose?.Invoke());
    }

    public static Stream OnDisposeAsync(this Stream stream, Func<ValueTask> onDisposeAsync)
    {
        return new DisposableCallbackStream(stream, onDisposeAsync: onDisposeAsync);
    }
}
