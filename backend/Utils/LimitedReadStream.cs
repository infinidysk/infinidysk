namespace NzbWebDAV.Utils;

/// <summary>
/// Bounds the total bytes read from an inner stream. The first read that would
/// exceed <paramref name="maximumBytes"/> throws the exception produced by
/// <paramref name="createLimitException"/>, so callers control whether a limit
/// trip surfaces as a user-facing validation error or an internal failure.
/// </summary>
internal sealed class LimitedReadStream(
    Stream inner,
    long maximumBytes,
    Func<Exception> createLimitException) : Stream
{
    private long _read;

    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => _read; set => throw new NotSupportedException(); }
    public override void Flush() => throw new NotSupportedException();
    public override int Read(byte[] buffer, int offset, int count)
    {
        // Validate the caller's original arguments before capping: an out-of-range
        // offset/count must throw, not shrink into a valid smaller read.
        ValidateBufferArguments(buffer, offset, count);
        return Count(inner.Read(buffer, offset, CapCount(count)));
    }
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
        ReadAndCountAsync(buffer[..CapCount(buffer.Length)], ct);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    // Bound each inner read to the remaining allowance plus one byte: an exact-limit
    // payload still reaches EOF, while an oversize source trips the limit after a
    // single extra byte instead of consuming a full caller buffer past the limit.
    private int CapCount(int requested)
    {
        var remaining = maximumBytes - _read;
        return remaining >= requested ? requested : (int)(remaining + 1);
    }

    private int Count(int read)
    {
        if (read > 0 && _read > maximumBytes - read)
            throw createLimitException();
        _read += read;
        return read;
    }

    private async ValueTask<int> ReadAndCountAsync(Memory<byte> buffer, CancellationToken ct) =>
        Count(await inner.ReadAsync(buffer, ct).ConfigureAwait(false));
}
