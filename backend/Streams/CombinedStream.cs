using System.Buffers;
using NzbWebDAV.Clients.Usenet.Contexts;
using UsenetSharp.Streams;

namespace NzbWebDAV.Streams;

/// <param name="readAheadBytes">
/// When positive, opens the next stream and reads its first bytes once a
/// <see cref="PaddedLengthStream"/> part has this many bytes or fewer left, so the next
/// part's download pipeline is running before the boundary.
/// </param>
public class CombinedStream(IEnumerable<Task<Stream>> streams, long readAheadBytes = 0) : FastReadOnlyNonSeekableStream, IDeliveredBytesValidation
{
    private const int PrimeBufferSize = 64 * 1024;

    private readonly IEnumerator<Task<Stream>> _streams = streams.GetEnumerator();
    private Stream? _currentStream;
    private Task<PreparedPart?>? _nextPart;
    private ContextualCancellationTokenSource? _prefetchCts;
    private byte[]? _primed;
    private int _primedOffset;
    private int _primedCount;
    private long _position;
    private bool _isDisposed;

    private sealed record PreparedPart(Stream Stream, byte[]? Primed, int PrimedCount);

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
                _position += count;
                return count;
            }

            var readCount = await _currentStream!.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            _position += readCount;
            if (readCount > 0)
            {
                if (_nextPart is null
                    && _currentStream is PaddedLengthStream part
                    && (part.ReadAheadBytes > 0 ? part.ReadAheadBytes : readAheadBytes) is > 0 and var window
                    && part.Length - part.Position <= window
                    // Next-part leases must never take credits the current tail still needs.
                        // Unknown inner wrappers report false, declining prefetch.
                    && ((ISegmentIssueProgress)part).AllSegmentsIssued)
                {
                    // Owned by the stream, not the triggering read: only disposal cancels prefetch.
                    _prefetchCts ??= ContextualCancellationTokenSource.CreateWithContextsOf(cancellationToken);
                    _nextPart = PrepareNextAsync(_prefetchCts.Token);
                }

                return readCount;
            }

            await _currentStream.DisposeAsync().ConfigureAwait(false);
            _currentStream = null;
        }
    }

    private async ValueTask<bool> OpenNextAsync()
    {
        PreparedPart? next;
        if (_nextPart is { } pending)
        {
            // A failed prefetch surfaces here, at the boundary, after every current-part byte.
            _nextPart = null;
            next = await pending.ConfigureAwait(false);
        }
        else
        {
            next = _streams.MoveNext()
                ? new PreparedPart(await _streams.Current.ConfigureAwait(false), null, 0)
                : null;
        }

        if (next is null) return false;
        _currentStream = next.Stream;
        _primed = next.Primed;
        _primedOffset = 0;
        _primedCount = next.PrimedCount;
        if (_primedCount == 0) ReturnPrimed();
        return true;
    }

    // Reading the first bytes is what starts a lazily-opened part's segment pipeline.
    private async Task<PreparedPart?> PrepareNextAsync(CancellationToken ct)
    {
        if (!_streams.MoveNext()) return null;
        var opening = _streams.Current;
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
        if (!_isDisposed && disposing)
        {
            _isDisposed = true;
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
        if (_isDisposed) return;
        _isDisposed = true;
        if (_prefetchCts is { } prefetchCts) await prefetchCts.CancelAsync().ConfigureAwait(false);
        if (_currentStream != null) await _currentStream.DisposeAsync().ConfigureAwait(false);
        ReturnPrimed();
        // Joined so callers that await teardown (Seek → next read) never overlap prefetch leases.
        await DisposeNextPartAsync().ConfigureAwait(false);
        _streams.Dispose();
        GC.SuppressFinalize(this);
        await base.DisposeAsync().ConfigureAwait(false);
    }

    private async Task DisposeNextPartAsync()
    {
        var pending = _nextPart;
        _nextPart = null;
        try
        {
            if (pending is not null && await pending.ConfigureAwait(false) is { } next)
            {
                if (next.Primed is { } primed) ArrayPool<byte>.Shared.Return(primed);
                await next.Stream.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Serilog.Log.Debug(e, "Prefetched part failed to open or dispose after the combined stream closed");
        }
        finally
        {
            _prefetchCts?.Dispose();
        }
    }
}
