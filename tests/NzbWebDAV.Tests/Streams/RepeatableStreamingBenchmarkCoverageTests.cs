using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Models;
using NzbWebDAV.Streams;
using NzbWebDAV.Tests.Fakes;

namespace NzbWebDAV.Tests.Streams;

public sealed class RepeatableStreamingBenchmarkCoverageTests
{
    private const int SegmentSize = 8 * 1024;
    private const int SegmentCount = 6;

    [Fact]
    public async Task DeterministicFixture_CoversColdWarmRangeTailSeekAndDeadArticlePaths()
    {
        var fixture = CreateFixture();
        var cacheDir = Path.Join(Path.GetTempPath(), "nzbdav-streaming-benchmark-" + Guid.NewGuid().ToString("N"));

        try
        {
            var transport = fixture.CreateClient();
            using var cached = new SegmentCacheNntpClient(
                transport,
                cacheDir,
                maxBytes: fixture.Source.Length * 2L);
            await WaitForCatalogAsync(cached);

            await AssertReadMatchesAsync(transport, fixture, offset: 0, count: fixture.Source.Length);
            var coldTransportRequests = transport.BodyRequestCount;
            var coldTransportBytes = transport.RequestedSegmentIds.Sum(id => fixture.Segments[id].Length);
            Assert.Equal(SegmentCount, coldTransportRequests);
            Assert.Equal(fixture.Source.Length, coldTransportBytes);

            await PrimeCacheAsync(cached, fixture);
            var requestsBeforeWarmRead = transport.BodyRequestCount;
            await AssertReadMatchesAsync(cached, fixture, offset: 0, count: fixture.Source.Length);
            Assert.Equal(requestsBeforeWarmRead, transport.BodyRequestCount);

            await AssertReadMatchesAsync(cached, fixture, offset: SegmentSize + 127, count: 1024);
            await AssertReadMatchesAsync(cached, fixture, offset: fixture.Source.Length - 257, count: 257);

            await using (var stream = fixture.CreateStream(cached))
            {
                foreach (var offset in new[] { 17, SegmentSize * 3 + 91, SegmentSize * 2 + 7 })
                {
                    stream.Seek(offset, SeekOrigin.Begin);
                    var buffer = new byte[513];
                    var read = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: true);
                    Assert.Equal(fixture.Source.AsSpan(offset, read).ToArray(), buffer);
                }
            }

            var deadSegments = fixture.Segments
                .Where(pair => pair.Key != fixture.SegmentIds[2])
                .ToDictionary(pair => pair.Key, pair => pair.Value);
            var deadTransport = new FakeNntpClient(
                deadSegments,
                useCachedYencStreams: true,
                segmentRanges: fixture.RangesById);
            await using var deadStream = fixture.CreateStream(deadTransport);
            deadStream.Seek(SegmentSize * 2, SeekOrigin.Begin);
            var deadBuffer = new byte[SegmentSize];
            Assert.Equal(SegmentSize, await deadStream.ReadAtLeastAsync(
                deadBuffer, deadBuffer.Length, throwOnEndOfStream: true));
            Assert.Equal(new byte[SegmentSize], deadBuffer);
            Assert.Contains(fixture.SegmentIds[2], deadTransport.RequestedSegmentIds);
        }
        finally
        {
            if (Directory.Exists(cacheDir))
                Directory.Delete(cacheDir, recursive: true);
        }
    }

    private static async Task AssertReadMatchesAsync(
        INntpClient client,
        Fixture fixture,
        int offset,
        int count)
    {
        await using var stream = fixture.CreateStream(client);
        stream.Seek(offset, SeekOrigin.Begin);
        var buffer = new byte[count];
        var read = await stream.ReadAtLeastAsync(buffer, count, throwOnEndOfStream: true);
        Assert.Equal(count, read);
        Assert.Equal(fixture.Source.AsSpan(offset, count).ToArray(), buffer);
    }

    private static async Task PrimeCacheAsync(SegmentCacheNntpClient client, Fixture fixture)
    {
        foreach (var segmentId in fixture.SegmentIds)
        {
            var response = await client.DecodedBodyAsync(segmentId, CancellationToken.None);
            using var body = response.Stream
                ?? throw new InvalidOperationException($"Cache prime returned no body for {segmentId}.");
            await body.CopyToAsync(Stream.Null);
        }
    }

    private static async Task WaitForCatalogAsync(SegmentCacheNntpClient client)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!client.IsCatalogReady && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        Assert.True(client.IsCatalogReady, "Segment cache catalog did not become ready.");
    }

    private static Fixture CreateFixture()
    {
        var source = new byte[SegmentSize * SegmentCount];
        new Random(1025).NextBytes(source);
        var segmentIds = Enumerable.Range(0, SegmentCount).Select(index => $"benchmark-{index:D2}").ToArray();
        var ranges = Enumerable.Range(0, SegmentCount)
            .Select(index => new LongRange(index * SegmentSize, (index + 1L) * SegmentSize))
            .ToArray();
        var segments = segmentIds
            .Select((id, index) => KeyValuePair.Create(
                id, source.AsSpan(index * SegmentSize, SegmentSize).ToArray()))
            .ToDictionary();
        var rangesById = segmentIds.Zip(ranges).ToDictionary(pair => pair.First, pair => pair.Second);
        return new Fixture(source, segmentIds, ranges, segments, rangesById);
    }

    private sealed record Fixture(
        byte[] Source,
        string[] SegmentIds,
        LongRange[] Ranges,
        IReadOnlyDictionary<string, byte[]> Segments,
        IReadOnlyDictionary<string, LongRange> RangesById)
    {
        public FakeNntpClient CreateClient() =>
            new(Segments, useCachedYencStreams: true, segmentRanges: RangesById);

        public NzbFileStream CreateStream(INntpClient client) =>
            new(SegmentIds, Source.Length, client, articleBufferSize: 0, segmentByteRanges: Ranges,
                usePipelinedBodyRequests: false, fileName: "repeatable-streaming-benchmark.bin");
    }
}
