using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Models;
using NzbWebDAV.Streams;
using NzbWebDAV.Tests.Fakes;

namespace NzbWebDAV.Tests.Streams;

public sealed class RepeatableStreamingBenchmarkCoverageTests
{
    private const int SegmentSize = 8 * 1024;
    private const int SegmentCount = 6;
    private const int RangeProbeOffset = SegmentSize + 127;
    private const int RangeProbeCount = 1024;
    private const int TailProbeCount = 257;
    private const int RangeProbeTransportRequests = 0;
    private const int RangeProbeTransportBytes = 0;
    private const int TailProbeTransportRequests = 0;
    private const int TailProbeTransportBytes = 0;
    private const int SeekTransportRequests = 0;
    private const int SeekTransportBytes = 0;
    private const int DeadArticleTransportRequests = 2;
    private const int DeadArticleTransportBytes = 0;
    private const string TransportContractMessage =
        "Intentional transport-contract change ⇒ update this constant and the committed baseline.";

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
            var coldTransportBytes = TransportBytes(transport, fixture.Segments);
            Assert.Equal(SegmentCount, coldTransportRequests);
            Assert.Equal(fixture.Source.Length, coldTransportBytes);

            await PrimeCacheAsync(cached, fixture);
            var requestsBeforeWarmRead = transport.BodyRequestCount;
            await AssertReadMatchesAsync(cached, fixture, offset: 0, count: fixture.Source.Length);
            Assert.Equal(requestsBeforeWarmRead, transport.BodyRequestCount);

            await AssertTransportDeltaAsync(
                transport, fixture.Segments, "range-probe", RangeProbeTransportRequests, RangeProbeTransportBytes,
                () => AssertReadMatchesAsync(cached, fixture, RangeProbeOffset, RangeProbeCount));
            await AssertTransportDeltaAsync(
                transport, fixture.Segments, "tail-probe", TailProbeTransportRequests, TailProbeTransportBytes,
                () => AssertReadMatchesAsync(
                    cached, fixture, fixture.Source.Length - TailProbeCount, TailProbeCount));

            var requestsBeforeSeeks = transport.BodyRequestCount;
            var bytesBeforeSeeks = TransportBytes(transport, fixture.Segments);
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

