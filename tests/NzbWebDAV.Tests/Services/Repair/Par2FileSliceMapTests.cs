using NzbWebDAV.Models;
using NzbWebDAV.Services.Repair;

namespace NzbWebDAV.Tests.Services.Repair;

public sealed class Par2FileSliceMapTests
{
    [Fact]
    public void GlobalSlicesForSegment_UsesHalfOpenOverlap()
    {
        const int sliceSize = 64;
        var ranges = new[]
        {
            LongRange.FromStartAndSize(0, 40),
            LongRange.FromStartAndSize(40, 40),
            LongRange.FromStartAndSize(80, 48),
        };

        Assert.True(Par2FileSliceMap.TryCreate(
            fileLength: 128,
            globalSliceBase: 4,
            sliceSize,
            sliceCount: 2,
            ranges,
            out var map,
            out var error));
        Assert.Null(error);
        Assert.Equal([4], map!.GlobalSlicesForSegment(0));
        Assert.Equal([4, 5], map.GlobalSlicesForSegment(1));
        Assert.Equal([5], map.GlobalSlicesForSegment(2));
        Assert.Equal([0, 1], map.SegmentIndicesForGlobalSlice(4));
        Assert.Equal([1, 2], map.SegmentIndicesForGlobalSlice(5));
    }

    [Fact]
    public void GlobalSlicesForSegment_SkipsZeroLengthRanges()
    {
        var ranges = new[]
        {
            LongRange.FromStartAndSize(0, 64),
            LongRange.FromStartAndSize(64, 0),
            LongRange.FromStartAndSize(64, 64),
        };

        Assert.True(Par2FileSliceMap.TryCreate(
            128, 0, 64, 2, ranges, out var map, out var error));
        Assert.Null(error);
        Assert.Empty(map!.GlobalSlicesForSegment(1));
        Assert.Equal([0], map.SegmentIndicesForGlobalSlice(0));
        Assert.Equal([2], map.SegmentIndicesForGlobalSlice(1));
    }

    [Fact]
    public void TryCreate_RejectsRangeOutsideTheTargetFile()
    {
        var ranges = new[] { LongRange.FromStartAndSize(0, 200) };
        Assert.False(Par2FileSliceMap.TryCreate(
            100, 0, 64, 2, ranges, out var map, out var error));
        Assert.Null(map);
        Assert.Contains("outside the target file", error, StringComparison.Ordinal);
    }

    [Fact]
    public void TryCreate_RejectsSliceCountMismatch()
    {
        var ranges = new[] { LongRange.FromStartAndSize(0, 100) };
        Assert.False(Par2FileSliceMap.TryCreate(
            100, 0, 64, 1, ranges, out var map, out var error));
        Assert.Null(map);
        Assert.Contains("slice count", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EstimateWorkingSetBytes_IncludesCachedSlicesRecoveryAndPatches()
    {
        var bytes = Par2RepairService.EstimateWorkingSetBytes(
            cachedSourceBodyBytes: 10,
            assembledPresentSliceCount: 3,
            recoverySliceCount: 2,
            reconstructedSliceCount: 2,
            stagedPatchBytes: 5,
            sliceSize: 64);
        Assert.Equal(10 + (3 * 64) + (2 * 64) + (2 * 64) + (2 * 64) + 5 + 10, bytes);
    }

    [Fact]
    public void EstimateWorkingSetBytes_OverflowsInsteadOfWrapping()
    {
        Assert.Throws<OverflowException>(() =>
            Par2RepairService.EstimateWorkingSetBytes(
                cachedSourceBodyBytes: long.MaxValue,
                assembledPresentSliceCount: 1,
                recoverySliceCount: 0,
                reconstructedSliceCount: 0,
                stagedPatchBytes: 1,
                sliceSize: 1));
    }
}
