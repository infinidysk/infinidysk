namespace UsenetSharpTest.Support;

internal sealed class FragmentedReadStream(
    ReadOnlyMemory<byte> source,
    IReadOnlyList<int> fragmentSizes,
    int? cancelOnRead = null) : Stream
{
    private readonly int[] _fragmentSizes = ValidateFragmentSizes(fragmentSizes);
    private readonly TaskCompletionSource? _refillStarted = cancelOnRead is null
        ? null
        : new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _offset;
    private int _fragmentIndex;
    private int _readCount;

    public int ReadCallCount => Volatile.Read(ref _readCount);
    public int SourceOffset => _offset;
    public Task RefillStarted => _refillStarted?.Task ?? Task.CompletedTask;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        var readNumber = Interlocked.Increment(ref _readCount);
        if (cancelOnRead == readNumber)
        {
            _refillStarted?.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (_offset >= source.Length)
        {
            return 0;
        }

        var fragment = _fragmentIndex < _fragmentSizes.Length
            ? _fragmentSizes[_fragmentIndex]
            : _fragmentSizes[^1];
        _fragmentIndex++;
        var take = Math.Min(Math.Min(fragment, buffer.Length), source.Length - _offset);
        source.Span.Slice(_offset, take).CopyTo(buffer.Span);
        _offset += take;
        return take;
    }

    public override void Flush() => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) =>
        throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    private static int[] ValidateFragmentSizes(IReadOnlyList<int> fragmentSizes)
    {
        ArgumentNullException.ThrowIfNull(fragmentSizes);
        if (fragmentSizes.Count == 0)
        {
            throw new ArgumentException(
                "At least one fragment size is required.", nameof(fragmentSizes));
        }

        var copy = new int[fragmentSizes.Count];
        for (var index = 0; index < fragmentSizes.Count; index++)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fragmentSizes[index]);
            copy[index] = fragmentSizes[index];
        }

        return copy;
    }
}
