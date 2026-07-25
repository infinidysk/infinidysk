using NzbWebDAV.Models;
using NzbWebDAV.Streams;
using NzbWebDAV.Tests.Fakes;

namespace NzbWebDAV.Tests.Streams;

public class StreamFidelityTests
{
    private const int Seed = 0x5EED;
    private static readonly int[] SegmentLengths = [1, 127, 4093, 17, 8192, 3001, 16384, 257, 7777];

    [Theory]
    [InlineData(0, false, false)]
    [InlineData(0, false, true)]
    [InlineData(0, true, false)]
    [InlineData(0, true, true)]
    [InlineData(4, false, false)]
    [InlineData(4, false, true)]
    [InlineData(4, true, false)]
    [InlineData(4, true, true)]
    public async Task RoundTrip_IrregularSegments_AreByteForByteIdentical(
        int articleBufferSize,
        bool usePipelinedBodyRequests,
        bool includeExactRanges)
    {
        var fixture = CreateFixture();
        var client = fixture.CreateClient();
        await using var stream = new NzbFileStream(
            fixture.SegmentIds,
            fixture.Source.Length,
            client,
            articleBufferSize,
            includeExactRanges ? fixture.Ranges : null,
            usePipelinedBodyRequests,
            fileName: $"fidelity-seed-{Seed}.bin");
        using var output = new MemoryStream(fixture.Source.Length);

        await stream.CopyToAsync(output);

        Assert.True(
            fixture.Source.AsSpan().SequenceEqual(output.ToArray()),
            $"Byte mismatch for seed {Seed}, buffer {articleBufferSize}, " +
            $"pipelined {usePipelinedBodyRequests}, exact ranges {includeExactRanges}.");
    }

    [Theory]
    [InlineData(0, false, false)]
    [InlineData(4, false, true)]
    [InlineData(4, true, false)]
    public async Task RandomSeeks_ReturnExactSourceSuffix(
        int articleBufferSize,
        bool usePipelinedBodyRequests,
        bool includeExactRanges)
    {
        var fixture = CreateFixture();
        var offsets = BuildSeekOffsets(fixture.Ranges, fixture.Source.Length);

        foreach (var offset in offsets)
        {
            var client = fixture.CreateClient();
            await using var stream = new NzbFileStream(
                fixture.SegmentIds,
                fixture.Source.Length,
                client,
                articleBufferSize,
                includeExactRanges ? fixture.Ranges : null,
                usePipelinedBodyRequests,
                fileName: $"seek-fidelity-seed-{Seed}.bin");
            stream.Seek(offset, SeekOrigin.Begin);
            using var output = new MemoryStream(fixture.Source.Length - offset);

            await stream.CopyToAsync(output);

            Assert.True(
                fixture.Source.AsSpan(offset).SequenceEqual(output.ToArray()),
                $"Seek mismatch for seed {Seed} at offset {offset}, buffer {articleBufferSize}, " +
                $"pipelined {usePipelinedBodyRequests}, exact ranges {includeExactRanges}.");
            Assert.Equal(fixture.Source.Length, stream.Position);
        }
    }

    private static int[] BuildSeekOffsets(IReadOnlyList<LongRange> ranges, int fileSize)
    {
        var offsets = new HashSet<int> { 0, 1, fileSize - 1 };
        foreach (var range in ranges)
        {
            var boundary = checked((int)range.StartInclusive);
            if (boundary > 0) offsets.Add(boundary - 1);
            if (boundary < fileSize) offsets.Add(boundary);
            if (boundary + 1 < fileSize) offsets.Add(boundary + 1);
        }

        var random = new Random(Seed + 1);
        for (var i = 0; i < 20; i++)
            offsets.Add(random.Next(fileSize));

        return offsets.Order().ToArray();
    }

    private static FidelityFixture CreateFixture()
    {
        var random = new Random(Seed);
        var source = new byte[SegmentLengths.Sum()];
        random.NextBytes(source);

        var segmentIds = new string[SegmentLengths.Length];
        var ranges = new LongRange[SegmentLengths.Length];
        var segments = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var position = 0;
        for (var i = 0; i < SegmentLengths.Length; i++)
        {
            var segmentId = $"segment-{i:D2}";
            var length = SegmentLengths[i];
            segmentIds[i] = segmentId;
            ranges[i] = new LongRange(position, position + length);
            segments[segmentId] = source.AsSpan(position, length).ToArray();
            position += length;
        }

        return new FidelityFixture(source, segmentIds, ranges, segments);
    }

    private sealed record FidelityFixture(
        byte[] Source,
        string[] SegmentIds,
        LongRange[] Ranges,
        IReadOnlyDictionary<string, byte[]> Segments)
    {
        public FakeNntpClient CreateClient() =>
            new(
                Segments,
                useCachedYencStreams: true,
                SegmentIds.Zip(Ranges).ToDictionary(pair => pair.First, pair => pair.Second));
    }
}
