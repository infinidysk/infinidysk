using System.Diagnostics;
using System.Runtime.ExceptionServices;
using NzbWebDAV.Clients.Usenet.Contexts;
using Serilog;
using UsenetSharp.Streams;

namespace NzbWebDAV.Streams;

/// <param name="Stream">Complete, validated replacement body.</param>
/// <param name="Degraded">The replacement is a gap fill or a short-padded body.</param>
/// <param name="GapFillFailure">Set when the replacement zero-fills an unrecoverable article.</param>
internal readonly record struct SegmentReplacement(Stream Stream, bool Degraded, Exception? GapFillFailure = null);

internal interface IIncrementalSegmentHandler
{
    /// <summary>Fetches a validated full replacement after the incremental body failed.</summary>
    Task<SegmentReplacement> RecoverAsync(Exception failure, CancellationToken cancellationToken);

    /// <summary>Failure surfaced when bytes the reader already took differ from the replacement.</summary>
    Exception CreatePostDeliveryFailure(Exception failure, int deliveredBytes);

    /// <summary>
    /// Runs on the reader exactly once: before the first degraded byte, or at a clean end.
    /// Throwing stops delivery of the segment.
    /// </summary>
    void OnConsumed(bool degraded);

    /// <summary>Runs on the reader after it waited for body bytes that had not arrived.</summary>
    void OnReaderWaited(TimeSpan elapsed);
}

/// <summary>
/// Exposes a segment body to the reader while it decodes instead of after the whole article
/// drains. The body is copied into a pooled buffer and reads return the bytes copied so far.
/// When the body fails, bytes the reader has not taken are discarded and refilled from a
/// validated replacement; bytes it already took must match that replacement, otherwise the
/// read fails rather than splicing two different bodies.
/// </summary>
internal sealed class IncrementalSegmentStream : FastReadOnlyNonSeekableStream
{
    private const int MinimumCapacity = 64 * 1024;

    private readonly object _gate = new();
    private readonly long _expectedLength;
    private readonly IIncrementalSegmentHandler _handler;
    private readonly Action<long, long> _onDrained;
    private readonly ISegmentBufferPool _pool;
    private readonly BufferPoolDiagnostics _diagnostics;
    private readonly ContextualCancellationTokenSource _cts;
    private readonly Task _fill;
    private byte[] _buffer;
    private int _written;
    private int _position;
    private int _degradedFrom = -1;
    private bool _outcomeReported;
    private bool _completed;
    private ExceptionDispatchInfo? _fault;
    private ExceptionDispatchInfo? _rejection;
    private TaskCompletionSource _progress = NewSignal();
    private int _disposed;

