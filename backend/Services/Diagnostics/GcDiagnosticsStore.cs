using System.Diagnostics;
using NzbWebDAV.Streams;

namespace NzbWebDAV.Services.Diagnostics;

public interface IGcDiagnosticsExecutor
{
    GcCollectionExecution Execute();
}

public readonly record struct GcCollectionExecution(
    GcSnapshot Before,
    GcSnapshot After,
    long PauseMs);

internal sealed class AggressiveGcDiagnosticsExecutor : IGcDiagnosticsExecutor
{
    public GcCollectionExecution Execute()
    {
        var before = GcSnapshotBuilder.Capture();
        var stopwatch = Stopwatch.StartNew();
#pragma warning disable CA2001 // this is the authenticated manual diagnostics path
        // codeql[cs/call-to-gc]
        GC.Collect(
            GC.MaxGeneration,
            GCCollectionMode.Aggressive,
            blocking: true,
            compacting: true);
        GC.WaitForPendingFinalizers();
        // codeql[cs/call-to-gc]
        GC.Collect(
            GC.MaxGeneration,
            GCCollectionMode.Aggressive,
            blocking: true,
            compacting: true);
#pragma warning restore CA2001
        stopwatch.Stop();
        return new GcCollectionExecution(
            before,
            GcSnapshotBuilder.Capture(),
            stopwatch.ElapsedMilliseconds);
    }
}

public sealed class GcDiagnosticsStore : IDisposable
{
    public static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(10);

    private readonly TimeProvider _timeProvider;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _runGate = new(1, 1);
    private long? _lastAttemptTimestamp;
    private GcDiagnosticsResult? _lastResult;

    public GcDiagnosticsStore() : this(TimeProvider.System)
    {
    }

    public GcDiagnosticsStore(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public GcDiagnosticsResult? LastResult => Volatile.Read(ref _lastResult);

    internal GcDiagnosticsAdmissionResult TryBegin()
    {
        if (!_runGate.Wait(0))
        {
            lock (_gate)
            {
                return new GcDiagnosticsAdmissionResult(
                    GcDiagnosticsAdmission.AlreadyRunning,
                    RemainingCooldownSecondsLocked());
            }
        }

        lock (_gate)
        {
            if (_lastAttemptTimestamp is { } last)
            {
                var elapsed = _timeProvider.GetElapsedTime(last);
                if (elapsed < Cooldown)
                {
                    var remaining = Cooldown - elapsed;
                    var seconds = Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds));
                    _runGate.Release();
                    return new GcDiagnosticsAdmissionResult(
                        GcDiagnosticsAdmission.Cooldown,
                        seconds);
                }
            }

            _lastAttemptTimestamp = _timeProvider.GetTimestamp();
        }

        return new GcDiagnosticsAdmissionResult(GcDiagnosticsAdmission.Started, RetryAfterSeconds: null);
    }

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

    private int? RemainingCooldownSecondsLocked()
    {
        if (_lastAttemptTimestamp is not { } last) return null;
        var elapsed = _timeProvider.GetElapsedTime(last);
        var remaining = Cooldown - elapsed;
        if (remaining <= TimeSpan.Zero) return null;
        return Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds));
    }
}

internal enum GcDiagnosticsAdmission
{
    Started,
    AlreadyRunning,
    Cooldown,
}

internal readonly record struct GcDiagnosticsAdmissionResult(
    GcDiagnosticsAdmission Status,
    int? RetryAfterSeconds);

public sealed record GcDiagnosticsResult(
    DateTimeOffset RunAtUtc,
    GcSnapshot Before,
    GcSnapshot After,
    long PauseMs,
    GcBufferRetention Retention,
    SegmentBufferPoolSnapshot? SegmentBufferPool)
{
    public string CollectionMode { get; init; } = "Aggressive";
    public int FullBlockingCollectionsRequested { get; init; } = 2;
}

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
    double PauseTimePercentage)
{
    public long Index { get; init; }
    public int Generation { get; init; }
    public bool Compacted { get; init; }
    public bool Concurrent { get; init; }
    public long MemoryLoadBytes { get; init; }
    public long HighMemoryLoadThresholdBytes { get; init; }
    public long? WorkingSetBytes { get; init; }
    public TimeSpan TotalPauseDuration { get; init; }
}

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
