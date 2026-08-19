using System.Diagnostics;
using System.Runtime.ExceptionServices;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Services.StreamTrace;
using UsenetSharp.Streams;

namespace NzbWebDAV.Streams;

/// <summary>
/// Per-reader seekable view of a <see cref="SharedStreamEntry"/> ring. Out-of-window
/// seeks and tail-pinning evictions detach to a private fallback at the exact cursor.
/// </summary>
internal sealed class SharedReaderStream : FastReadOnlyStream
{
    private readonly SharedStreamEntry _entry;
    private readonly SharedStreamRingBuffer _ring;
    private readonly long _readerId;
    private readonly long _fileSize;
    private readonly long _ringSize;
    private readonly SharedStreamFallbackFactory _fallbackFactory;
    private long _cursor;
    private Stream? _fallback;
    private bool _detached;
    private Exception? _deliveredFailure;
    private int _disposed;

    internal SharedReaderStream(
        SharedStreamEntry entry,
        SharedStreamRingBuffer ring,
        long readerId,
        long cursor,
        long fileSize,
        long ringSize,
        SharedStreamFallbackFactory fallbackFactory)
    {
        _entry = entry;
        _ring = ring;
        _readerId = readerId;
        _cursor = cursor;
        _fileSize = fileSize;
        _ringSize = ringSize;
        _fallbackFactory = fallbackFactory;
    }

    internal long ReaderId => _readerId;
    internal bool IsDetached => _detached;
    internal long Cursor => _cursor;

    public override bool CanSeek => true;
    public override long Length => _fileSize;

    public override long Position
    {
        get => _cursor;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override void Flush()
    {
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (buffer.IsEmpty) return 0;
        if (_deliveredFailure is { } delivered)
            throw DuplicateFailure(delivered);
        if (_cursor >= _fileSize) return 0;

        if (_detached)
            return await ReadFallbackAsync(buffer, cancellationToken).ConfigureAwait(false);

        while (true)
        {
            var result = _ring.TryCopyAt(_readerId, _cursor, buffer.Span);
            switch (result.Kind)
            {
                case RingReadKind.Copied:
                    _cursor += result.Count;
                    if (result.Count > 0)
                        _entry.NotifyCursorAdvanced(_readerId, _cursor);
                    return result.Count;

                case RingReadKind.NeedWait:
                {
                    var waitStarted = Stopwatch.GetTimestamp();
                    await _ring.WaitForDataAsync(_readerId, _cursor, cancellationToken)
                        .ConfigureAwait(false);
                    StreamTrace.TryStall(
                        MultiProviderNntpClient.CurrentStreamTraceRange,
                        StreamStallKind.ConsumerWait,
                        Stopwatch.GetElapsedTime(waitStarted));
                    continue;
                }

                case RingReadKind.Evicted:
                case RingReadKind.Released:
                case RingReadKind.Detached:
                    await DetachToPrivateAsync(cancellationToken).ConfigureAwait(false);
                    return await ReadFallbackAsync(buffer, cancellationToken).ConfigureAwait(false);

                case RingReadKind.Failed:
                    _deliveredFailure = result.Exception;
                    DetachQuiet();
                    throw result.DispatchFailure();

                default:
                    throw new InvalidOperationException($"Unexpected ring read kind {result.Kind}.");
            }
        }
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        long target;
        try
        {
            target = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => checked(_cursor + offset),
                SeekOrigin.End => checked(_fileSize + offset),
                _ => throw new ArgumentOutOfRangeException(nameof(origin), origin, "Invalid seek origin.")
            };
        }
        catch (OverflowException)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), offset, "Seek position is outside stream bounds.");
        }

        if (target < 0 || target > _fileSize)
            throw new ArgumentOutOfRangeException(nameof(offset), offset, "Seek position is outside stream bounds.");

        if (_detached)
        {
            _cursor = target;
            _fallback?.Seek(target, SeekOrigin.Begin);
            return _cursor;
        }

        var tail = _ring.TailStart;
        var frontier = _ring.Frontier;
        if (target == _fileSize || (target >= tail && target <= frontier + _ringSize))
        {
            _cursor = target;
            _entry.NotifyCursorAdvanced(_readerId, target);
            return _cursor;
        }

        DetachQuiet();
        _cursor = target;
        return _cursor;
    }

    protected override void Dispose(bool disposing)
    {
        if (!disposing) return;
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        DetachQuiet();
        var fallback = _fallback;
        _fallback = null;
        fallback?.Dispose();
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        DetachQuiet();
        if (_fallback is { } fallback)
        {
            _fallback = null;
            await fallback.DisposeAsync().ConfigureAwait(false);
        }

        GC.SuppressFinalize(this);
        await base.DisposeAsync().ConfigureAwait(false);
    }

    private static Exception DuplicateFailure(Exception delivered)
    {
        ExceptionDispatchInfo.Capture(delivered).Throw();
        return delivered;
    }

    private void DetachQuiet()
    {
        if (_detached) return;
        _detached = true;
        _entry.Detach(_readerId);
    }

    private async Task DetachToPrivateAsync(CancellationToken cancellationToken)
    {
        DetachQuiet();
        _fallback ??= await _fallbackFactory(_cursor, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<int> ReadFallbackAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        _fallback ??= await _fallbackFactory(_cursor, cancellationToken).ConfigureAwait(false);
        var read = await _fallback.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        _cursor += read;
        return read;
    }
}