    /// <param name="expectedLength">Recorded segment length, or -1 when unknown. Output is
    /// truncated or zero-padded to it.</param>
    /// <param name="onDrained">Called once with the decoded and final lengths after the
    /// body fully drains; not called when the drain fails or is cancelled.</param>
    public IncrementalSegmentStream(
        Stream source,
        int capacity,
        long expectedLength,
        IIncrementalSegmentHandler handler,
        Action<long, long> onDrained,
        CancellationToken cancellationToken,
        ISegmentBufferPool? pool = null,
        BufferPoolDiagnostics? diagnostics = null)
    {
        _expectedLength = expectedLength;
        _handler = handler;
        _onDrained = onDrained;
        _pool = pool ?? PooledBufferStream.DefaultPool;
        _diagnostics = diagnostics ?? BufferPoolDiagnostics.Shared;
        var initial = Math.Max(capacity, MinimumCapacity);
        _buffer = _pool.Rent(initial);
        _diagnostics.RecordRent(initial, _buffer.Length);
        // Keeps playback priority and timeout contexts on recovery requests.
        _cts = ContextualCancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // Started inline so bytes that are already decoded are readable on return.
        _fill = FillAsync(source, _cts.Token);
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
                await RecoverAsync(e, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (!sourceDisposed)
                    await DisposeQuietlyAsync(source).ConfigureAwait(false);
            }

            var drained = _written;
            if (_expectedLength >= 0 && drained < _expectedLength)
                PadTo((int)_expectedLength);
            _onDrained(drained, _expectedLength >= 0 ? _expectedLength : drained);
        }
        catch (Exception e)
        {
            lock (_gate)
            {
                _fault = ExceptionDispatchInfo.Capture(e);
                _written = Math.Min(_written, _position);
            }
        }
        finally
        {
            lock (_gate) _completed = true;
            Signal();
        }
    }

    private async Task RecoverAsync(Exception failure, CancellationToken cancellationToken)
    {
        int delivered;
        int rejected;
        lock (_gate)
        {
            delivered = _position;
            rejected = _written - delivered;
            _written = delivered;
        }

        Log.Debug(
            failure,
            "Segment body failed after the reader took {Delivered} bytes; discarding {Rejected} unread bytes and recovering.",
            delivered, rejected);
        var replacement = await _handler.RecoverAsync(failure, cancellationToken).ConfigureAwait(false);
        await using (replacement.Stream.ConfigureAwait(false))
        {
            if (delivered > 0 && replacement.GapFillFailure is { } gap)
                ExceptionDispatchInfo.Capture(gap).Throw();
            if (replacement.Degraded)
                lock (_gate) _degradedFrom = delivered;
            if (!await CopyAsync(replacement.Stream, delivered, cancellationToken).ConfigureAwait(false))
                throw _handler.CreatePostDeliveryFailure(failure, delivered);
        }
    }

    /// <summary>
    /// Appends the source to the buffer. Its first <paramref name="verify"/> bytes were already
    /// delivered, so they are compared instead of appended.
    /// </summary>
    /// <returns>False when those leading bytes differ from what was delivered.</returns>
    private async Task<bool> CopyAsync(Stream source, int verify, CancellationToken cancellationToken)
    {
        var verified = 0;
        while (true)
        {
            if (_written == _buffer.Length)
                Grow(_buffer.Length + 1);
            var read = await source.ReadAsync(_buffer.AsMemory(_written), cancellationToken).ConfigureAwait(false);
            if (read == 0) return verified == verify;
            if (verified < verify)
            {
                var compared = Math.Min(verify - verified, read);
                if (!_buffer.AsSpan(_written, compared).SequenceEqual(_buffer.AsSpan(verified, compared)))
                    return false;
                verified += compared;
                if (compared == read) continue;
                read -= compared;
                _buffer.AsSpan(_written + compared, read).CopyTo(_buffer.AsSpan(_written));
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
        lock (_gate)
        {
            if (_degradedFrom < 0)
                _degradedFrom = _written;
            _written = length;
        }
    }

    private void Grow(int minimumLength)
    {
        var current = _buffer;
        var target = (int)Math.Min(
            Math.Max((long)minimumLength, current.Length + (long)current.Length / 2),
            Array.MaxLength);
        var bigger = _pool.Rent(target);
        lock (_gate)
        {
            current.AsSpan(0, _written).CopyTo(bigger);
            _buffer = bigger;
        }

        _pool.Return(current);
        _diagnostics.RecordGrowth(minimumLength, current.Length, bigger.Length);
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
            Task? progress = null;
            bool degraded;
            lock (_gate)
            {
                _rejection?.Throw();
                var available = _expectedLength >= 0 ? (int)Math.Min(_written, _expectedLength) : _written;
                var limit = _outcomeReported || _degradedFrom < 0 ? available : Math.Min(available, _degradedFrom);
                if (_position < limit)
                {
                    var count = Math.Min(buffer.Length, limit - _position);
                    _buffer.AsSpan(_position, count).CopyTo(buffer.Span);
                    _position += count;
                    return count;
                }

                // Reached the first degraded byte or a clean end: report before going further.
                degraded = _degradedFrom >= 0;
                if (!_outcomeReported && (degraded || (_completed && _fault is null)))
                    _outcomeReported = true;
                else if (_completed)
                {
                    _fault?.Throw();
                    return 0;
                }
                else
                    progress = Volatile.Read(ref _progress).Task;
            }

            if (progress is null)
            {
                ReportOutcome(degraded);
                continue;
            }

            var waitStarted = Stopwatch.GetTimestamp();
            await progress.WaitAsync(cancellationToken).ConfigureAwait(false);
            _handler.OnReaderWaited(Stopwatch.GetElapsedTime(waitStarted));
        }
    }

    private void ReportOutcome(bool degraded)
    {
        try
        {
            _handler.OnConsumed(degraded);
        }
        catch (Exception e)
        {
            lock (_gate) _rejection = ExceptionDispatchInfo.Capture(e);
            throw;
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
            _diagnostics.RecordReturn(_buffer.Length);
            _pool.Return(_buffer);
            _buffer = [];
        }

        _cts.Dispose();
    }
}
