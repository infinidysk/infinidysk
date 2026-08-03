namespace UsenetSharpTest.Support;

/// <summary>
/// Test transport that can block, fail, or complete writes so mid-write
/// session poisoning can be asserted without a real TCP backpressure stall.
/// </summary>
internal sealed class ControllableWriteStream : Stream
{
    public enum WriteMode
    {
        Complete,
        BlockUntilCancelled,
        ThrowIOException
    }

    private readonly TaskCompletionSource _writeEntered = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private WriteMode _mode = WriteMode.Complete;

    public Task WriteEntered => _writeEntered.Task;

    public void SetMode(WriteMode mode) => _mode = mode;

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) =>
        throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        WriteAsync(buffer.AsMemory(offset, count), CancellationToken.None)
            .AsTask()
            .GetAwaiter()
            .GetResult();

    public override async ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        _writeEntered.TrySetResult();
        switch (_mode)
        {
            case WriteMode.BlockUntilCancelled:
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
                    .ConfigureAwait(false);
                break;
            case WriteMode.ThrowIOException:
                throw new IOException("Simulated write failure.");
            case WriteMode.Complete:
                break;
            default:
                throw new InvalidOperationException($"Unknown write mode: {_mode}");
        }
    }
}
