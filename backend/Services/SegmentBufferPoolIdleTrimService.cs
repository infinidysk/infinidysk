using Microsoft.Extensions.Hosting;
using NzbWebDAV.Streams;
using Serilog;

namespace NzbWebDAV.Services;

/// <summary>
/// Releases retained segment buffers once streaming has stopped. Without live
/// streams nothing allocates enough to trigger a gen2 collection, so released
/// read-ahead buffers and stream garbage would otherwise stay resident.
/// </summary>
internal sealed class SegmentBufferPoolIdleTrimService(ConcurrentReadTracker readTracker) : BackgroundService
{
    internal static readonly TimeSpan TrimAfter = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(5);
    internal const long CollectThresholdBytes = 16L * 1024 * 1024;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(CheckInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                ReleaseIdleBuffers(PooledBufferStream.DefaultPool);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal application shutdown.
        }
    }

    internal long ReleaseIdleBuffers(ISegmentBufferPool pool)
    {
        if (pool is not SegmentBufferPool { RetentionPolicy: SegmentBufferRetentionPolicy.CapacityOnly } segmentPool)
            return 0;
        // A paused or backpressured reader can leave the pool quiet while it still streams.
        if (readTracker.GetActiveReaderCount() > 0)
            return 0;

        var released = segmentPool.TrimIfIdle(TrimAfter);
        if (released < CollectThresholdBytes) return released;
#pragma warning disable CA2001 // one collection per idle transition, never while a read is live
        // codeql[cs/call-to-gc]
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
#pragma warning restore CA2001
        Log.Debug("Released {ReleasedMiB} MiB of idle segment buffers after streaming stopped.",
            released / (1024 * 1024));
        return released;
    }
}
