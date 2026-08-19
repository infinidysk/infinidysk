using NzbWebDAV.Streams;

namespace NzbWebDAV.Services.Diagnostics;

public sealed class GcDiagnosticsStore : IDisposable
{
    private GcDiagnosticsResult? _lastResult;
    private readonly SemaphoreSlim _runGate = new(1, 1);

    public GcDiagnosticsResult? LastResult => Volatile.Read(ref _lastResult);

    public bool TryBegin() => _runGate.Wait(0);

    public void End() => _runGate.Release();

    public void Store(GcDiagnosticsResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        Volatile.Write(ref _lastResult, result);
    }

    public void Dispose()
    {
        _runGate.Dispose();
    }
}

public sealed record GcDiagnosticsResult(
    DateTimeOffset RunAtUtc,
    GcSnapshot Before,
    GcSnapshot After,
    long PauseMs,
    GcBufferRetention Retention,
    SegmentBufferPoolSnapshot? SegmentBufferPool);

public sealed record GcSnapshot(
    IReadOnlyList<GcGenerationInfo> Generations,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections,
    long TotalAllocatedBytes,
    long HeapSizeBytes,
    long TotalCommittedBytes,
    long TotalAvailableMemoryBytes,
    long FragmentationBytes,
    double PauseTimePercentage);

public sealed record GcGenerationInfo(
    string Name,
    long SizeAfterBytes,
    long FragmentationAfterBytes);

public sealed record GcBufferRetention(
    long InFlightArticleBytes,
    long InFlightArticleBudgetBytes,
    long InFlightArticleThrottleEvents,
    long SegmentBufferRents,
    long SegmentBufferReturns,
    long SegmentBufferGrowths,
    long SegmentBufferCheckedOutBytes,
    long SegmentBufferRequestedBytes,
    long SegmentBufferRentedBytes,
    long SegmentBufferBucketWasteBytes);
