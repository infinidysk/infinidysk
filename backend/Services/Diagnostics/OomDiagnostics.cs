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
            var pool = PooledBufferStream.DefaultPool as SegmentBufferPool;
            var poolSnapshot = pool?.SnapshotForOom();
            var articleBudget = InFlightArticleBudget.Current;
            long lohSize = -1;
            long lohFragmentation = -1;
            if (info.GenerationInfo.Length > 3)
            {
                lohSize = info.GenerationInfo[3].SizeAfterBytes;
                lohFragmentation = info.GenerationInfo[3].FragmentationAfterBytes;
            }

            Log.Warning(
                "OutOfMemoryException during {Context}. " +
                "LastGcIndex={GcIndex} LastGcGeneration={GcGeneration} " +
                "LastGcCompacted={GcCompacted} LastGcConcurrent={GcConcurrent} " +
                "LastGcHeap={Heap:N0} LastGcFragmentation={Fragmentation:N0} " +
                "LastGcLohSize={LohSize:N0} LastGcLohFragmentation={LohFragmentation:N0} " +
                "LastGcMemoryLoad={MemoryLoad:N0} LastGcHighMemoryLoadThreshold={HighMemoryLoadThreshold:N0} " +
                "LastGcCommitted={Committed:N0} LastGcAvailableCeiling={Available:N0} " +
                "CurrentWorkingSet={WorkingSet:N0} PoolIdle={PoolIdle:N0} " +
                "PoolOutstandingUnreturned={PoolOutstanding:N0} " +
                "PoolAllocationAttempts={PoolAllocationAttempts:N0} " +
                "PoolAllocationFailures={PoolAllocationFailures:N0} " +
                "PoolAllocations={PoolAllocations:N0} PoolTrimmed={PoolTrimmed:N0} " +
                "InFlight={InFlight:N0} DecodedPipe={DecodedPipe:N0} ArticleWaiters={ArticleWaiters:N0} " +
                "Cap={Cap:N0} Virtual={Virtual:N0} " +
                "RLIMIT_AS={AddressSpaceLimit:N0} RegionRange={RegionRange:N0} " +
                "HeapHardLimit={HeapHardLimit:N0} LohHardLimit={LohHardLimit:N0} " +
                "LohHardLimitPercent={LohHardLimitPercent:N0}",
                context,
                info.Index,
                info.Generation,
                info.Compacted,
                info.Concurrent,
                info.HeapSizeBytes,
                info.FragmentedBytes,
                lohSize,
                lohFragmentation,
                info.MemoryLoadBytes,
                info.HighMemoryLoadThresholdBytes,
                info.TotalCommittedBytes,
                info.TotalAvailableMemoryBytes,
                addressSpace.WorkingSetBytes ?? -1,
                poolSnapshot?.IdleBytes ?? -1,
                poolSnapshot?.CheckedOutBytes ?? -1,
                poolSnapshot?.AllocationAttemptCount ?? -1,
                poolSnapshot?.AllocationFailureCount ?? -1,
                poolSnapshot?.AllocationCount ?? -1,
                poolSnapshot?.TrimmedBytes ?? -1,
                articleBudget?.LeasedBytes ?? -1,
                articleBudget?.DecodedPipeBytes ?? -1,
                articleBudget?.WaiterCount ?? -1,
                articleBudget?.CapBytes ?? -1,
                addressSpace.VirtualMemoryBytes ?? -1,
                addressSpace.AddressSpaceLimitBytes ?? -1,
                addressSpace.GcRegionRangeBytes ?? -1,
                addressSpace.GcHeapHardLimitBytes ?? -1,
                addressSpace.GcHeapHardLimitLohBytes ?? -1,
                addressSpace.GcHeapHardLimitLohPercent ?? -1);
            Log.Debug(exception, "OutOfMemoryException stack during {Context}", context);
        }
        catch
        {
            // Memory diagnostics must never mask the original OOM.
        }
    }

    public static void LogSegmentBufferAllocationFailure(
        OutOfMemoryException exception,
        int requestedBytes,
        int roundedBytes,
        SegmentBufferPoolOomSnapshot snapshot)
    {
        Log.Warning(
            "OutOfMemoryException allocating segment buffer. " +
            "Requested={RequestedBytes:N0} Rounded={RoundedBytes:N0} " +
            "PoolIdle={PoolIdle:N0} PoolOutstandingUnreturned={PoolOutstanding:N0} " +
            "PoolAllocationAttempts={PoolAllocationAttempts:N0} " +
            "PoolAllocationFailures={PoolAllocationFailures:N0} " +
            "PoolAllocations={PoolAllocations:N0} PoolTrimmed={PoolTrimmed:N0}",
            requestedBytes,
            roundedBytes,
            snapshot.IdleBytes,
            snapshot.CheckedOutBytes,
            snapshot.AllocationAttemptCount,
            snapshot.AllocationFailureCount,
            snapshot.AllocationCount,
            snapshot.TrimmedBytes);
        Log.Debug(exception, "OutOfMemoryException stack allocating segment buffer");
    }
}
