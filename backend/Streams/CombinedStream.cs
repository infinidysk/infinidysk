using System.Buffers;
using System.Diagnostics;
using NzbWebDAV.Clients.Usenet.Contexts;
using NzbWebDAV.Services.StreamTrace;
using UsenetSharp.Streams;

namespace NzbWebDAV.Streams;

/// <param name="readAheadBytes">
/// When positive, opens the next stream and reads its first bytes once a
/// <see cref="PaddedLengthStream"/> part has this many bytes or fewer left, so the next
/// part's download pipeline is running before the boundary. Further parts open while
/// every byte ahead of the reader still fits the window, so short parts never shrink it.
/// A part with a <see cref="SpeculativeReadAhead"/> prefetches only what the window has left
/// in front of it, so the queued parts together stay within one window.
/// </param>
public class CombinedStream(IEnumerable<Task<Stream>> streams, long readAheadBytes = 0) : FastReadOnlyNonSeekableStream, IDeliveredBytesValidation
{
    private const int PrimeBufferSize = 64 * 1024;

    private readonly IEnumerator<Task<Stream>> _streams = streams.GetEnumerator();
    // Guards _streams, _nextParts, _lastScheduled and _isDisposed: a finished preparation
    // refills the queue from a thread-pool continuation while the reader may be opening parts.
    private readonly Lock _gate = new();
    private readonly Queue<PendingPart> _nextParts = new();
    private PendingPart? _lastScheduled;
    private readonly ReadAheadCursor _cursor = new();
    private long _window;
    private Stream? _currentStream;
    private ContextualCancellationTokenSource? _prefetchCts;
    private byte[]? _primed;
    private int _primedOffset;
    private int _primedCount;
    private long _position;
    private bool _isDisposed;

    private sealed record PreparedPart(Stream Stream, byte[]? Primed, int PrimedCount);

