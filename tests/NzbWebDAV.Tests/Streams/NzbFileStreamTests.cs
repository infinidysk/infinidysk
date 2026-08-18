using System.Collections.Concurrent;
using System.Text;
using MemoryPack;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Models;
using NzbWebDAV.Streams;
using NzbWebDAV.Tests.Fakes;
using NzbWebDAV.Tests.TestUtils;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using UsenetSharp.Models;

namespace NzbWebDAV.Tests.Streams;

[Collection(nameof(GlobalLoggerCollection))]
public class NzbFileStreamTests
{
    private static readonly byte[][] SegmentBytes =
    [
        Encoding.ASCII.GetBytes("abcde"),
        Encoding.ASCII.GetBytes("fghij"),
        Encoding.ASCII.GetBytes("klmno")
    ];

    private static readonly string[] SegmentIds = ["one", "two", "three"];
    private static readonly LongRange[] SegmentRanges =
    [
        new(0, 5),
        new(5, 10),
        new(10, 15)
    ];

    [SkippableTheory]
    [InlineData(0, "abcdefghijklmno")]
    [InlineData(1, "abcdefghijklmno")]
    [InlineData(4, "abcdefghijklmno")]
    public async Task ReadAsync_ConcatenatesSegmentsWithConfiguredPipeline(
        int articleBufferSize, string expected)
    {
        Skip.IfNot(RapidYenc.IsAvailable, "rapidyenc native library not available on this platform");
        var client = CreateClient();
        await using var stream = new NzbFileStream(
            SegmentIds, 15, client, articleBufferSize, SegmentRanges);

        using var destination = new MemoryStream();
        await stream.CopyToAsync(destination);

        Assert.Equal(expected, Encoding.ASCII.GetString(destination.ToArray()));
        if (articleBufferSize > 0) Assert.True(client.BatchRequestCount > 0);
    }

    [Fact]
    public async Task PlaybackStart_DeliversFirstSegmentBeforeStartingBufferedPrefetch()
    {
        var client = new FakeNntpClient(
            SegmentIds.Zip(SegmentBytes).ToDictionary(pair => pair.First, pair => pair.Second),
            useCachedYencStreams: true,
            segmentRanges: SegmentIds.Zip(SegmentRanges).ToDictionary(pair => pair.First, pair => pair.Second));
        await using var stream = new NzbFileStream(
            SegmentIds, 15, client, articleBufferSize: 4, segmentByteRanges: SegmentRanges);

        var firstByte = new byte[1];
        Assert.Equal(1, await stream.ReadAsync(firstByte));

        Assert.Equal("a", Encoding.ASCII.GetString(firstByte));
        Assert.Equal(0, client.BatchRequestCount);

        var restOfFirstSegment = new byte[4];
        Assert.Equal(4, await stream.ReadAsync(restOfFirstSegment));
        Assert.Equal("bcde", Encoding.ASCII.GetString(restOfFirstSegment));
        Assert.Equal(0, client.BatchRequestCount);

        Assert.Equal(1, await stream.ReadAsync(firstByte));
        Assert.True(client.BatchRequestCount > 0);
        Assert.Equal("f", Encoding.ASCII.GetString(firstByte));
    }

