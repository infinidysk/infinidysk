using NzbWebDAV.Models;

namespace NzbWebDAV.Services.Repair;

/// <summary>
/// Immutable mapping from a target file's NZB segments onto PAR2 global slice indexes.
/// Overlap uses half-open byte ranges; zero-length segments contribute no slices.
/// </summary>
internal sealed class Par2FileSliceMap
{
    public long FileLength { get; }
    public int GlobalSliceBase { get; }
    public int SliceSize { get; }
    public int SliceCount { get; }
    public IReadOnlyList<LongRange> SegmentRanges { get; }

    private Par2FileSliceMap(
        long fileLength,
        int globalSliceBase,
        int sliceSize,
        int sliceCount,
        LongRange[] segmentRanges)
    {
        FileLength = fileLength;
        GlobalSliceBase = globalSliceBase;
        SliceSize = sliceSize;
        SliceCount = sliceCount;
        SegmentRanges = segmentRanges;
    }

    public static bool TryCreate(
        long fileLength,
        int globalSliceBase,
        int sliceSize,
        int sliceCount,
        LongRange[] segmentRanges,
        out Par2FileSliceMap? map,
        out string? error)
    {
        map = null;
        error = null;

        if (fileLength < 0)
        {
            error = "Target file length is negative.";
            return false;
        }

        if (sliceSize <= 0)
        {
            error = "PAR2 slice size must be positive.";
            return false;
        }

        if (globalSliceBase < 0)
        {
            error = "PAR2 global slice base is negative.";
            return false;
        }

        if (sliceCount < 0)
        {
            error = "PAR2 slice count is negative.";
            return false;
        }

        var expectedSlices = fileLength == 0
            ? 0
            : (int)((fileLength + sliceSize - 1) / sliceSize);
        if (sliceCount != expectedSlices)
        {
            error = "PAR2 slice count does not match the target file length.";
            return false;
        }

        for (var i = 0; i < segmentRanges.Length; i++)
        {
            var range = segmentRanges[i];
            if (range.Count < 0)
            {
                error = $"Segment {i} has a negative byte range.";
                return false;
            }

            if (range.Count == 0)
                continue;

            if (range.StartInclusive < 0 || range.EndExclusive > fileLength)
            {
                error = $"Segment {i} maps outside the target file.";
                return false;
            }
        }

        map = new Par2FileSliceMap(fileLength, globalSliceBase, sliceSize, sliceCount, segmentRanges);
        return true;
    }

    public IEnumerable<int> GlobalSlicesForSegment(int segmentIndex)
    {
        var range = SegmentRanges[segmentIndex];
        if (range.Count == 0)
            yield break;

        long firstSlice;
        long lastSlice;
        try
        {
            firstSlice = range.StartInclusive / SliceSize;
            lastSlice = (range.EndExclusive - 1) / SliceSize;
        }
        catch (OverflowException)
        {
            yield break;
        }

        for (var local = firstSlice; local <= lastSlice; local++)
            yield return checked(GlobalSliceBase + (int)local);
    }

    public IEnumerable<int> SegmentIndicesForGlobalSlice(int globalSlice)
    {
        var local = globalSlice - GlobalSliceBase;
        if ((uint)local >= (uint)SliceCount)
            yield break;

        var start = (long)local * SliceSize;
        var end = Math.Min(start + SliceSize, FileLength);
        for (var i = 0; i < SegmentRanges.Count; i++)
        {
            var range = SegmentRanges[i];
            if (range.Count == 0)
                continue;
            if (range.StartInclusive < end && range.EndExclusive > start)
                yield return i;
        }
    }

    public LongRange SliceFileRange(int globalSlice)
    {
        var local = globalSlice - GlobalSliceBase;
        var start = (long)local * SliceSize;
        var count = Math.Min(SliceSize, Math.Max(0, FileLength - start));
        return LongRange.FromStartAndSize(start, count);
    }
}
