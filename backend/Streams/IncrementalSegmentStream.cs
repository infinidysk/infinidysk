using System.Buffers;
using System.Runtime.ExceptionServices;
using Serilog;
using UsenetSharp.Streams;

namespace NzbWebDAV.Streams;

/// <summary>
/// Exposes a segment body to the reader while it decodes instead of after the whole article
/// drains. A background task copies the source into a pooled buffer and reads return the
/// bytes copied so far. A failure mid-body is handed to <c>recover</c>, whose complete
/// replacement body supplies the bytes not yet copied, so retries, fallbacks, and zero-fill
/// still apply to the remainder.
/// </summary>
internal sealed class IncrementalSegmentStream : FastReadOnlyNonSeekableStream
{
    private const int MinimumCapacity = 64 * 1024;

    private readonly object _gate = new();
    private readonly long _expectedLength;
    private readonly Func<Exception, CancellationToken, Task<Stream>> _recover;
    private readonly Action<long, long> _onCompleted;
    private readonly CancellationTokenSource _cts;
    private readonly Task _fill;
    private byte[] _buffer;
    private int _written;
    private int _position;
    private bool _completed;
    private ExceptionDispatchInfo? _fault;
    private TaskCompletionSource _progress = NewSignal();
    private int _disposed;

    /// <param name="expectedLength">Recorded segment length, or -1 when unknown. Output is
    /// truncated or zero-padded to it.</param>
    /// <param name="onCompleted">Called once with the decoded and final lengths after the
    /// body fully drains; not called when the drain fails or is cancelled.</param>
    public IncrementalSegmentStream(
        Stream source,
        int capacity,
        long expectedLength,
        Func<Exception, CancellationToken, Task<Stream>> recover,
        Action<long, long> onCompleted,
        CancellationToken cancellationToken)
    {
        _expectedLength = expectedLength;
        _recover = recover;
        _onCompleted = onCompleted;
        _buffer = ArrayPool<byte>.Shared.Rent(Math.Max(capacity, MinimumCapacity));
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _fill = Task.Run(() => FillAsync(source, _cts.Token), CancellationToken.None);
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private async Task FillAsync(Stream source, CancellationToken cancellationToken)
    {
        try
        {
            var sourceDisposed = false;
            try
            {
                await CopyAsync(source, 0, cancellationToken).ConfigureAwait(false);
                sourceDisposed = true;
                await source.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception e) when (!cancellationToken.IsCancellationRequested && e is not OutOfMemoryException)
            {
                if (!sourceDisposed)
                    await DisposeQuietlyAsync(source).ConfigureAwait(false);
                sourceDisposed = true;
                Log.Debug(e, "Segment body failed after {Bytes} bytes were delivered; recovering the remainder.", _written);
                var replacement = await _recover(e, cancellationToken).ConfigureAwait(false);
                await using (replacement.ConfigureAwait(false))
                    await CopyAsync(replacement, _written, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (!sourceDisposed && cancellationToken.IsCancellationRequested)
                    await DisposeQuietlyAsync(source).ConfigureAwait(false);
            }

            var drained = _written;
            if (_expectedLength >= 0 && drained < _expectedLength)
                PadTo((int)_expectedLength);
            var length = _expectedLength >= 0 ? _expectedLength : drained;
            _onCompleted(drained, length);
        }
        catch (Exception e)
        {
            lock (_gate) _fault = ExceptionDispatchInfo.Capture(e);
        }
        finally
        {
            lock (_gate) _completed = true;
            Signal();
        }
    }

    /// <summary>Appends the source to the buffer, discarding its first <paramref name="skip"/> bytes.</summary>
    private async Task CopyAsync(Stream source, long skip, CancellationToken cancellationToken)
    {
        while (true)
        {
            if (_written == _buffer.Length)
                Grow(_buffer.Length * 2);
            var read = await source.ReadAsync(_buffer.AsMemory(_written), cancellationToken).ConfigureAwait(false);
            if (read == 0) return;
            if (skip > 0)
            {
                // Replacement bytes already delivered from the failed body are dropped.
                var dropped = (int)Math.Min(skip, read);
                skip -= dropped;
                if (dropped == read) continue;
                _buffer.AsSpan(_written + dropped, read - dropped).CopyTo(_buffer.AsSpan(_written));
                read -= dropped;
            }

            lock (_gate) _written += read;
            Signal();
        }
    }

    private void PadTo(int length)
    {
        if (_buffer.Length < length)
            Grow(length);
        _buffer.AsSpan(_written, length - _written).Clear();
        lock (_gate) _written = length;
    }

    private void Grow(int minimumLength)
    {
        var bigger = ArrayPool<byte>.Shared.Rent(minimumLength);
        lock (_gate)
        {
            _buffer.AsSpan(0, _written).CopyTo(bigger);
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = bigger;
        }
    }

    private void Signal() =>
        Interlocked.Exchange(ref _progress, NewSignal()).TrySetResult();

    private static async Task DisposeQuietlyAsync(Stream stream)
    {
        try
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Log.Debug(e, "Failed to dispose a segment body after its drain stopped.");
        }
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            Task progress;
            lock (_gate)
            {
                var available = _expectedLength >= 0 ? (int)Math.Min(_written, _expectedLength) : _written;
                if (_position < available)
                {
                    var count = Math.Min(buffer.Length, available - _position);
                    _buffer.AsSpan(_position, count).CopyTo(buffer.Span);
                    _position += count;
                    return count;
                }

                if (_completed)
                {
                    _fault?.Throw();
                    return 0;
                }

                progress = Volatile.Read(ref _progress).Task;
            }

            await progress.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public override async ValueTask DisposeAsync()
    {
        await ReleaseAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            ReleaseAsync().GetAwaiter().GetResult();
        base.Dispose(disposing);
    }

    private async Task ReleaseAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _cts.CancelAsync().ConfigureAwait(false);
        // FillAsync records every failure itself, so awaiting it cannot throw.
        await _fill.ConfigureAwait(false);

        lock (_gate)
        {
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = [];
        }

        _cts.Dispose();
    }
}
