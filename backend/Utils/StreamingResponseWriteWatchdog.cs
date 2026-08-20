using System.Diagnostics;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Streams;

namespace NzbWebDAV.Utils;

/// <summary>
/// Bounds writes of a streaming HTTP response so a stalled or trickling client
/// cannot pin <see cref="InFlightArticleBudget"/> leases until restart.
/// Per-write <see cref="WaitAsync"/> covers a fully paused destination; the
/// aggregate check covers a client that completes each 64 KB write inside the
/// deadline but transfers less than one chunk per timeout window while other
/// streams are waiting on the budget.
/// </summary>
internal sealed class StreamingResponseWriteWatchdog(
    TimeSpan timeout,
    CancellationTokenSource readCts,
    InFlightArticleBudget? budget)
{
    /// <summary>
    /// Size of the WebDAV/`/view` copy buffer. Aggregate reclaim fires when a
    /// contended stream writes fewer than this many bytes over one timeout of
    /// write-side time.
    /// </summary>
    internal const int CopyChunkBytes = 64 * 1024;

    private long _windowWriteTicks;
    private long _windowBytes;

    public async ValueTask WriteAsync(
        Stream dest,
        Memory<byte> chunk,
        CancellationToken cancellationToken)
    {
        var writeStarted = Stopwatch.GetTimestamp();
        await WriteWithProgressTimeoutAsync(dest, chunk, timeout, readCts, cancellationToken)
            .ConfigureAwait(false);
        if (ObserveWrite(chunk.Length, Stopwatch.GetElapsedTime(writeStarted)))
        {
            await readCts.CancelAsync().ConfigureAwait(false);
            throw new StreamingWriteTimeoutException(
                "Client transferred under 64 KB per timeout window while other streams waited on Article RAM.")
            {
                Reason = StreamingWriteTimeoutException.AggregateReclaimReason,
            };
        }
    }

    /// <summary>
    /// Records a completed client write. Returns true when write-side throughput
    /// stayed below one copy chunk per timeout window and the article budget is
    /// contended — the caller must cancel the linked read token and throw
    /// <see cref="StreamingWriteTimeoutException"/>. Time spent in <c>ReadAsync</c>
    /// is excluded by only counting durations passed here.
    /// </summary>
    internal bool ObserveWrite(int bytes, TimeSpan writeDuration)
    {
        if (timeout <= TimeSpan.Zero) return false;
        if (bytes < 0) return false;

        _windowBytes += bytes;
        if (writeDuration > TimeSpan.Zero)
            _windowWriteTicks += writeDuration.Ticks;

        if (_windowWriteTicks < timeout.Ticks) return false;

        if (_windowBytes < CopyChunkBytes && budget is { HasWaiters: true })
            return true;

        _windowBytes = 0;
        _windowWriteTicks = 0;
        return false;
    }

    /// <summary>
    /// Writes one chunk to the client, enforcing a per-write progress deadline.
    /// On timeout the linked read token is cancelled so the pipeline unwinds and
    /// in-flight article leases are released.
    /// </summary>
    internal static async ValueTask WriteWithProgressTimeoutAsync(
        Stream dest,
        Memory<byte> chunk,
        TimeSpan timeout,
        CancellationTokenSource readCts,
        CancellationToken cancellationToken)
    {
        if (timeout <= TimeSpan.Zero)
        {
            await dest.WriteAsync(chunk, cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            await dest.WriteAsync(chunk, cancellationToken).AsTask()
                .WaitAsync(timeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            await readCts.CancelAsync().ConfigureAwait(false);
            throw new StreamingWriteTimeoutException(
                "Client stopped reading; streaming write timed out.")
            {
                Reason = StreamingWriteTimeoutException.PerWriteStallReason,
            };
        }
    }
}
