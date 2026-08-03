namespace UsenetSharp.Streams;

/// <summary>
/// Tracks unread bytes in a decoded-body pipe and signals when its consumer has
/// reached EOF or disposed the stream.
/// </summary>
internal sealed class DecodedBodyReadStream(
    Stream innerStream,
    Action<long> reportBufferedByteDelta) : FastReadOnlyNonSeekableStream
{
    private readonly object _sync = new();
    private readonly TaskCompletionSource _consumed =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _bufferedBytes;
    private bool _completed;

    internal Task Completion => _consumed.Task;

    internal void AddBufferedBytes(int count)
    {
        if (count <= 0)
        {
            return;
        }

        lock (_sync)
        {
            if (_completed)
            {
                return;
            }

            _bufferedBytes += count;
            reportBufferedByteDelta(count);
        }
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        var bytesRead = await innerStream.ReadAsync(buffer, cancellationToken)
            .ConfigureAwait(false);
        if (bytesRead == 0)
        {
            CompleteConsumption();
        }
        else
        {
            ConsumeBytes(bytesRead);
        }

        return bytesRead;
    }

    private void ConsumeBytes(int count)
    {
        lock (_sync)
        {
            if (_completed)
            {
                return;
            }

            var consumed = Math.Min(_bufferedBytes, count);
            _bufferedBytes -= consumed;
            reportBufferedByteDelta(-consumed);
        }
    }

    private void CompleteConsumption()
    {
        lock (_sync)
        {
            if (_completed)
            {
                return;
            }

            _completed = true;
            reportBufferedByteDelta(-_bufferedBytes);
            _bufferedBytes = 0;
            _consumed.TrySetResult();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            CompleteConsumption();
            innerStream.Dispose();
        }

        base.Dispose(disposing);
    }
}
