using NzbWebDAV.Services.Repair;

namespace NzbWebDAV.Tests.Services.Repair;

public class SegmentDamageClassifierTests
{
    private static readonly SegmentDamageCaps Caps = new(MaxConsecutiveMissing: 2, MaxTotalMissing: 5, MaxMissingBytePercent: 1);

    [Fact]
    public void Classify_NoMissingSegments_IsClean()
    {
        var verdict = Classify(
            [], [100L, 100, 100, 100], MediaContainerClass.ResyncTolerant, out var reason);

        Assert.Equal(SegmentDamageVerdict.Clean, verdict);
        Assert.Contains("0 missing segment(s)", reason);
    }

    [Fact]
    public void Classify_BoundedTailHole_IsDegraded()
    {
        var verdict = Classify(
            [3], [100L, 100, 100, 1], MediaContainerClass.ResyncTolerant, out var reason);

        Assert.Equal(SegmentDamageVerdict.Degraded, verdict);
        Assert.Contains("largest run 1", reason);
    }

    [Fact]
    public void Classify_FastStartMp4_MidFileMiss_IsDegraded()
    {
        var verdict = Classify(
            [2], [100L, 100, 1, 100], MediaContainerClass.Mp4FastStart, out _);

        Assert.Equal(SegmentDamageVerdict.Degraded, verdict);
    }

    [Theory]
    [InlineData(MediaContainerClass.Unknown)]
    [InlineData(MediaContainerClass.Mp4MoovAtEnd)]
    public void Classify_UnsafeContainer_IsFailed(MediaContainerClass containerClass)
    {
        var verdict = Classify(
            [2], [100L, 100, 100, 100], containerClass, out _);

        Assert.Equal(SegmentDamageVerdict.Failed, verdict);
    }

    [Fact]
    public void Classify_HoleAtSegmentZero_IsFailed()
    {
        var verdict = Classify(
            [0], [100L, 100, 100, 100], MediaContainerClass.Mp4FastStart, out _);

        Assert.Equal(SegmentDamageVerdict.Failed, verdict);
    }

    [Fact]
    public void Classify_FastStart_HoleOverlappingMoovExtent_IsFailed()
    {
        // Segment 1 starts at 100, which is inside [0, 150). Hole is 1 byte so the
        // fail comes from the moov overlap, not MaxMissingBytePercent.
        var verdict = Classify(
            [1], [100L, 1, 100, 100], MediaContainerClass.Mp4FastStart, out _,
            criticalHeadEndExclusive: 150);

        Assert.Equal(SegmentDamageVerdict.Failed, verdict);
    }

    [Fact]
    public void Classify_FastStart_HoleStartingExactlyAtMoovExtent_IsDegraded()
    {
        var verdict = Classify(
            [1], [100L, 1, 100, 100], MediaContainerClass.Mp4FastStart, out _,
            criticalHeadEndExclusive: 100);

        Assert.Equal(SegmentDamageVerdict.Degraded, verdict);
    }

    [Fact]
    public void Classify_FastStart_HoleStartingAfterMoovExtent_IsDegraded()
    {
        var verdict = Classify(
            [2], [100L, 100, 1, 100], MediaContainerClass.Mp4FastStart, out _,
            criticalHeadEndExclusive: 150);

        Assert.Equal(SegmentDamageVerdict.Degraded, verdict);
    }

    [Fact]
    public void Classify_FastStart_ZeroExtent_OnlyGuardsSegmentZero()
    {
        var midFile = Classify(
            [1], [100L, 1, 100, 100], MediaContainerClass.Mp4FastStart, out _,
            criticalHeadEndExclusive: 0);
        Assert.Equal(SegmentDamageVerdict.Degraded, midFile);

        var head = Classify(
            [0], [100L, 100, 100, 100], MediaContainerClass.Mp4FastStart, out _,
            criticalHeadEndExclusive: 0);
        Assert.Equal(SegmentDamageVerdict.Failed, head);
    }

    [Fact]
    public void Classify_RunAtCap_IsDegraded()
    {
        var verdict = Classify(
            [5, 6], Sizes(20, (5, 10), (6, 10)), MediaContainerClass.ResyncTolerant, out _);

        Assert.Equal(SegmentDamageVerdict.Degraded, verdict);
    }

    [Fact]
    public void Classify_RunOverCap_IsFailed()
    {
        var verdict = Classify(
            [5, 6, 7], Sizes(20, (5, 10), (6, 10), (7, 10)), MediaContainerClass.ResyncTolerant, out var reason);

        Assert.Equal(SegmentDamageVerdict.Failed, verdict);
        Assert.Contains("largest run 3", reason);
    }

