using System.Collections.Concurrent;
using System.Text;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Models;
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
                e.RenderMessage().Contains("Zero-filling", StringComparison.Ordinal));
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

            var zeroFillWarnings = sink.Events.Count(e =>
                e.Level == LogEventLevel.Warning &&
                e.RenderMessage().Contains(fileName, StringComparison.Ordinal) &&
                e.RenderMessage().Contains("Zero-filling", StringComparison.Ordinal));
            Assert.Equal(1, zeroFillWarnings);
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
        Assert.NotEmpty(openedBodies);
        Assert.All(openedBodies, body => Assert.True(body.Disposed));
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

        public ConcurrentDictionary<string, int> BodyRequestCounts { get; } = new(StringComparer.Ordinal);

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

        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId,
            Action<ArticleBodyResult>? onConnectionReadyAgain,
            CancellationToken cancellationToken)
        {
            var response = DecodedBodyAsync(segmentId, cancellationToken);
            onConnectionReadyAgain?.Invoke(ArticleBodyResult.Retrieved);
            return response;
        }

        public override Task<UsenetDecodedBodyBatch> DecodedBodiesAsync(
            IReadOnlyList<SegmentId> segmentIds,
            Action<ArticleBodyResult>? onConnectionReadyAgain,
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
            Action<ArticleBodyResult>? onConnectionReadyAgain,
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
