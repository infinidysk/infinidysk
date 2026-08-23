using Serilog;

namespace NzbWebDAV.Utils;

/// <summary>
/// Derives streaming defaults from the process's managed-heap ceiling so small
/// containers get safer unset defaults without operator configuration.
/// </summary>
public static class MemoryBudget
{
    private const long Mb = 1024 * 1024;

    /// <summary>Used when the runtime reports nothing usable — deliberately pessimistic.</summary>
    private const long FallbackHeapLimitBytes = 512 * Mb;

    /// <summary>Share of the heap reserved for the host-wide in-flight article budget when unset.</summary>
    private const double InFlightBudgetShare = 0.25;

    private const int MinInFlightArticleBudgetMb = 64;
    private const int MaxDefaultInFlightArticleBudgetMb = 8192;

    private static readonly Lazy<long> LazyHeapLimit = new(DetectHeapLimitBytes);

    /// <summary>The managed-heap ceiling this process is actually running under.</summary>
    public static long HeapLimitBytes => LazyHeapLimit.Value;

    /// <summary>
    /// Default for <c>usenet.in-flight-article-budget-mb</c> when unset: 25% of the
    /// detected managed-heap ceiling, clamped to [64, 8192]. The budget gates
    /// decoded-article admission; it does not reserve that memory eagerly.
    /// </summary>
    public static int DefaultInFlightArticleBudgetMb() =>
        DefaultInFlightArticleBudgetMb(HeapLimitBytes);

    /// <summary>Pure sizing helper for tests.</summary>
    public static int DefaultInFlightArticleBudgetMb(long heapLimitBytes)
    {
        if (heapLimitBytes <= 0) heapLimitBytes = FallbackHeapLimitBytes;
        var mb = (int)(heapLimitBytes * InFlightBudgetShare / Mb);
        return Math.Clamp(mb, MinInFlightArticleBudgetMb, MaxDefaultInFlightArticleBudgetMb);
    }

    /// <summary>
    /// The GC's configured hard limit if there is one, otherwise what it reports as available.
    /// <c>GC.GetConfigurationVariables</c> is the authoritative source for the limit and, unlike
    /// <c>GCMemoryInfo.TotalAvailableMemoryBytes</c>, does not vary with which collection last ran.
    /// </summary>
    private static long DetectHeapLimitBytes()
    {
        try
        {
            var config = GC.GetConfigurationVariables();

            if (config.TryGetValue("GCHeapHardLimit", out var hardLimit))
            {
                var bytes = ToInt64(hardLimit);
                if (bytes > 0) return bytes;
            }

            var available = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            if (config.TryGetValue("GCHeapHardLimitPercent", out var percent))
            {
                var pct = ToInt64(percent);
                if (pct > 0 && available > 0) return available * pct / 100;
            }

            if (available > 0) return available;
        }
        catch (Exception e) when (e is InvalidOperationException or NotSupportedException)
        {
            Log.Warning(e, "[MemoryBudget] Could not read the GC configuration; falling back to {FallbackMB}MB.",
                FallbackHeapLimitBytes / Mb);
        }

        return FallbackHeapLimitBytes;
    }

    private static long ToInt64(object value) => value switch
    {
        long l => l,
        ulong ul => ul > long.MaxValue ? long.MaxValue : (long)ul,
        int i => i,
        uint ui => ui,
        string s when long.TryParse(s, out var parsed) => parsed,
        _ => 0,
    };

    /// <summary>One startup line saying what unset defaults the box affords.</summary>
    public static void LogInFlightBudget(int effectiveBudgetMb)
    {
        var addressSpace = AddressSpaceDiagnostics.Capture();
        Log.Information(
            "[MemoryBudget] Heap limit {HeapMB}MB, GC hard limit {GcHardLimitMB}MB, region range {RegionRangeMB}MB, " +
            "region size {RegionSizeMB}MB, committed heap {CommittedMB}MB, virtual memory {VirtualMemoryMB}MB, " +
            "RLIMIT_AS {AddressSpaceLimitMB}MB -> in-flight article budget {BudgetMB}MB " +
            "(derived when unset; explicit usenet.in-flight-article-budget-mb still wins).",
            HeapLimitBytes / Mb,
            ToMegabytes(addressSpace.GcHeapHardLimitBytes),
            ToMegabytes(addressSpace.GcRegionRangeBytes),
            ToMegabytes(addressSpace.GcRegionSizeBytes),
            ToMegabytes(addressSpace.GcCommittedBytes),
            ToMegabytes(addressSpace.VirtualMemoryBytes),
            ToMegabytes(addressSpace.AddressSpaceLimitBytes),
            effectiveBudgetMb);
    }

    private static long? ToMegabytes(long? bytes) => bytes / Mb;
}