            AssertTransportContract(
                "seeks",
                SeekTransportRequests,
                transport.BodyRequestCount - requestsBeforeSeeks,
                SeekTransportBytes,
                TransportBytes(transport, fixture.Segments) - bytesBeforeSeeks);

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
            AssertTransportContract(
                "dead-article",
                DeadArticleTransportRequests,
                deadTransport.BodyRequestCount,
                DeadArticleTransportBytes,
                TransportBytes(deadTransport, deadSegments));
        }
        finally
        {
            if (Directory.Exists(cacheDir))
                Directory.Delete(cacheDir, recursive: true);
        }
    }

    [Fact]
    public async Task MaxConsecutiveZeroFills_ZeroFillsUpToBoundThenAborts()
    {
        var fixture = CreateFixture();
        // Keep segment 0 so NzbFileStream does not fail-fast on the first article,
        // then remove the next MaxConsecutiveZeroFills segments.
        var missingIds = fixture.SegmentIds
            .Skip(1)
            .Take(GapFillLimits.MaxConsecutiveZeroFills)
            .ToHashSet(StringComparer.Ordinal);
        var remaining = fixture.Segments
            .Where(pair => !missingIds.Contains(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value);
        var transport = new FakeNntpClient(
            remaining,
            useCachedYencStreams: true,
            segmentRanges: fixture.RangesById);
        await using var stream = fixture.CreateStream(transport);
        var buffer = new byte[SegmentSize];

        Assert.Equal(SegmentSize, await stream.ReadAtLeastAsync(
            buffer, buffer.Length, throwOnEndOfStream: true));
        Assert.Equal(fixture.Source.AsSpan(0, SegmentSize).ToArray(), buffer);

        for (var index = 0; index < GapFillLimits.MaxConsecutiveZeroFills - 1; index++)
        {
            Assert.Equal(SegmentSize, await stream.ReadAtLeastAsync(
                buffer, buffer.Length, throwOnEndOfStream: true));
            Assert.Equal(new byte[SegmentSize], buffer);
        }

        await Assert.ThrowsAsync<UsenetArticleNotFoundException>(
            async () => await stream.ReadAtLeastAsync(
                buffer, buffer.Length, throwOnEndOfStream: false));
        Assert.Equal(1 + GapFillLimits.MaxConsecutiveZeroFills, transport.BodyRequestCount);
    }

    [Fact]
    public async Task ProductionShapedPipelinedFixture_WarmRereadUsesZeroTransport()
    {
        var fixture = CreateFixture();
        var cacheDir = Path.Join(Path.GetTempPath(), "nzbdav-streaming-pipelined-" + Guid.NewGuid().ToString("N"));
        var statistics = new SegmentCacheStatistics();

        try
        {
            var transport = fixture.CreateClient();
            using var cached = new SegmentCacheNntpClient(
                transport,
                cacheDir,
                maxBytes: fixture.Source.Length * 2L,
                usageTracker: null,
                metricsWriter: null,
                enumerateCacheFiles: null,
                statistics);
            await WaitForCatalogAsync(cached);

            var beforeCold = Capture(transport, fixture, statistics);
            await AssertProductionReadMatchesAsync(cached, fixture);
            var cold = Delta(beforeCold, Capture(transport, fixture, statistics), fixture.Source.Length);
            Assert.Equal(fixture.Source.Length, cold.Bytes);
            Assert.Equal(SegmentCount, cold.TransportRequests);
            Assert.Equal(fixture.Source.Length, cold.TransportBytes);
            Assert.Equal(2, cold.TransportBatchRequests);
            Assert.Equal(0, cold.CacheHits);
            Assert.Equal(SegmentCount, cold.CacheMisses);
            Assert.Equal(0, cold.CacheBytesServed);
            Assert.Equal(0, cold.CacheBatchBypassRequests);
            Assert.Equal(0, cold.CacheBatchBypassArticles);
            Assert.Equal(SegmentCount, cold.CacheWriteCommits);

            var beforeWarm = Capture(transport, fixture, statistics);
            await AssertProductionReadMatchesAsync(cached, fixture);
            var warm = Delta(beforeWarm, Capture(transport, fixture, statistics), fixture.Source.Length);
            Assert.Equal(fixture.Source.Length, warm.Bytes);
            AssertPipelinedWarmTransportContract(warm.TransportRequests, warm.TransportBytes);
            Assert.Equal(0, warm.TransportRequests);
            Assert.Equal(0, warm.TransportBytes);
            Assert.Equal(0, warm.TransportBatchRequests);
            Assert.Equal(6, warm.CacheHits);
            Assert.Equal(0, warm.CacheMisses);
            Assert.Equal(fixture.Source.Length, warm.CacheBytesServed);
            Assert.Equal(0, warm.CacheBatchBypassRequests);
            Assert.Equal(0, warm.CacheBatchBypassArticles);
            Assert.Equal(0, warm.CacheWriteCommits);

            var repeatBefore = Capture(transport, fixture, statistics);
            await AssertProductionReadMatchesAsync(cached, fixture);
            var repeat = Delta(repeatBefore, Capture(transport, fixture, statistics), fixture.Source.Length);
            Assert.Equal(warm.TransportRequests, repeat.TransportRequests);
            Assert.Equal(warm.TransportBytes, repeat.TransportBytes);
            Assert.Equal(warm.CacheHits, repeat.CacheHits);
            Assert.Equal(warm.CacheBatchBypassArticles, repeat.CacheBatchBypassArticles);
        }
        finally
        {
            if (Directory.Exists(cacheDir))
                Directory.Delete(cacheDir, recursive: true);
        }
    }

    /// <summary>
    /// After the batch-local cache overlay, a fully consumed production-shaped
    /// warm re-read must not touch the transport.
    /// </summary>
    internal static void AssertPipelinedWarmTransportContract(long transportRequests, long transportBytes)
    {
        Assert.True(
            transportRequests == 0 && transportBytes == 0,
            $"Pipelined warm re-read must use zero transport; was requests={transportRequests} bytes={transportBytes}.");
    }

    [Fact]
    public async Task PipelinedWarmReread_UsesZeroTransportRequestsAndBytes()
    {
        var fixture = CreateFixture();
        var cacheDir = Path.Join(Path.GetTempPath(), "nzbdav-streaming-pipelined-zero-" + Guid.NewGuid().ToString("N"));
        var statistics = new SegmentCacheStatistics();
        try
        {
            var transport = fixture.CreateClient();
            using var cached = new SegmentCacheNntpClient(
                transport, cacheDir, fixture.Source.Length * 2L, null, null, null, statistics);
            await WaitForCatalogAsync(cached);
            await AssertProductionReadMatchesAsync(cached, fixture);
            var before = Capture(transport, fixture, statistics);
            await AssertProductionReadMatchesAsync(cached, fixture);
            var warm = Delta(before, Capture(transport, fixture, statistics), fixture.Source.Length);
            Assert.Equal(0, warm.TransportRequests);
            Assert.Equal(0, warm.TransportBytes);
            Assert.Equal(0, warm.TransportBatchRequests);
            Assert.Equal(SegmentCount, warm.CacheHits);
            Assert.Equal(0, warm.CacheMisses);
            Assert.Equal(fixture.Source.Length, warm.CacheBytesServed);
            Assert.Equal(fixture.Source.Length, warm.Bytes);
        }
        finally
        {
            if (Directory.Exists(cacheDir))
                Directory.Delete(cacheDir, recursive: true);
        }
    }

    [Fact]
    public async Task PipelinedColdRead_CommitsEveryCompleteTouchedSegment()
    {
        var fixture = CreateFixture();
        var cacheDir = Path.Join(Path.GetTempPath(), "nzbdav-streaming-pipelined-cold-" + Guid.NewGuid().ToString("N"));
        var statistics = new SegmentCacheStatistics();
        try
        {
            var transport = fixture.CreateClient();
            using var cached = new SegmentCacheNntpClient(
                transport, cacheDir, fixture.Source.Length * 2L, null, null, null, statistics);
            await WaitForCatalogAsync(cached);
            var before = Capture(transport, fixture, statistics);
            await AssertProductionReadMatchesAsync(cached, fixture);
            var cold = Delta(before, Capture(transport, fixture, statistics), fixture.Source.Length);
            Assert.Equal(SegmentCount, cold.CacheWriteCommits);
            Assert.Equal(SegmentCount, cold.CacheMisses);
            Assert.Equal(0, cold.CacheBatchBypassRequests);
            Assert.Equal(fixture.Source.Length, cold.Bytes);
        }
        finally
        {
            if (Directory.Exists(cacheDir))
                Directory.Delete(cacheDir, recursive: true);
        }
    }

    [Fact]
    public async Task PipelinedWarmReread_UsesZeroTransportBatchRequests()
    {
        var fixture = CreateFixture();
        var cacheDir = Path.Join(Path.GetTempPath(), "nzbdav-streaming-pipelined-batch-" + Guid.NewGuid().ToString("N"));
        var statistics = new SegmentCacheStatistics();
        try
        {
            var transport = fixture.CreateClient();
            using var cached = new SegmentCacheNntpClient(
                transport, cacheDir, fixture.Source.Length * 2L, null, null, null, statistics);
            await WaitForCatalogAsync(cached);
            await AssertProductionReadMatchesAsync(cached, fixture);
            var before = Capture(transport, fixture, statistics);
            await AssertProductionReadMatchesAsync(cached, fixture);
            var warm = Delta(before, Capture(transport, fixture, statistics), fixture.Source.Length);
            Assert.Equal(0, warm.TransportBatchRequests);
        }
        finally
        {
            if (Directory.Exists(cacheDir))
                Directory.Delete(cacheDir, recursive: true);
        }
    }

    private static Counters Capture(
        FakeNntpClient transport,
        Fixture fixture,
        SegmentCacheStatistics statistics)
    {
        var snapshot = statistics.GetSnapshot();
        return new Counters(
            transport.BodyRequestCount,
            transport.BodyRequestCounts.Sum(pair =>
                fixture.Segments.TryGetValue(pair.Key, out var bytes) ? bytes.Length * (long)pair.Value : 0),
            transport.BatchRequestCount,
            snapshot.Hits,
            snapshot.Misses,
            snapshot.BytesServed,
            snapshot.BatchBypassRequests,
            snapshot.BatchBypassArticles,
            snapshot.WriteCommits,
            Bytes: 0);
    }

    private static Counters Delta(Counters before, Counters after, long bytes) =>
        new(
            after.TransportRequests - before.TransportRequests,
            after.TransportBytes - before.TransportBytes,
            after.TransportBatchRequests - before.TransportBatchRequests,
            after.CacheHits - before.CacheHits,
            after.CacheMisses - before.CacheMisses,
            after.CacheBytesServed - before.CacheBytesServed,
            after.CacheBatchBypassRequests - before.CacheBatchBypassRequests,
            after.CacheBatchBypassArticles - before.CacheBatchBypassArticles,
            after.CacheWriteCommits - before.CacheWriteCommits,
            bytes);

    private readonly record struct Counters(
        long TransportRequests,
        long TransportBytes,
        long TransportBatchRequests,
        long CacheHits,
        long CacheMisses,
        long CacheBytesServed,
        long CacheBatchBypassRequests,
        long CacheBatchBypassArticles,
        long CacheWriteCommits,
        long Bytes);

    private static async Task AssertProductionReadMatchesAsync(INntpClient client, Fixture fixture)
    {
        await using var stream = fixture.CreateProductionShapedStream(client);
        var buffer = new byte[fixture.Source.Length];
        var read = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: true);
        Assert.Equal(fixture.Source.Length, read);
        Assert.Equal(fixture.Source, buffer);
    }

    private static async Task AssertTransportDeltaAsync(
        FakeNntpClient transport,
        IReadOnlyDictionary<string, byte[]> servedSegments,
        string scenario,
        int expectedRequests,
        long expectedBytes,
        Func<Task> action)
    {
        var requestsBefore = transport.BodyRequestCount;
        var bytesBefore = TransportBytes(transport, servedSegments);
        await action();
        AssertTransportContract(
            scenario,
            expectedRequests,
            transport.BodyRequestCount - requestsBefore,
            expectedBytes,
            TransportBytes(transport, servedSegments) - bytesBefore);
    }

    private static void AssertTransportContract(
        string scenario,
        int expectedRequests,
        int actualRequests,
        long expectedBytes,
        long actualBytes)
    {
        Assert.True(
            actualRequests == expectedRequests && actualBytes == expectedBytes,
            $"{scenario} expected requests={expectedRequests} bytes={expectedBytes} " +
            $"but was requests={actualRequests} bytes={actualBytes}. {TransportContractMessage}");
    }

    private static long TransportBytes(
        FakeNntpClient transport,
        IReadOnlyDictionary<string, byte[]> servedSegments) =>
        transport.RequestedSegmentIds.Sum(id =>
            servedSegments.TryGetValue(id, out var bytes) ? bytes.Length : 0);

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

        public NzbFileStream CreateProductionShapedStream(INntpClient client) =>
            new(
                SegmentIds,
                Source.Length,
                client,
                articleBufferSize: 40,
                segmentByteRanges: Ranges,
                usePipelinedBodyRequests: true,
                fileName: "repeatable-streaming-pipelined-benchmark.bin",
                streamingBodyBatchWidth: 4);
    }
}
