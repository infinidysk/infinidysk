using System.Diagnostics;

namespace NzbWebDAV.Services.Diagnostics;

internal static class GcSnapshotBuilder
{
    public static GcSnapshot Capture()
    {
        var info = GC.GetGCMemoryInfo();
        var generations = new List<GcGenerationInfo>(info.GenerationInfo.Length);
        for (var generation = 0; generation < info.GenerationInfo.Length; generation++)
        {
            var entry = info.GenerationInfo[generation];
            generations.Add(new GcGenerationInfo(
                GenerationName(generation),
                entry.SizeAfterBytes,
                entry.FragmentationAfterBytes));
        }

        return new GcSnapshot(
            generations,
            GC.CollectionCount(0),
            GC.CollectionCount(1),
            GC.CollectionCount(2),
            GC.GetTotalAllocatedBytes(precise: false),
            info.HeapSizeBytes,
            info.TotalCommittedBytes,
            info.TotalAvailableMemoryBytes,
            info.FragmentedBytes,
            info.PauseTimePercentage)
        {
            Index = info.Index,
            Generation = info.Generation,
            Compacted = info.Compacted,
            Concurrent = info.Concurrent,
            MemoryLoadBytes = info.MemoryLoadBytes,
            HighMemoryLoadThresholdBytes = info.HighMemoryLoadThresholdBytes,
            WorkingSetBytes = TryGetWorkingSetBytes(),
        };
    }

    private static long? TryGetWorkingSetBytes()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            return process.WorkingSet64;
        }
        catch (Exception e) when (e is InvalidOperationException or PlatformNotSupportedException)
        {
            return null;
        }
    }

    private static string GenerationName(int generation) => generation switch
    {
        0 => "gen0",
        1 => "gen1",
        2 => "gen2",
        3 => "loh",
        4 => "poh",
        _ => $"gen{generation}",
    };
}
