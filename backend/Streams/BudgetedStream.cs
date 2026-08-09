namespace NzbWebDAV.Streams;

/// <summary>
/// Wraps a drained segment buffer and releases its <see cref="ArticleByteLease"/>
/// exactly once when disposed.
/// </summary>
public sealed class BudgetedStream : Stream
{
    private readonly Stream _inner;
    private ArticleByteLease? _lease;

    public BudgetedStream(Stream inner, ArticleByteLease lease)
    {
        _inner = inner;
        _lease = lease;
    }

    public override bool CanRead => true;
    public override bool CanSeek => _inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => _inner.Length;

    public override long Position
    {
        get => _inner.Position;
        set => _inner.Position = value;
    }

    public ArticleByteLease? Lease => _lease;

    public override void Flush() => _inner.Flush();

    public override int Read(byte[] buffer, int offset, int count) =>
        _inner.Read(buffer, offset, count);

    public override int Read(Span<byte> buffer) => _inner.Read(buffer);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        _inner.ReadAsync(buffer, offset, count, cancellationToken);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        _inner.ReadAsync(buffer, cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
            ReleaseLease();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await _inner.DisposeAsync().ConfigureAwait(false);
        ReleaseLease();
        GC.SuppressFinalize(this);
        await base.DisposeAsync().ConfigureAwait(false);
    }

    private void ReleaseLease()
    {
        var lease = Interlocked.Exchange(ref _lease, null);
        lease?.Dispose();
    }
}