    // Start is the combined-stream offset of the part's first byte.
    private sealed record PendingPart(long Start, Task<PreparedPart?> Prepared);

    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.Length == 0) return 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_currentStream == null && !await OpenNextAsync().ConfigureAwait(false)) return 0;

            if (_primedCount > 0)
            {
                var count = Math.Min(_primedCount, buffer.Length);
                _primed.AsMemory(_primedOffset, count).CopyTo(buffer);
                _primedOffset += count;
                _primedCount -= count;
                if (_primedCount == 0) ReturnPrimed();
                OnDelivered(count, cancellationToken);
                return count;
            }

            var readCount = await _currentStream!.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (readCount > 0)
            {
                OnDelivered(readCount, cancellationToken);
                return readCount;
            }

            await _currentStream.DisposeAsync().ConfigureAwait(false);
            _currentStream = null;
        }
    }

    // Every delivered byte, primed or not, frees window for parts opened ahead of the reader.
    private void OnDelivered(int count, CancellationToken cancellationToken)
    {
        _position += count;
        _cursor.Advance(_position);
        if (_currentStream is not PaddedLengthStream current) return;
        var window = current.ReadAheadBytes > 0 ? current.ReadAheadBytes : readAheadBytes;
        Volatile.Write(ref _window, window);
        if (window <= 0) return;

        lock (_gate)
        {
            if (_isDisposed) return;
            if (_nextParts.Count > 0)
            {
                TryScheduleAfterLastLocked(_position, window, cancellationToken);
                return;
            }

            var nextStart = _position + _primedCount + (current.Length - current.Position);
            // Next-part leases must never take credits the current tail still needs.
            // Unknown inner wrappers report false, declining prefetch.
            if (nextStart - _position <= window && ((ISegmentIssueProgress)current).AllSegmentsIssued)
                ScheduleLocked(nextStart, window, cancellationToken);
        }
    }

    // A finished preparation may open the next part without waiting for another read.
    private void RefillAfterPreparation()
    {
        lock (_gate)
        {
            if (_isDisposed || _nextParts.Count == 0) return;
            TryScheduleAfterLastLocked(_cursor.Position, Volatile.Read(ref _window), CancellationToken.None);
        }
    }

    private void TryScheduleAfterLastLocked(long readerPosition, long window, CancellationToken cancellationToken)
    {
        // Wait for a part still opening; a finished sequence or foreign stream ends the chain.
        if (window <= 0
            || _lastScheduled is not { Prepared.IsCompletedSuccessfully: true } last
            || last.Prepared.Result?.Stream is not PaddedLengthStream stream)
            return;

        var nextStart = last.Start + stream.Length;
        // Next-part leases must never take credits an earlier part still needs.
        if (nextStart - readerPosition <= window && ((ISegmentIssueProgress)stream).AllSegmentsIssued)
            ScheduleLocked(nextStart, window, cancellationToken);
    }

    private void ScheduleLocked(long start, long window, CancellationToken cancellationToken)
    {
        // No reader waits on this part yet, so its opening and priming are background preparation.
        // The scope only restores an AsyncLocal; started tasks keep the flag through their captured context.
        using var background = StreamTrace.BeginBackground();
        Task<Stream> opening;
        try
        {
            if (!_streams.MoveNext()) return;
            opening = _streams.Current;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Surfaces at the boundary, after every current-part byte.
            opening = Task.FromException<Stream>(e);
        }

        // Owned by the stream, not the triggering read: only disposal cancels prefetch.
        _prefetchCts ??= ContextualCancellationTokenSource.CreateWithContextsOf(cancellationToken);
#pragma warning disable CA2025 // the background scope only restores an AsyncLocal, which the task already captured
        var pending = new PendingPart(start, PrepareNextAsync(opening, start, window, _prefetchCts.Token));
#pragma warning restore CA2025
        _nextParts.Enqueue(pending);
        _lastScheduled = pending;
        _ = pending.Prepared.ContinueWith(
            static (_, state) => ((CombinedStream)state!).RefillAfterPreparation(),
            this,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnRanToCompletion,
            TaskScheduler.Default);
    }

    private async ValueTask<bool> OpenNextAsync()
    {
        PendingPart? pending;
        Task<Stream>? opening = null;
        lock (_gate)
        {
            if (!_nextParts.TryDequeue(out pending) && _streams.MoveNext())
                opening = _streams.Current;
        }

        // A failed prefetch surfaces here, at the boundary, after every current-part byte.
        var phase = pending is null ? "cold" : pending.Prepared.IsCompleted ? "prepared" : "preparing";
        var waitStarted = Stopwatch.GetTimestamp();
        var next = pending is not null
            ? await pending.Prepared.ConfigureAwait(false)
            : opening is not null
                ? new PreparedPart(await opening.ConfigureAwait(false), null, 0)
                : null;

        if (next is null) return false;
        StreamTrace.TryWait(
            StreamTraceKind.VolumeBoundary,
            phase,
            Stopwatch.GetElapsedTime(waitStarted),
            partIndex: (next.Stream as PaddedLengthStream)?.PartIndex,
            offset: _position);
        _currentStream = next.Stream;
        _primed = next.Primed;
        _primedOffset = 0;
        _primedCount = next.PrimedCount;
        if (_primedCount == 0) ReturnPrimed();
        return true;
    }

    // Reading the first bytes is what starts a lazily-opened part's segment pipeline.
    private async Task<PreparedPart?> PrepareNextAsync(
        Task<Stream> opening, long start, long window, CancellationToken ct)
    {
        Stream stream;
        try
        {
            // Only the wait is cancelled; a shared lazy resolution keeps running for others.
            stream = await opening.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The open may have completed after cancellation won; the abandoned stream is still ours.
            _ = DisposeWhenOpenedAsync(opening);
            throw;
        }

        // Bound before the first read builds the pipeline, so it never prefetches past the window.
        if (stream is PaddedLengthStream { SpeculativeReadAhead: { } speculative })
            speculative.Bind(_cursor, start, window);

        var primed = ArrayPool<byte>.Shared.Rent(PrimeBufferSize);
        try
        {
            var count = await stream.ReadAsync(primed.AsMemory(0, PrimeBufferSize), ct).ConfigureAwait(false);
            return new PreparedPart(stream, primed, count);
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(primed);
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task DisposeWhenOpenedAsync(Task<Stream> opening)
    {
        try
        {
            await (await opening.ConfigureAwait(false)).DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Serilog.Log.Debug(e, "Abandoned next part failed to open or dispose after the combined stream closed");
        }
    }

    private void ReturnPrimed()
    {
        if (_primed is { } primed) ArrayPool<byte>.Shared.Return(primed);
        _primed = null;
        _primedCount = 0;
    }

    public override void Flush()
    {
        _currentStream?.Flush();
    }

    ValueTask IDeliveredBytesValidation.ValidateDeliveredAsync(CancellationToken cancellationToken) =>
        _currentStream.ValidateDeliveredAsync(cancellationToken);

    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        return _currentStream?.FlushAsync(cancellationToken) ?? Task.CompletedTask;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && MarkDisposed())
        {
            _prefetchCts?.Cancel();
            _streams.Dispose();
            _currentStream?.Dispose();
            ReturnPrimed();
            _ = DisposeNextPartAsync();
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (!MarkDisposed()) return;
        if (_prefetchCts is { } prefetchCts) await prefetchCts.CancelAsync().ConfigureAwait(false);
        if (_currentStream != null) await _currentStream.DisposeAsync().ConfigureAwait(false);
        ReturnPrimed();
        // Joined so callers that await teardown (Seek → next read) never overlap prefetch leases.
        await DisposeNextPartAsync().ConfigureAwait(false);
        _streams.Dispose();
        GC.SuppressFinalize(this);
        await base.DisposeAsync().ConfigureAwait(false);
    }

    // After this no refill schedules another part, so the queue is only drained.
    private bool MarkDisposed()
    {
        lock (_gate)
        {
            if (_isDisposed) return false;
            _isDisposed = true;
            return true;
        }
    }

    private async Task DisposeNextPartAsync()
    {
        try
        {
            while (true)
            {
                PendingPart? pending;
                // A read racing disposal may still dequeue in OpenNextAsync.
                lock (_gate)
                {
                    if (!_nextParts.TryDequeue(out pending)) break;
                }

                try
                {
                    if (await pending.Prepared.ConfigureAwait(false) is { } next)
                    {
                        if (next.Primed is { } primed) ArrayPool<byte>.Shared.Return(primed);
                        await next.Stream.DisposeAsync().ConfigureAwait(false);
                    }
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    Serilog.Log.Debug(e, "Prefetched part failed to open or dispose after the combined stream closed");
                }
            }
        }
        finally
        {
            _prefetchCts?.Dispose();
        }
    }
}
