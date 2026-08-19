using UsenetSharp.Streams;

namespace NzbWebDAV.Streams;

/// <summary>
/// Async-only token boundary: every read is issued with the construction token
/// (or a link of construction + caller tokens). Synchronous reads are not supported.
/// </summary>
public class CancellableStream(Stream innerStream, CancellationToken token) : FastReadOnlyStream
{
    private readonly Stream _innerStream = innerStream ?? throw new ArgumentNullException(nameof(innerStream));
    private bool _disposed;

    public override bool CanSeek => _innerStream.CanSeek;
    public override long Length => _innerStream.Length;

    public override long Position
    {
        get => _innerStream.Position;
        set => _innerStream.Position = value;
    }

    public override void Flush()
    {
        CheckDisposed();
        _innerStream.Flush();
    }

    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        CheckDisposed();
        return _innerStream.FlushAsync(cancellationToken);
    }

    // Must override: deleting this would fall back to FastReadOnlyStream's
    // CancellationToken.None bridge, which is uncancellable.
    public override int Read(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("CancellableStream supports asynchronous reads only.");

    // Must override: deleting this would fall back to FastReadOnlyStream's
    // CancellationToken.None bridge, which is uncancellable.
    public override int Read(Span<byte> buffer) =>
        throw new NotSupportedException("CancellableStream supports asynchronous reads only.");

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        CheckDisposed();
        token.ThrowIfCancellationRequested();
        return !cancellationToken.CanBeCanceled || cancellationToken == token
            ? _innerStream.ReadAsync(buffer, token)
            : ReadWithLinkedTokenAsync(buffer, cancellationToken);
    }

    private async ValueTask<int> ReadWithLinkedTokenAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token, cancellationToken);
        return await _innerStream.ReadAsync(buffer, linkedCts.Token).ConfigureAwait(false);
    }

    public override void SetLength(long value)
    {
        CheckDisposed();
        _innerStream.SetLength(value);
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        CheckDisposed();
        return _innerStream.Seek(offset, origin);
    }

    private void CheckDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, typeof(CancellableStream));
    }

    protected override void Dispose(bool disposing)
    {
        if (_disposed) return;
        if (!disposing) return;
        _disposed = true;
        _innerStream.Dispose();
        base.Dispose(disposing);
    }
}
