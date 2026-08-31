using NzbWebDAV.Models;

namespace NzbWebDAV.Streams;

/// <summary>
/// Exact contiguous work needed to satisfy a private finite byte range. All ranges use
/// half-open arithmetic and must already have passed <see cref="NzbFileStream"/>'s
/// persisted-segment-range validation.
/// </summary>
internal readonly record struct FiniteRangeSegmentPlan(
    int FirstSegmentIndex,
    int SegmentCount,
    long RequestedBytes,
    long ExactPlannedSegmentBytes,
    long PrefixBytes,
    long FinalSegmentSlackBytes,
    long HeadContributionBytes,
    long RemainderBudget,
    int RemainderSegmentCount,
    long ExactPlannedRemainderBytes)
{
    internal bool HasBufferedRemainder => RemainderBudget > 0;

    internal static bool TryCreate(
        IReadOnlyList<LongRange>? ranges,
        int firstSegmentIndex,
        long rangeStart,
        long readBudget,
        long fileSize,
        out FiniteRangeSegmentPlan plan,
        out FiniteRangePlanUnavailableReason reason)
    {
        plan = default;
        if (readBudget <= 0)
        {
            reason = FiniteRangePlanUnavailableReason.ZeroBudget;
            return false;
        }

        if (rangeStart < 0 || rangeStart >= fileSize)
        {
            reason = FiniteRangePlanUnavailableReason.StartOutsideFile;
            return false;
        }

        long endExclusive;
        try
        {
            endExclusive = checked(rangeStart + readBudget);
        }
        catch (OverflowException)
        {
            reason = FiniteRangePlanUnavailableReason.ArithmeticOverflow;
            return false;
        }

        if (endExclusive > fileSize)
        {
            reason = FiniteRangePlanUnavailableReason.EndBeyondFileSize;
            return false;
        }

        if (!AreValid(ranges, fileSize) ||
            (uint)firstSegmentIndex >= (uint)ranges!.Count ||
            !ranges[firstSegmentIndex].Contains(rangeStart))
        {
            reason = FiniteRangePlanUnavailableReason.InvalidRanges;
            return false;
        }

        var lastRequestedByte = endExclusive - 1;
        var lastSegmentIndex = FindContainingIndex(ranges, firstSegmentIndex, lastRequestedByte);
        if (lastSegmentIndex < firstSegmentIndex)
        {
            reason = FiniteRangePlanUnavailableReason.InvalidRanges;
            return false;
        }

        var first = ranges[firstSegmentIndex];
        var last = ranges[lastSegmentIndex];
        var prefixBytes = rangeStart - first.StartInclusive;
        var headAvailable = first.Count - prefixBytes;
        if (prefixBytes < 0 || headAvailable <= 0)
        {
            reason = FiniteRangePlanUnavailableReason.InvalidRanges;
            return false;
        }

        var segmentCount = checked(lastSegmentIndex - firstSegmentIndex + 1);
        var headContribution = Math.Min(readBudget, headAvailable);
        var remainderBudget = checked(readBudget - headContribution);
        var exactBytes = SumLengths(ranges, firstSegmentIndex, segmentCount);
        var remainderCount = remainderBudget > 0 ? segmentCount - 1 : 0;
        var remainderBytes = remainderCount > 0
            ? checked(exactBytes - first.Count)
            : 0;

        plan = new FiniteRangeSegmentPlan(
            firstSegmentIndex,
            segmentCount,
            readBudget,
            exactBytes,
            prefixBytes,
            checked(last.EndExclusive - endExclusive),
            headContribution,
            remainderBudget,
            remainderCount,
            remainderBytes);
        reason = FiniteRangePlanUnavailableReason.None;
        return true;
    }

    private static bool AreValid(IReadOnlyList<LongRange>? ranges, long fileSize)
    {
        if (ranges is not { Count: > 0 } || fileSize < 0 ||
            ranges[0].StartInclusive != 0 || ranges[ranges.Count - 1].EndExclusive != fileSize)
            return false;

        for (var index = 0; index < ranges.Count; index++)
        {
            var range = ranges[index];
            if (range.StartInclusive < 0 || range.EndExclusive <= range.StartInclusive ||
                range.EndExclusive > fileSize ||
                (index > 0 && ranges[index - 1].EndExclusive != range.StartInclusive))
                return false;
        }

        return true;
    }

    private static int FindContainingIndex(
        IReadOnlyList<LongRange> ranges,
        int firstIndex,
        long position)
    {
        for (var index = firstIndex; index < ranges.Count; index++)
        {
            if (ranges[index].Contains(position))
                return index;
            if (ranges[index].StartInclusive > position)
                break;
        }

        return -1;
    }

    private static long SumLengths(IReadOnlyList<LongRange> ranges, int firstIndex, int count)
    {
        var bytes = 0L;
        for (var index = 0; index < count; index++)
            bytes = checked(bytes + ranges[firstIndex + index].Count);
        return bytes;
    }
}

internal enum FiniteRangePlanUnavailableReason
{
    None,
    ZeroBudget,
    StartOutsideFile,
    EndBeyondFileSize,
    ArithmeticOverflow,
    InvalidRanges,
    SchedulerDisabled,
    MissingSchedulingContext,
    UnbufferedOrNonPipelined,
    NoFiniteReadBudget,
    MissingExactMetadata,
}
