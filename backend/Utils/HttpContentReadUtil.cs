using NzbWebDAV.Exceptions;

namespace NzbWebDAV.Utils;

public static class HttpContentReadUtil
{
    private const int BufferSize = 81920;

    public static async Task<byte[]> ReadBoundedAsync(
        HttpContent content,
        long maxBytes,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);

        var declared = content.Headers.ContentLength;
        if (declared is { } length && length > maxBytes)
            throw new NzbResponseTooLargeException(maxBytes, length);

        await using var input = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var ms = new MemoryStream(declared is { } known && known >= 0 && known <= int.MaxValue
            ? (int)known
            : 0);
        var buf = new byte[BufferSize];
        int read;
        while (true)
        {
            var remaining = maxBytes - ms.Length;
            var toRead = remaining >= buf.Length ? buf.Length : (int)(remaining + 1);
            read = await input.ReadAsync(buf.AsMemory(0, toRead), ct).ConfigureAwait(false);
            if (read == 0)
                break;
            if (ms.Length + read > maxBytes)
                throw new NzbResponseTooLargeException(maxBytes);
            await ms.WriteAsync(buf.AsMemory(0, read), ct).ConfigureAwait(false);
        }

        return ms.ToArray();
    }
}
