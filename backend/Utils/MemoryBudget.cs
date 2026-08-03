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
    private const int MaxDefaultInFlightArticleBudgetMb = 512;

    private static readonly Lazy<long> LazyHeapLimit = new(DetectHeapLimitBytes);

    /// <summary>The managed-heap ceiling this process is actually running under.</summary>
    public static long HeapLimitBytes => LazyHeapLimit.Value;

    /// <summary>
    /// Default for <c>usenet.in-flight-article-budget-mb</c> when unset: 25% of the
    /// detected heap limit, clamped to [64, 512] so large hosts keep today's ceiling
    /// and small hosts do not dedicate half their RAM to decoded articles.
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
        catch (Exception e)
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
        Log.Information(
            "[MemoryBudget] Heap limit {HeapMB}MB -> in-flight article budget {BudgetMB}MB (derived when unset; explicit usenet.in-flight-article-budget-mb still wins).",
            HeapLimitBytes / Mb,
            effectiveBudgetMb);
    }
}
