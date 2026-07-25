using NzbWebDAV.Streams;

namespace NzbWebDAV.Tests.Streams;

public class SegmentSizesTests
{
    [Theory]
    [MemberData(nameof(InvalidExactSizes))]
    public void Validate_InvalidSizes_ReturnsEmpty(long[] sizes, int segmentCount)
    {
        Assert.True(SegmentSizes.Validate(sizes, segmentCount).IsEmpty);
    }

    public static TheoryData<long[], int> InvalidExactSizes =>
        new()
        {
            { [], 0 },
            { [1, 2], 0 },
            { [1], 2 },
            { [1, 2, 3], 2 },
            { [1, 0], 2 },
            { [1, -1], 2 },
        };

    [Fact]
    public void Validate_MatchingPositiveSizes_PreservesValues()
    {
        long[] sizes = [17, 23, 5];

        var validated = SegmentSizes.Validate(sizes, sizes.Length);

        Assert.Equal(sizes, validated.ToArray());
    }

    [Fact]
    public void UniformObservedSizes_CanFillANonFinalMissingSegment()
    {
        var sizes = new SegmentSizes(default, segmentCount: 4);
        sizes.RecordObservedSize(segmentIndex: 0, size: 128);
        sizes.RecordObservedSize(segmentIndex: 1, size: 128);

        Assert.True(sizes.TryGetFillLength(2, out var length, out var isExact));
        Assert.Equal(128, length);
        Assert.False(isExact);
    }

    [Fact]
    public void ConflictingObservedSizes_PermanentlyDisableInferredFills()
    {
        var sizes = new SegmentSizes(default, segmentCount: 5);
        sizes.RecordObservedSize(segmentIndex: 0, size: 128);
        sizes.RecordObservedSize(segmentIndex: 1, size: 127);
        sizes.RecordObservedSize(segmentIndex: 2, size: 128);

        Assert.False(sizes.TryGetFillLength(3, out var length, out var isExact));
        Assert.Equal(0, length);
        Assert.False(isExact);
    }

    [Fact]
    public void FinalSegment_IsNeitherObservedNorFilledFromAnObservation()
    {
        var sizes = new SegmentSizes(default, segmentCount: 3);
        sizes.RecordObservedSize(segmentIndex: 2, size: 12);

        Assert.False(sizes.TryGetFillLength(0, out _, out _));

        sizes.RecordObservedSize(segmentIndex: 0, size: 128);

        Assert.False(sizes.TryGetFillLength(2, out _, out _));
    }

    [Fact]
    public void ExactSize_WinsOverObservedSize()
    {
        var sizes = new SegmentSizes(new long[] { 101, 102, 7 }, segmentCount: 3);
        sizes.RecordObservedSize(segmentIndex: 0, size: 128);

        Assert.True(sizes.TryGetFillLength(1, out var length, out var isExact));
        Assert.Equal(102, length);
        Assert.True(isExact);
    }

    [Fact]
    public void MissingFirstSegment_WithoutExactOrObservedSize_CannotBeFilled()
    {
        var sizes = new SegmentSizes(default, segmentCount: 3);

        Assert.False(sizes.TryGetFillLength(0, out var length, out var isExact));
        Assert.Equal(0, length);
        Assert.False(isExact);
    }

    [Fact]
    public async Task ConcurrentUniformObservations_ProduceTheObservedFillLength()
    {
        var sizes = new SegmentSizes(default, segmentCount: 102);

        await Task.WhenAll(Enumerable.Range(0, 100)
            .Select(index => Task.Run(() => sizes.RecordObservedSize(index, 256))));

        Assert.True(sizes.TryGetFillLength(100, out var length, out var isExact));
        Assert.Equal(256, length);
        Assert.False(isExact);
    }

    [Fact]
    public async Task ConcurrentMixedObservations_NeverLeaveAnInventedFillLength()
    {
        var sizes = new SegmentSizes(default, segmentCount: 102);

        await Task.WhenAll(Enumerable.Range(0, 100)
            .Select(index => Task.Run(() =>
                sizes.RecordObservedSize(index, index % 2 == 0 ? 255 : 256))));

        Assert.False(sizes.TryGetFillLength(100, out var length, out _));
        Assert.Equal(0, length);
    }
}
