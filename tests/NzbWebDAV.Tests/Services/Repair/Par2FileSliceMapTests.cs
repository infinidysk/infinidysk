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
    public void EstimateMaxOverlappingSegmentBytes_UsesOnlyTheCurrentSliceWindow()
    {
        var ranges = new[]
        {
            LongRange.FromStartAndSize(0, 40),
            LongRange.FromStartAndSize(40, 40),
            LongRange.FromStartAndSize(80, 48),
        };

        Assert.True(Par2FileSliceMap.TryCreate(
            128, 0, 64, 2, ranges, out var map, out var error));
        Assert.Null(error);

        // Slice 0 overlaps segments 0 and 1; slice 1 overlaps 1 and 2.
        // The source cache must retain the larger 88-byte window, not all 128 bytes.
        Assert.Equal(88, map!.EstimateMaxOverlappingSegmentBytes());
    }

    [Fact]
    public void EstimateMaxOverlappingSegmentBytes_SupportsUnorderedOverlappingRanges()
    {
        var ranges = new[]
        {
            LongRange.FromStartAndSize(64, 64),
            LongRange.FromStartAndSize(0, 80),
        };

        Assert.True(Par2FileSliceMap.TryCreate(
            128, 0, 64, 2, ranges, out var map, out var error));
        Assert.Null(error);

        // Both retained bodies overlap slice 1 even though the persisted ranges are
        // unordered and overlapping.
        Assert.Equal(144, map!.EstimateMaxOverlappingSegmentBytes());
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
    public void EstimateWorkingSetBytes_IncludesPeakSourceWindowRecoveryAndPatches()
    {
        var bytes = Par2RepairService.EstimateWorkingSetBytes(
            peakSourceBodyBytes: 10,
            recoverySliceCount: 2,
            reconstructedSliceCount: 2,
            stagedPatchBytes: 5,
            sliceSize: 64);
        Assert.Equal(10 + (2 * 64) + (2 * 64) + (2 * 64) + (2 * 64) + 5, bytes);
    }

    [Fact]
    public void EstimateWorkingSetBytes_OverflowsInsteadOfWrapping()
    {
        Assert.Throws<OverflowException>(() =>
            Par2RepairService.EstimateWorkingSetBytes(
                peakSourceBodyBytes: long.MaxValue,
                recoverySliceCount: 0,
                reconstructedSliceCount: 0,
                stagedPatchBytes: 1,
                sliceSize: 1));
    }
}
