namespace NzbWebDAV.Tests.TestUtils;

/// <summary>
/// Phase-bounded readable stream: prefix, then immediately available requested
/// bytes, then a gated tail/EOF. A large caller buffer cannot collapse phases.
/// An empty tail produces ungated EOF; tests that need the tail gate must
/// supply a non-empty tail.
/// </summary>
internal sealed class StagedBodyStream : Stream
{
    private readonly byte[] _prefix;
    private readonly byte[] _requested;
    private readonly byte[] _tail;
    private readonly TaskCompletionSource _tailStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _tailRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Func<string, Exception?>? _readFailure;
    private readonly Func<Exception>? _disposeFailure;
    private int _phase;
    private int _offsetInPhase;
    private int _disposed;

    internal StagedBodyStream(
        byte[] prefix,
        byte[] requested,
        byte[] tail,
        Func<string, Exception?>? readFailure = null,
        Func<Exception>? disposeFailure = null)
    {
        _prefix = prefix;
        _requested = requested;
        _tail = tail;
        _readFailure = readFailure;
        _disposeFailure = disposeFailure;
    }

    internal Task TailReadStarted => _tailStarted.Task;
    internal void ReleaseTail() => _tailRelease.TrySetResult();
    internal int ReadCount { get; private set; }
    internal long TotalBytesRead { get; private set; }
    internal int SyncDisposeCount { get; private set; }
    internal int AsyncDisposeCount { get; private set; }
    internal bool TailGateClosed => !_tailRelease.Task.IsCompleted;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => TotalBytesRead;
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (buffer.IsEmpty || Volatile.Read(ref _disposed) != 0)
            return 0;

        ReadCount++;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var phaseName = PhaseName(_phase);
            if (_readFailure?.Invoke(phaseName) is { } failure)
                throw failure;

            var source = CurrentPhaseBytes();
            if (source.Length == 0)
            {
                // Empty tail (phase 2 with no bytes) is ungated EOF. Do not wait
                // on ReleaseTail; empty-tail handoff tests rely on that.
                if (_phase >= 2)
                    return 0;

                _phase++;
                _offsetInPhase = 0;
                continue;
            }

            if (_phase == 2 && _offsetInPhase == 0)
            {
                _tailStarted.TrySetResult();
                await _tailRelease.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            var remaining = source.Length - _offsetInPhase;
            var n = Math.Min(buffer.Length, remaining);
            source.AsSpan(_offsetInPhase, n).CopyTo(buffer.Span);
            _offsetInPhase += n;
            TotalBytesRead += n;
            if (_offsetInPhase >= source.Length)
            {
                _phase++;
                _offsetInPhase = 0;
            }

            return n;
        }
    }

    private byte[] CurrentPhaseBytes() => _phase switch
    {
        0 => _prefix,
        1 => _requested,
        2 => _tail,
        _ => [],
    };

    private static string PhaseName(int phase) => phase switch
    {
        0 => "prefix",
        1 => "requested",
        2 => "tail",
        _ => "eof",
    };

    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            SyncDisposeCount++;
            _tailRelease.TrySetCanceled();
            if (_disposeFailure is not null)
                throw _disposeFailure();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            AsyncDisposeCount++;
            _tailRelease.TrySetCanceled();
            if (_disposeFailure is not null)
                throw _disposeFailure();
        }

        await base.DisposeAsync().ConfigureAwait(false);
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
