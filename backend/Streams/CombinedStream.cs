using UsenetSharp.Streams;

namespace NzbWebDAV.Streams;

/// <param name="readAheadBytes">
/// When positive, opens the next stream once a <see cref="PaddedLengthStream"/> part has
/// this many bytes or fewer left, so its download is under way before the boundary.
/// </param>
public class CombinedStream(IEnumerable<Task<Stream>> streams, long readAheadBytes = 0) : FastReadOnlyNonSeekableStream
{
    private readonly IEnumerator<Task<Stream>> _streams = streams.GetEnumerator();
    private Stream? _currentStream;
    private Task<Stream>? _nextStream;
    private long _position;
    private bool _isDisposed;

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

            // If we haven't read the first stream, read it.
            if (_currentStream == null)
            {
                if (TakeNext() is not { } first) return 0;
                _currentStream = await first.ConfigureAwait(false);
            }

            // read from our current stream
            var readCount = await _currentStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            _position += readCount;
            if (readCount > 0)
            {
                if (readAheadBytes > 0
                    && _nextStream is null
                    && _currentStream is PaddedLengthStream part
                    && part.Length - part.Position <= readAheadBytes
                    && _streams.MoveNext())
                {
                    _nextStream = _streams.Current;
                }

                return readCount;
            }

            // If we couldn't read anything from our current stream,
            // it's time to advance to the next stream.
            await _currentStream.DisposeAsync().ConfigureAwait(false);
            _currentStream = null;
            if (TakeNext() is not { } next) return 0;
            _currentStream = await next.ConfigureAwait(false);
        }
    }

    private Task<Stream>? TakeNext()
    {
        if (_nextStream is { } prefetched)
        {
            _nextStream = null;
            return prefetched;
        }

        return _streams.MoveNext() ? _streams.Current : null;
    }

    public override void Flush()
    {
        _currentStream?.Flush();
    }

    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        return _currentStream?.FlushAsync(cancellationToken) ?? Task.CompletedTask;
    }

    protected override void Dispose(bool disposing)
    {
        if (!_isDisposed && disposing)
        {
            _streams.Dispose();
            _currentStream?.Dispose();
            DisposeNextWhenOpened();
            _isDisposed = true;
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (_isDisposed) return;
        if (_currentStream != null) await _currentStream.DisposeAsync().ConfigureAwait(false);
        DisposeNextWhenOpened();
        _streams.Dispose();
        _isDisposed = true;
        GC.SuppressFinalize(this);
        await base.DisposeAsync().ConfigureAwait(false);
    }

    // A prefetched part may still be resolving; dispose it once open and observe any fault.
    private void DisposeNextWhenOpened()
    {
        if (Interlocked.Exchange(ref _nextStream, null) is { } next) _ = DisposeWhenOpenedAsync(next);
    }

    private static async Task DisposeWhenOpenedAsync(Task<Stream> next)
    {
        try
        {
            await (await next.ConfigureAwait(false)).DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Serilog.Log.Debug(e, "Prefetched part failed to open or dispose after the combined stream closed");
        }
    }
}
