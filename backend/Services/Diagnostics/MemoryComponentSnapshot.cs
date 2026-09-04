using System.Diagnostics;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Config;
using NzbWebDAV.Services;
using NzbWebDAV.Streams;

namespace NzbWebDAV.Services.Diagnostics;

/// <summary>
/// Versioned, privacy-safe, point-in-time ownership counters for correlating
/// backend memory with external process and cgroup sampling. Component values
/// intentionally overlap GC/runtime views and must not be summed in product code.
/// </summary>
public sealed record MemoryComponentSnapshot(
    int SchemaVersion,
    DateTimeOffset CapturedAtUtc,
    long MonotonicTimestamp,
    long CaptureDurationMicroseconds,
    long? BackendWorkingSetBytes,
    GcSnapshot Gc,
    InFlightArticleMemorySnapshot InFlightArticles,
    SegmentBufferMemorySnapshot? SegmentBuffers,
    SharedStreamMemorySnapshot SharedStreams,
    SegmentCacheWriterMemorySnapshot CacheWriter,
    MemoryActivitySnapshot Activity);

public readonly record struct SharedStreamMemorySnapshot(
    long RingLogicalBytes,
    long RingRentedCapacityBytes,
    long RingRentedCapacityBytesPeak,
    long PumpScratchRentedCapacityBytes,
    long PumpScratchRentedCapacityBytesPeak,
    long ConfiguredRingMaximumBytes,
    long LiveEntries,
    long DrainingEntries,
    long LaggingReaders);

/// <summary>
/// Null fields indicate that asynchronous cache write-behind is unsupported,
/// rather than claiming an unavailable owner has zero bytes.
/// </summary>
public readonly record struct SegmentCacheWriterMemorySnapshot(
    bool Supported,
    long? WriteBudgetBytes,
    long? QueuedWriteBytes,
    long? PeakQueuedWriteBytes,
    long? QueuedJobs,
    long? ActiveJobs,
    long? CapacitySkipsTotal);

public readonly record struct MemoryActivitySnapshot(
    long ActiveReads,
    long CurrentInFlightSegmentFetches);

/// <summary>
/// Assembles one cheap snapshot from existing domain counters. It neither forces
/// collection nor enumerates per-path, per-segment, or per-size-class state.
/// </summary>
public sealed class MemoryComponentSnapshotBuilder(
    InFlightArticleBudget inFlightArticleBudget,
    ConfigManager configManager,
    ConcurrentReadTracker concurrentReadTracker,
    ActiveReadRegistry activeReads,
    SegmentCacheStatistics segmentCacheStatistics)
{
    public const int CurrentSchemaVersion = 1;

    public MemoryComponentSnapshot Capture()
    {
        var timestamp = Stopwatch.GetTimestamp();
        var capturedAtUtc = DateTimeOffset.UtcNow;

        var gc = GcSnapshotBuilder.Capture();
        var articleBudget = inFlightArticleBudget.SnapshotMemory();
        var bufferPool = BufferPoolDiagnostics.Shared.Snapshot();
        var segmentPool = (PooledBufferStream.DefaultPool as SegmentBufferPool)?.MemorySnapshot();
        var reads = concurrentReadTracker.Snapshot();
        var writer = segmentCacheStatistics.GetWriterSnapshot();

        SegmentBufferMemorySnapshot? segmentBuffers = segmentPool is { } pool
            ? new SegmentBufferMemorySnapshot(
                pool.Mode,
                pool.CheckedOutCapacityBytes,
                pool.IdleCapacityBytes,
                pool.MaxIdleBytes,
                pool.RentCount,
                pool.ReturnCount,
                pool.RejectedReturnCount,
                bufferPool.RequestedBytes,
                bufferPool.RentedBytes,
                bufferPool.BucketWasteBytes)
            : null;

        var sharedStreams = new SharedStreamMemorySnapshot(
            reads.SharedStreamRingLogicalBytes,
            reads.SharedStreamRingRetainedBytes,
            reads.SharedStreamRingRetainedBytesPeak,
            reads.SharedStreamPumpScratchRentedBytes,
            reads.SharedStreamPumpScratchRentedBytesPeak,
            (long)configManager.GetSharedStreamsMaxEntries() *
                configManager.GetSharedStreamsRingBytes(),
            reads.SharedStreamLiveEntries,
            reads.SharedStreamDrainingEntries,
            reads.SharedStreamLaggingReaders);

        var cacheWriter = new SegmentCacheWriterMemorySnapshot(
            Supported: writer.HasValue,
            WriteBudgetBytes: writer?.BudgetBytes,
            QueuedWriteBytes: writer?.ReservedBytes,
            PeakQueuedWriteBytes: writer?.PeakReservedBytes,
            QueuedJobs: writer?.QueuedJobs,
            ActiveJobs: writer?.ActiveJobs,
            CapacitySkipsTotal: writer?.CapacitySkips);

        return new MemoryComponentSnapshot(
            CurrentSchemaVersion,
            capturedAtUtc,
            timestamp,
            Stopwatch.GetElapsedTime(timestamp).Ticks / 10,
            gc.WorkingSetBytes,
            gc,
            articleBudget,
            segmentBuffers,
            sharedStreams,
            cacheWriter,
            new MemoryActivitySnapshot(activeReads.Count, reads.CurrentInFlightSegmentFetches));
    }
}

/// <summary>
/// Aggregate segment-pool counters. A null <see cref="MemoryComponentSnapshot.SegmentBuffers"/>
/// means the process is using ArrayPool.Shared, whose retained capacity is not observable here.
/// </summary>
public readonly record struct SegmentBufferMemorySnapshot(
    string Mode,
    long CheckedOutCapacityBytes,
    long IdleCapacityBytes,
    long MaxIdleBytes,
    long RentCount,
    long ReturnCount,
    long RejectedReturnCount,
    long RequestedBytesTotal,
    long RentedCapacityBytesTotal,
    long BucketWasteBytesTotal);
