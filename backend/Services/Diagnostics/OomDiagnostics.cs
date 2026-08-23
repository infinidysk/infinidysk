using System.Diagnostics.CodeAnalysis;
using NzbWebDAV.Streams;
using NzbWebDAV.Utils;
using Serilog;

namespace NzbWebDAV.Services.Diagnostics;

internal static class OomDiagnostics
{
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Diagnostic logging must not replace the original OutOfMemoryException.")]
    public static void LogHeapStateOnOom(Exception exception, string context)
    {
        if (exception is not OutOfMemoryException) return;

        try
        {
            var info = GC.GetGCMemoryInfo();
            var addressSpace = AddressSpaceDiagnostics.Capture();
            Log.Warning(
                "OutOfMemoryException during {Context}. Heap={Heap:N0} Committed={Committed:N0} " +
                "Available={Available:N0} Fragmentation={Fragmentation:N0} " +
                "InFlight={InFlight:N0} Cap={Cap:N0} Virtual={Virtual:N0} RLIMIT_AS={AddressSpaceLimit:N0} " +
                "RegionRange={RegionRange:N0} HeapHardLimit={HeapHardLimit:N0}",
                context,
                info.HeapSizeBytes,
                info.TotalCommittedBytes,
                info.TotalAvailableMemoryBytes,
                info.FragmentedBytes,
                InFlightArticleBudget.Current?.LeasedBytes ?? -1,
                InFlightArticleBudget.Current?.CapBytes ?? -1,
                addressSpace.VirtualMemoryBytes ?? -1,
                addressSpace.AddressSpaceLimitBytes ?? -1,
                addressSpace.GcRegionRangeBytes ?? -1,
                addressSpace.GcHeapHardLimitBytes ?? -1);
            Log.Debug(exception, "OutOfMemoryException stack during {Context}", context);
        }
        catch
        {
            // Memory diagnostics must never mask the original OOM.
        }
    }
}