    [Fact]
    public async Task SmallClosedRange_StreamsTargetSegmentWithoutDrainingItFirst()
    {
        var client = new FakeNntpClient(
            SegmentIds.Zip(SegmentBytes).ToDictionary(pair => pair.First, pair => pair.Second),
            useCachedYencStreams: true,
            segmentRanges: SegmentIds.Zip(SegmentRanges).ToDictionary(pair => pair.First, pair => pair.Second));
        var previousBudget = NzbWebDAV.WebDav.Requests.RangeContext.GetReadBudget();
        NzbWebDAV.WebDav.Requests.RangeContext.SetReadBudget(2);
        try
        {
            await using var stream = new NzbFileStream(
                SegmentIds, 15, client, articleBufferSize: 4, segmentByteRanges: SegmentRanges);
            stream.Seek(6, SeekOrigin.Begin);
            var buffer = new byte[2];

            Assert.Equal(2, await stream.ReadAsync(buffer));
            Assert.Equal("gh", Encoding.ASCII.GetString(buffer));
            Assert.Equal(0, client.BatchRequestCount);
            Assert.Equal(1, client.BodyRequestCounts["two"]);
        }
        finally
        {
            NzbWebDAV.WebDav.Requests.RangeContext.SetReadBudget(previousBudget);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Create_UsesConfiguredBodyRequestApi(
        bool usePipelinedBodyRequests)
    {
        var client = CreateClient();
        await using var stream = MultiSegmentStream.Create(
            SegmentIds.AsMemory(),
            client,
            articleBufferSize: 4,
            usePipelinedBodyRequests: usePipelinedBodyRequests,
            cancellationToken: CancellationToken.None);

        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (usePipelinedBodyRequests
                    ? client.BatchRequestCount > 0
                    : client.BodyRequestCount == SegmentIds.Length)
                break;
            await Task.Delay(10);
        }

        Assert.Equal(usePipelinedBodyRequests, client.BatchRequestCount > 0);
        if (!usePipelinedBodyRequests)
            Assert.Equal(SegmentIds.Length, client.BodyRequestCount);
    }

    [SkippableTheory]
    [InlineData(0, "abc")]
    [InlineData(4, "efg")]
    [InlineData(5, "fgh")]
    [InlineData(9, "jkl")]
    [InlineData(14, "o")]
    public async Task Seek_ReadsAcrossSegmentBoundaries(long offset, string expected)
    {
        Skip.IfNot(RapidYenc.IsAvailable, "rapidyenc native library not available on this platform");
        var client = CreateClient();
        await using var stream = new NzbFileStream(
            SegmentIds, 15, client, 2, SegmentRanges);
        stream.Seek(offset, SeekOrigin.Begin);
        var buffer = new byte[3];

        var read = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false);

        Assert.Equal(expected, Encoding.ASCII.GetString(buffer, 0, read));
        Assert.Equal(offset + read, stream.Position);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(4, true)]
    public async Task SeekToMissingTsSegment_UsesFileAlignedNullPacket(
        int articleBufferSize,
        bool usePipelinedBodyRequests)
    {
        string[] segmentIds = ["one", "two", "three"];
        var firstPacket = Enumerable.Repeat((byte)'a', 188).ToArray();
        var thirdPacket = Enumerable.Repeat((byte)'c', 188).ToArray();
        var segmentRanges = new[]
        {
            new LongRange(0, 188),
            new LongRange(188, 376),
            new LongRange(376, 564),
        };
        var rangesById = segmentIds
            .Zip(segmentRanges)
            .ToDictionary(pair => pair.First, pair => pair.Second);
        var client = new FakeNntpClient(
            new Dictionary<string, byte[]>
            {
                ["one"] = firstPacket,
                ["three"] = thirdPacket,
            },
            useCachedYencStreams: true,
            segmentRanges: rangesById);
        await using var stream = new NzbFileStream(
            segmentIds,
            564,
            client,
            articleBufferSize,
            segmentRanges,
            usePipelinedBodyRequests,
            fileName: "movie.ts",
            useContainerAwareFill: true);
        stream.Seek(188, SeekOrigin.Begin);
        var buffer = new byte[188];

        var read = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: true);