    [Fact]
    public void Classify_TotalAtCap_IsDegraded()
    {
        var verdict = Classify(
            [2, 5, 8, 11, 14], Sizes(20, (2, 10), (5, 10), (8, 10), (11, 10), (14, 10)),
            MediaContainerClass.ResyncTolerant, out _);

        Assert.Equal(SegmentDamageVerdict.Degraded, verdict);
    }

    [Fact]
    public void Classify_TotalOverCap_IsFailed()
    {
        var verdict = Classify(
            [2, 5, 8, 11, 14, 17],
            Sizes(20, (2, 10), (5, 10), (8, 10), (11, 10), (14, 10), (17, 10)),
            MediaContainerClass.ResyncTolerant, out _);

        Assert.Equal(SegmentDamageVerdict.Failed, verdict);
    }

    [Fact]
    public void Classify_ByteShareAtCap_IsDegraded()
    {
        // Exactly 1.0% of the file's bytes missing: the cap comparison is strictly-greater.
        var verdict = Classify(
            [1], [9900L, 100], MediaContainerClass.ResyncTolerant, out _);

        Assert.Equal(SegmentDamageVerdict.Degraded, verdict);
    }

    [Fact]
    public void Classify_ByteShareOverCap_IsFailed()
    {
        var verdict = Classify(
            [1], [9899L, 101], MediaContainerClass.ResyncTolerant, out var reason);

        Assert.Equal(SegmentDamageVerdict.Failed, verdict);
        Assert.Contains("% of file", reason);
    }

    [Fact]
    public void Classify_ReasonReportsCountRunAndByteShare()
    {
        Classify(
            [4, 5], Sizes(10, (4, 100), (5, 100)), MediaContainerClass.ResyncTolerant,
            out var reason);

        Assert.Contains("2 missing segment(s)", reason);
        Assert.Contains("largest run 2", reason);
        Assert.Contains("of file", reason);
    }

    [Fact]
    public void Classify_MismatchedSizeCount_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            SegmentDamageClassifier.Classify(
                [1], 4, [100L, 100, 100], Starts([100L, 100, 100, 100]),
                MediaContainerClass.ResyncTolerant, Caps, 0, out _));
    }

    [Fact]
    public void Classify_UnsortedIndices_ClassifiesSameAsSorted()
    {
        var sorted = Classify(
            [2, 3], Sizes(10, (2, 10), (3, 10)), MediaContainerClass.ResyncTolerant, out _);
        var unsorted = Classify(
            [3, 2], Sizes(10, (2, 10), (3, 10)), MediaContainerClass.ResyncTolerant, out var reason);

        Assert.Equal(sorted, unsorted);
        Assert.Contains("largest run 2", reason);
    }

    [Fact]
    public void Classify_DuplicateIndices_CountOnce()
    {
        var verdict = Classify(
            [2, 2], Sizes(10, (2, 10)), MediaContainerClass.ResyncTolerant, out var reason);

        Assert.Equal(SegmentDamageVerdict.Degraded, verdict);
        Assert.Contains("1 missing segment(s)", reason);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    public void Classify_OutOfRangeIndex_Throws(int index)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SegmentDamageClassifier.Classify(
                [index], 4, [100L, 100, 100, 100], Starts([100L, 100, 100, 100]),
                MediaContainerClass.ResyncTolerant, Caps, 0, out _));
    }

    private static SegmentDamageVerdict Classify(
        int[] missing,
        long[] sizes,
        MediaContainerClass containerClass,
        out string reason,
        long criticalHeadEndExclusive = 0) =>
        SegmentDamageClassifier.Classify(
            missing, sizes.Length, sizes, Starts(sizes), containerClass, Caps,
            criticalHeadEndExclusive, out reason);

    private static long[] Starts(IReadOnlyList<long> sizes)
    {
        var starts = new long[sizes.Count];
        long offset = 0;
        for (var i = 0; i < sizes.Count; i++)
        {
            starts[i] = offset;
            offset += sizes[i];
        }

        return starts;
    }

    private static long[] Sizes(int count, params (int Index, long Size)[] holes)
    {
        var sizes = Enumerable.Repeat(100_000L, count).ToArray();
        foreach (var (index, size) in holes)
            sizes[index] = size;
        return sizes;
    }
}
