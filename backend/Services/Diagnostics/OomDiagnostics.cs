using NzbWebDAV.Streams;
using Serilog;

namespace NzbWebDAV.Services.Diagnostics;

internal static class OomDiagnostics
{
    public static void LogHeapStateOnOom(Exception exception, string context)
    {
        if (exception is not OutOfMemoryException) return;

        try
        {
            var info = GC.GetGCMemoryInfo();
            Log.Warning(
                exception,
                "OutOfMemoryException during {Context}. Heap={Heap:N0} Committed={Committed:N0} " +
                "Available={Available:N0} Fragmentation={Fragmentation:N0} " +
                "InFlight={InFlight:N0} Cap={Cap:N0}",
                context,
                info.HeapSizeBytes,
                info.TotalCommittedBytes,
                info.TotalAvailableMemoryBytes,
                info.FragmentedBytes,
                InFlightArticleBudget.Current?.LeasedBytes ?? -1,
                InFlightArticleBudget.Current?.CapBytes ?? -1);
        }
        catch
        {
            // Memory diagnostics must never mask the original OOM.
        }
    }
}