        Assert.Equal(188, read);
        Assert.Equal(new byte[] { 0x47, 0x1F, 0xFF, 0x10 }, buffer[..4]);
        Assert.All(buffer[4..], value => Assert.Equal(0xFF, value));
        Assert.Equal(376, stream.Position);
    }

    [Fact]
    public void Seek_RejectsPositionsOutsideFile()
    {
        using var stream = new NzbFileStream(
            SegmentIds, 15, CreateClient(), 1, SegmentRanges);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => stream.Seek(-1, SeekOrigin.Begin));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => stream.Seek(16, SeekOrigin.Begin));
    }

    [SkippableFact]
    public async Task SmallForwardSeek_DrainsExistingPipeline()
    {
        Skip.IfNot(RapidYenc.IsAvailable, "rapidyenc native library not available on this platform");
        var client = CreateClient();
        await using var stream = new NzbFileStream(
            SegmentIds, 15, client, 2, SegmentRanges);
        var initial = new byte[2];
        Assert.Equal(2, await stream.ReadAsync(initial));

        stream.Seek(7, SeekOrigin.Begin);
        var buffer = new byte[3];
        var read = await stream.ReadAsync(buffer);

        Assert.Equal("hij", Encoding.ASCII.GetString(buffer, 0, read));
    }

    [Fact]
    public async Task MissingArticle_ZeroFillsAndLogsFileName()
    {
        const string fileName = "/content/show/episode.mkv";
        const string segmentId = "missing-article";
        var sink = new CollectingSink();
        var previous = Log.Logger;
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Warning()
            .WriteTo.Sink(sink)
            .CreateLogger();

        try
        {
            // Empty segment map → UsenetArticleNotFound before any yEnc decode.
            // Use unbuffered mode so the assertion does not depend on pipelined batch timing.
            var client = new FakeNntpClient(new Dictionary<string, byte[]>());
            await using var stream = MultiSegmentStream.Create(
                new[] { segmentId }.AsMemory(),
                client,
                articleBufferSize: 0,
                estimatedSegmentSize: 5,
                failFastOnFirstSegment: false,
                usePipelinedBodyRequests: false,
                cancellationToken: CancellationToken.None,
                fileName: fileName,
                exactSegmentSizes: new long[] { 5 });

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var buffer = new byte[5];
            var read = await stream.ReadAsync(buffer, cts.Token);

            Assert.Equal(5, read);
            Assert.Equal(new byte[5], buffer);
            Assert.Contains(sink.Events, e =>
                e.Level == LogEventLevel.Warning &&
                e.RenderMessage().Contains(fileName, StringComparison.Ordinal) &&
                e.RenderMessage().Contains(segmentId, StringComparison.Ordinal) &&
                e.RenderMessage().Contains("Filling the 5-byte gap", StringComparison.Ordinal));
        }
        finally
        {
            Log.Logger = previous;
        }
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(4, true)]
    public async Task MissingArticles_ZeroFillWarningsAreCoalescedByFile(
        int articleBufferSize,
        bool usePipelinedBodyRequests)
    {
        var fileName = $"/content/show/coalesced-episode-{articleBufferSize}.mkv";
        var sink = new CollectingSink();
        var previous = Log.Logger;
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Warning()
            .WriteTo.Sink(sink)
            .CreateLogger();

        try
        {
            var client = new FakeNntpClient(new Dictionary<string, byte[]>());
            await using var stream = MultiSegmentStream.Create(
                new[] { "missing-one", "missing-two" }.AsMemory(),
                client,
                articleBufferSize: articleBufferSize,
                estimatedSegmentSize: 5,
                failFastOnFirstSegment: false,
                usePipelinedBodyRequests: usePipelinedBodyRequests,
                cancellationToken: CancellationToken.None,
                fileName: fileName,
                exactSegmentSizes: new long[] { 5, 5 });

            var buffer = new byte[5];
            Assert.Equal(5, await stream.ReadAsync(buffer));
            Assert.Equal(5, await stream.ReadAsync(buffer));

            var gapFillWarnings = sink.Events.Count(e =>
                e.Level == LogEventLevel.Warning &&
                e.RenderMessage().Contains(fileName, StringComparison.Ordinal) &&
                e.RenderMessage().Contains("Filling the 5-byte gap", StringComparison.Ordinal));
            Assert.Equal(1, gapFillWarnings);
        }
        finally
        {
            Log.Logger = previous;
        }
    }

    [Fact]
    public async Task MissingArticles_ThirdConsecutiveZeroFillFailsStream()
    {
        var client = new FakeNntpClient(new Dictionary<string, byte[]>());
        await using var stream = MultiSegmentStream.Create(
            new[] { "missing-one", "missing-two", "missing-three", "missing-four" }.AsMemory(),
            client,
            articleBufferSize: 0,
            estimatedSegmentSize: 5,
            failFastOnFirstSegment: false,
            usePipelinedBodyRequests: false,
            cancellationToken: CancellationToken.None,
            fileName: "/content/show/dead-episode.mkv",
            exactSegmentSizes: new long[] { 5, 5, 5, 5 });

        var buffer = new byte[5];
        Assert.Equal(5, await stream.ReadAsync(buffer));
        Assert.Equal(5, await stream.ReadAsync(buffer));
        await Assert.ThrowsAsync<NzbWebDAV.Exceptions.UsenetArticleNotFoundException>(
            async () => await stream.ReadAtLeastAsync(
                buffer, buffer.Length, throwOnEndOfStream: false));

        Assert.Equal(3, client.BodyRequestCount);
    }

    // These fast-seek tests use CachedYencStream (pre-parsed headers over decoded
    // bytes), so they run even where the rapidyenc native library is unavailable.
    [Theory]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    public async Task ColdStartSeek_UsesPersistedRangesInsteadOfHeaderProbesAfterFastPathFallback(
        bool persistRanges,
        int expectedHeaderProbes)
    {
        var stored = new DavNzbFile
        {
            Id = Guid.NewGuid(),
            SegmentIds = SegmentIds,
            SegmentByteRanges = persistRanges ? SegmentRanges : null,
        };
        var blob = MemoryPackSerializer.Serialize(stored);
        var restored = MemoryPackSerializer.Deserialize<DavNzbFile>(blob)!;
        var client = CreateFlakyClient(
            () => new ThrowingReadStream(
                () => new TimeoutException("Timeout reading from NNTP stream.")));
        await using var stream = new NzbFileStream(
            restored.SegmentIds,
            15,
            client,
            2,
            restored.SegmentByteRanges,
            usePipelinedBodyRequests: false);
        stream.Seek(7, SeekOrigin.Begin);
        var buffer = new byte[3];

        var read = await stream.ReadAtLeastAsync(
            buffer, buffer.Length, throwOnEndOfStream: false);

        Assert.Equal("hij", Encoding.ASCII.GetString(buffer, 0, read));
        Assert.True(client.BodyRequestCounts["two"] >= 2);
        Assert.Equal(expectedHeaderProbes, client.HeaderProbeCount);
    }

    [Fact]
    public async Task FastSeek_BodyReadTimeout_FallsBackToSlowSeekPath()
    {
        var client = CreateFlakyClient(
            () => new ThrowingReadStream(
                () => new TimeoutException("Timeout reading from NNTP stream.")));
        await using var stream = new NzbFileStream(
            SegmentIds, 15, client, 2, SegmentRanges, usePipelinedBodyRequests: false);
        stream.Seek(7, SeekOrigin.Begin);
        var buffer = new byte[3];

        var read = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false);

        Assert.Equal("hij", Encoding.ASCII.GetString(buffer, 0, read));
        // Failed fast-seek attempt + successful slow-path fetch.
        Assert.True(client.BodyRequestCounts["two"] >= 2);
    }

    [Fact]
    public async Task FastSeek_BodyReadCancellation_PropagatesCancellation()
    {
        using var cts = new CancellationTokenSource();
        var client = CreateFlakyClient(() => new ThrowingReadStream(() =>
        {
            cts.Cancel();
            return new OperationCanceledException(cts.Token);
        }));
        await using var stream = new NzbFileStream(
            SegmentIds, 15, client, 2, SegmentRanges, usePipelinedBodyRequests: false);
        stream.Seek(7, SeekOrigin.Begin);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await stream.ReadAtLeastAsync(
                new byte[3], 3, throwOnEndOfStream: false, cts.Token));
        // Cancellation must not trigger the slow-path fallback.
        Assert.Equal(1, client.BodyRequestCounts["two"]);
    }

    [Fact]
    public async Task FastSeek_BodyDisposeFailure_FallsBackToSlowSeekPath()
    {
        var client = CreateFlakyClient(
            () => new ThrowingDisposeMemoryStream(SegmentBytes[1]));
        await using var stream = new NzbFileStream(
            SegmentIds, 15, client, 2, SegmentRanges, usePipelinedBodyRequests: false);
        stream.Seek(7, SeekOrigin.Begin);
        var buffer = new byte[3];

        var read = await stream.ReadAtLeastAsync(
            buffer, buffer.Length, throwOnEndOfStream: false);

        Assert.Equal("hij", Encoding.ASCII.GetString(buffer, 0, read));
        Assert.True(client.BodyRequestCounts["two"] >= 2);
    }

    [Fact]
    public async Task ReadAsync_EndsBeforeDeclaredFileSize_ReturnsZeroWithoutThrowing()
    {
        using var client = new FakeNntpClient(new Dictionary<string, byte[]>
        {
            ["seg"] = Enumerable.Repeat((byte)1, 10).ToArray(),
        }, useCachedYencStreams: true);
        await using var stream = new NzbFileStream(
            ["seg"],
            fileSize: 100,
            client,
            articleBufferSize: 0,
            segmentByteRanges: null,
            usePipelinedBodyRequests: false,
            fileName: "short.bin");

        var buffer = new byte[100];
        var firstRead = await stream.ReadAsync(buffer);
        Assert.Equal(10, firstRead);

        var secondRead = await stream.ReadAsync(buffer.AsMemory(10));
        Assert.Equal(0, secondRead);
    }

    [Fact]
    public async Task Seek_WhenIndexedSegmentEndsBeforeOffset_ThrowsAndDisposesBodies()
    {
        string[] segmentIds = ["short"];
        var segments = new Dictionary<string, byte[]> { ["short"] = [1, 2, 3, 4, 5] };
        var ranges = new Dictionary<string, LongRange> { ["short"] = new(0, 5) };
        var openedBodies = new List<TrackingMemoryStream>();
        var client = new FakeNntpClient(
            segments,
            useCachedYencStreams: true,
            ranges,
            (_, _) =>
            {
                var body = new TrackingMemoryStream([1, 2]);
                openedBodies.Add(body);
                return body;
            });
        await using var stream = new NzbFileStream(
            segmentIds,
            fileSize: 5,
            client,
            articleBufferSize: 0,
            segmentByteRanges: null,
            usePipelinedBodyRequests: false,
            fileName: "short.bin");
        stream.Seek(4, SeekOrigin.Begin);

        var exception = await Assert.ThrowsAsync<SeekPositionNotFoundException>(
            async () => await stream.ReadAtLeastAsync(
                new byte[1], 1, throwOnEndOfStream: false));

        Assert.Contains("Byte position 4", exception.Message, StringComparison.Ordinal);
        Assert.Contains("segment 1", exception.Message, StringComparison.Ordinal);
        Assert.IsType<EndOfStreamException>(exception.InnerException);
        Assert.NotEmpty(openedBodies);
        Assert.All(openedBodies, body => Assert.True(body.Disposed));
    }

    [Fact]
    public async Task Seek_WhenHeaderProbeMissCausesSearchFailure_PreservesMissingArticle()
    {
        string[] segmentIds = ["zero", "one", "missing"];
        var client = new FakeNntpClient(
            new Dictionary<string, byte[]>
            {
                ["zero"] = new byte[20],
                ["one"] = new byte[20],
            },
            useCachedYencStreams: true,
            segmentRanges: new Dictionary<string, LongRange>
            {
                ["zero"] = new(0, 20),
                ["one"] = new(0, 20),
            });
        await using var stream = new NzbFileStream(
            segmentIds,
            fileSize: 100,
            client,
            articleBufferSize: 0,
            segmentByteRanges: null,
            usePipelinedBodyRequests: false,
            fileName: "missing-probe.bin");
        stream.Seek(50, SeekOrigin.Begin);

        var exception = await Assert.ThrowsAsync<SeekPositionNotFoundException>(
            async () => await stream.ReadAtLeastAsync(
                new byte[1], 1, throwOnEndOfStream: false));

        var missing = Assert.IsType<UsenetArticleNotFoundException>(exception.InnerException);
        Assert.Equal("missing", missing.SegmentId);
    }

    [Fact]
    public async Task Seek_WhenSegmentReadFails_ReleasesArticleBudget()
    {
        const int segmentSize = 1000;
        const int segmentCount = 6;
        var budget = new InFlightArticleBudget(segmentSize * 40);
        var segmentIds = Enumerable.Range(0, segmentCount).Select(i => $"seg-{i}").ToArray();
        var segments = segmentIds.ToDictionary(
            id => id,
            _ => Enumerable.Repeat((byte)1, segmentSize).ToArray());
        var client = new FakeNntpClient(
            segments,
            useCachedYencStreams: true,
            decodedStreamFactory: (key, bytes) => key == "seg-2"
                ? new ThrowingReadStream(() => new IOException("provider reset"))
                : new MemoryStream(bytes, writable: false));

        await using var stream = new NzbFileStream(
            segmentIds,
            fileSize: segmentSize * segmentCount,
            client,
            articleBufferSize: 4,
            segmentByteRanges: null,
            usePipelinedBodyRequests: false,
            fileName: "seek-fail.bin",
            inFlightArticleBudget: budget);
        stream.Seek(segmentSize * 2 + 10, SeekOrigin.Begin);

        await Assert.ThrowsAnyAsync<Exception>(
            async () => await stream.ReadAtLeastAsync(
                new byte[16], 16, throwOnEndOfStream: false));

        Assert.Equal(0, budget.LeasedBytes);
    }

    [Fact]
    public async Task Seek_RepeatedSegmentReadFailures_DoNotAccumulateArticleBudget()
    {
        const int segmentSize = 1000;
        const int segmentCount = 6;
        var budget = new InFlightArticleBudget(segmentSize * 40);
        var segmentIds = Enumerable.Range(0, segmentCount).Select(i => $"seg-{i}").ToArray();
        var segments = segmentIds.ToDictionary(
            id => id,
            _ => Enumerable.Repeat((byte)1, segmentSize).ToArray());

        for (var attempt = 0; attempt < 12; attempt++)
        {
            var client = new FakeNntpClient(
                segments,
                useCachedYencStreams: true,
                decodedStreamFactory: (key, bytes) => key == "seg-2"
                    ? new ThrowingReadStream(() => new IOException("provider reset"))
                    : new MemoryStream(bytes, writable: false));

            await using var stream = new NzbFileStream(
                segmentIds,
                fileSize: segmentSize * segmentCount,
                client,
                articleBufferSize: 4,
                segmentByteRanges: null,
                usePipelinedBodyRequests: false,
                fileName: "seek-fail-repeat.bin",
                inFlightArticleBudget: budget);
            stream.Seek(segmentSize * 2 + 10, SeekOrigin.Begin);

            await Assert.ThrowsAnyAsync<Exception>(
                async () => await stream.ReadAtLeastAsync(
                    new byte[16], 16, throwOnEndOfStream: false));

            Assert.Equal(0, budget.LeasedBytes);
        }
    }

    [Fact]
    public async Task RapidScrubbing_ReleasesArticleBudgetAcrossGenerations()
    {
        // Models the pringles dump: many overlapping offset reads on one file pin the
        // global article budget after NNTP goes idle. Each Seek replaces the inner
        // stream; the next ReadAsync must join the prior teardown before leasing again
        // so generations do not overlap, and DisposeAsync must drain everything.
        const int segmentSize = 1000;
        const int segmentCount = 12;
        var budget = new InFlightArticleBudget(segmentSize * 40);
        var segmentIds = Enumerable.Range(0, segmentCount).Select(i => $"seg-{i}").ToArray();
        var segments = segmentIds.ToDictionary(id => id, _ => Enumerable.Repeat((byte)1, segmentSize).ToArray());
        var rangesById = segmentIds
            .Zip(Enumerable.Range(0, segmentCount).Select(i => new LongRange(i * segmentSize, (i + 1) * segmentSize)))
            .ToDictionary(pair => pair.First, pair => pair.Second);
        var ranges = Enumerable.Range(0, segmentCount)
            .Select(i => new LongRange(i * segmentSize, (i + 1) * segmentSize))
            .ToArray();
        var client = new FakeNntpClient(segments, useCachedYencStreams: true, segmentRanges: rangesById);

        await using var stream = new NzbFileStream(
            segmentIds,
            fileSize: segmentSize * segmentCount,
            client,
            articleBufferSize: 4,
            segmentByteRanges: ranges,
            usePipelinedBodyRequests: false,
            fileName: "scrub.bin",
            inFlightArticleBudget: budget);

        var buffer = new byte[segmentSize / 4];
        for (var scrub = 0; scrub < 16; scrub++)
        {
            // Jump to a different segment each iteration, simulating scroll back/forward.
            var pos = ((scrub * 5 + 1) * segmentSize) % (segmentSize * segmentCount);
            stream.Seek(pos, SeekOrigin.Begin);
            _ = await stream.ReadAsync(buffer);
            Assert.True(budget.LeasedBytes <= budget.CapBytes,
                $"Leased {budget.LeasedBytes} exceeded cap {budget.CapBytes} during scrub {scrub}");
        }

        await stream.DisposeAsync();
        Assert.Equal(0, budget.LeasedBytes);
    }

    [Fact]
    public async Task SeekThenDisposeAsync_AwaitsPriorTeardownBeforeReleasingBudget()
    {
        // A Seek with no following Read still starts the old inner stream's teardown;
        // DisposeAsync must join it so leases are released before it returns.
        const int segmentSize = 1000;
        const int segmentCount = 6;
        var budget = new InFlightArticleBudget(segmentSize * 40);
        var segmentIds = Enumerable.Range(0, segmentCount).Select(i => $"seg-{i}").ToArray();
        var segments = segmentIds.ToDictionary(id => id, _ => Enumerable.Repeat((byte)1, segmentSize).ToArray());
        var rangesById = segmentIds
            .Zip(Enumerable.Range(0, segmentCount).Select(i => new LongRange(i * segmentSize, (i + 1) * segmentSize)))
            .ToDictionary(pair => pair.First, pair => pair.Second);
        var ranges = Enumerable.Range(0, segmentCount)
            .Select(i => new LongRange(i * segmentSize, (i + 1) * segmentSize))
            .ToArray();
        var client = new FakeNntpClient(segments, useCachedYencStreams: true, segmentRanges: rangesById);

        var stream = new NzbFileStream(
            segmentIds,
            fileSize: segmentSize * segmentCount,
            client,
            articleBufferSize: 4,
            segmentByteRanges: ranges,
            usePipelinedBodyRequests: false,
            fileName: "seek-dispose.bin",
            inFlightArticleBudget: budget);

        var buffer = new byte[segmentSize];
        _ = await stream.ReadAsync(buffer);
        // The first segment uses the direct first-byte path; reading into the next
        // segment creates the buffered prefetch stream that owns byte-budget leases.
        _ = await stream.ReadAsync(buffer);
        Assert.True(budget.LeasedBytes > 0);

        // Seek away from the data we just read; this starts the old stream's teardown.
        stream.Seek(segmentSize * 4, SeekOrigin.Begin);

        await stream.DisposeAsync();
        Assert.Equal(0, budget.LeasedBytes);
    }

    private static FakeNntpClient CreateClient()
    {
        return new FakeNntpClient(
            SegmentIds.Zip(SegmentBytes).ToDictionary(pair => pair.First, pair => pair.Second));
    }

    private static FlakySeekNntpClient CreateFlakyClient(Func<Stream> firstFlakyBody)
    {
        return new FlakySeekNntpClient(
            SegmentIds.Zip(SegmentBytes).ToDictionary(pair => pair.First, pair => pair.Second),
            SegmentIds.Zip(SegmentRanges).ToDictionary(pair => pair.First, pair => pair.Second),
            fileSize: 15,
            flakySegmentId: "two",
            firstFlakyBody);
    }

    /// <summary>
    /// Serilog's <see cref="Log.Logger"/> is process-global, so while a test has this
    /// sink installed every test class running in parallel emits into it too.
    /// The fix is to lock writes and return a snapshot for reads.
    /// </summary>
    private sealed class CollectingSink : ILogEventSink
    {
        private readonly List<LogEvent> _events = [];

        public IReadOnlyList<LogEvent> Events
        {
            get
            {
                lock (_events) return _events.ToArray();
            }
        }

        public void Emit(LogEvent logEvent)
        {
            lock (_events) _events.Add(logEvent);
        }
    }

    /// <summary>
    /// Serves segments as <see cref="CachedYencStream"/>s (no yEnc decode). The
    /// first body fetched for <paramref name="flakySegmentId"/> reads from
    /// <paramref name="firstFlakyBody"/> instead of the real payload, letting
    /// tests fail the fast-seek drain mid-body.
    /// </summary>
    private sealed class FlakySeekNntpClient(
        IReadOnlyDictionary<string, byte[]> segments,
        IReadOnlyDictionary<string, LongRange> ranges,
        long fileSize,
        string flakySegmentId,
        Func<Stream> firstFlakyBody) : NntpClient
    {
        private int _flakyBodiesServed;
        private int _headerProbeCount;

        public ConcurrentDictionary<string, int> BodyRequestCounts { get; } = new(StringComparer.Ordinal);
        public int HeaderProbeCount => Volatile.Read(ref _headerProbeCount);

        public override Task ConnectAsync(
            string host, int port, bool useSsl, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public override Task<UsenetResponse> AuthenticateAsync(
            string user, string pass, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetStatResponse> StatAsync(
            SegmentId segmentId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetHeadResponse> HeadAsync(
            SegmentId segmentId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = segmentId.ToString();
            BodyRequestCounts.AddOrUpdate(key, 1, static (_, count) => count + 1);

            var range = ranges[key];
            var headers = new UsenetYencHeader
            {
                FileName = "fake.bin",
                FileSize = fileSize,
                LineLength = 128,
                PartNumber = 1,
                TotalParts = 1,
                PartOffset = range.StartInclusive,
                PartSize = range.Count,
            };
            var inner = key == flakySegmentId && Interlocked.Increment(ref _flakyBodiesServed) == 1
                ? firstFlakyBody()
                : new MemoryStream(segments[key], writable: false);
            return Task.FromResult(new UsenetDecodedBodyResponse
            {
                SegmentId = key,
                ResponseCode = (int)UsenetResponseType.ArticleRetrievedBodyFollows,
                ResponseMessage = "222 cached body",
                Stream = new CachedYencStream(headers, inner),
            });
        }

        public override Task<UsenetYencHeader> GetYencHeadersAsync(
            string segmentId,
            CancellationToken ct)
        {
            Interlocked.Increment(ref _headerProbeCount);
            return base.GetYencHeadersAsync(segmentId, ct);
        }

        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId,
            ArticleBodyCompletionHandler? onConnectionReadyAgain,
            CancellationToken cancellationToken)
        {
            var response = DecodedBodyAsync(segmentId, cancellationToken);
            onConnectionReadyAgain?.Invoke(ArticleBodyResult.Retrieved);
            return response;
        }

        public override Task<UsenetDecodedBodyBatch> DecodedBodiesAsync(
            IReadOnlyList<SegmentId> segmentIds,
            ArticleBodyCompletionHandler? onConnectionReadyAgain,
            CancellationToken cancellationToken)
        {
            var responses = segmentIds
                .Select(segmentId => DecodedBodyAsync(segmentId, cancellationToken))
                .ToArray();
            onConnectionReadyAgain?.Invoke(ArticleBodyResult.Retrieved);
            return Task.FromResult(new UsenetDecodedBodyBatch { Responses = responses });
        }

        public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
            SegmentId segmentId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
            SegmentId segmentId,
            ArticleBodyCompletionHandler? onConnectionReadyAgain,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetDateResponse> DateAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetExclusiveConnection> AcquireExclusiveConnectionAsync(
            string segmentId, CancellationToken cancellationToken) =>
            Task.FromResult(new UsenetExclusiveConnection(null));

        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId,
            UsenetExclusiveConnection exclusiveConnection,
            CancellationToken cancellationToken) =>
            DecodedBodyAsync(segmentId, exclusiveConnection.OnConnectionReadyAgain, cancellationToken);

        public override void Dispose()
        {
        }
    }

    private sealed class ThrowingReadStream(Func<Exception> exceptionFactory) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw exceptionFactory();

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(exceptionFactory());

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class ThrowingDisposeMemoryStream(byte[] bytes)
        : MemoryStream(bytes, writable: false)
    {
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
                throw new IOException("Simulated source disposal failure.");
        }
    }

    private sealed class TrackingMemoryStream(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        public bool Disposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }
}
