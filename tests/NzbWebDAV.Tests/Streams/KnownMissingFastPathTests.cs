using System.Text;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Models;
using NzbWebDAV.Streams;
using NzbWebDAV.Tests.Fakes;
using UsenetSharp.Models;
using UsenetSharp.Streams;

namespace NzbWebDAV.Tests.Streams;

[Collection(nameof(PlaybackHoleTrackerCollection))]
public class KnownMissingFastPathTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(4, true)]
    public async Task KnownMissingSegment_SkipsProviderAndPreservesOrder(
        int articleBufferSize,
        bool usePipelinedBodyRequests)
    {
        const string first = "first@test";
        const string missing = "missing@test";
        const string last = "last@test";
        var client = new FakeNntpClient(
            new Dictionary<string, byte[]>
            {
                [first] = "one"u8.ToArray(),
                [last] = "two"u8.ToArray(),
            },
            useCachedYencStreams: true,
            segmentRanges: new Dictionary<string, LongRange>
            {
                [first] = new(0, 3),
                [missing] = new(3, 6),
                [last] = new(6, 9),
            });
        await using var stream = MultiSegmentStream.Create(
            new[] { first, missing, last }.AsMemory(),
            client,
            articleBufferSize,
            estimatedSegmentSize: 3,
            failFastOnFirstSegment: false,
            usePipelinedBodyRequests,
            CancellationToken.None,
            fileName: "movie.mkv",
            exactSegmentSizes: new long[] { 3, 3, 3 },
            knownMissingSegmentIndices: new HashSet<int> { 1 });
        using var output = new MemoryStream();

        await stream.CopyToAsync(output);

        Assert.Equal("one\0\0\0two", Encoding.ASCII.GetString(output.ToArray()));
        Assert.Equal(0, client.BodyRequestCounts.GetValueOrDefault(missing));
        Assert.Equal(1, client.BodyRequestCounts[first]);
        Assert.Equal(1, client.BodyRequestCounts[last]);
        Assert.Equal(usePipelinedBodyRequests, client.BatchRequestCount > 0);
    }

    [Fact]
    public async Task KnownMissingSegment_UsesLocalCopyBeforeGapFill()
    {
        const string segmentId = "missing@test";
        var client = new FakeNntpClient(
            new Dictionary<string, byte[]>(),
            useCachedYencStreams: true,
            segmentRanges: new Dictionary<string, LongRange> { [segmentId] = new(0, 5) },
            localSegments: new Dictionary<string, byte[]> { [segmentId] = "local"u8.ToArray() });
        await using var stream = MultiSegmentStream.Create(
            new[] { segmentId }.AsMemory(),
            client,
            articleBufferSize: 0,
            estimatedSegmentSize: 5,
            failFastOnFirstSegment: false,
            usePipelinedBodyRequests: false,
            CancellationToken.None,
            fileName: "movie.mkv",
            exactSegmentSizes: new long[] { 5 },
            knownMissingSegmentIndices: new HashSet<int> { 0 });
        using var output = new MemoryStream();

        await stream.CopyToAsync(output);

        Assert.Equal("local", Encoding.ASCII.GetString(output.ToArray()));
        Assert.Equal(0, client.BodyRequestCount);
    }

    [Fact]
    public async Task KnownMissingSegment_UsesLocalFallbackWithoutRangeMetadata()
    {
        const string primary = "missing@test";
        const string fallback = "fallback@test";
        var client = new FakeNntpClient(
            new Dictionary<string, byte[]>(),
            useCachedYencStreams: true,
            segmentRanges: new Dictionary<string, LongRange> { [primary] = new(0, 5) },
            localSegments: new Dictionary<string, byte[]> { [fallback] = "local"u8.ToArray() });
        await using var stream = MultiSegmentStream.Create(
            new[] { primary }.AsMemory(),
            client,
            articleBufferSize: 0,
            estimatedSegmentSize: 5,
            failFastOnFirstSegment: false,
            usePipelinedBodyRequests: false,
            CancellationToken.None,
            fileName: "movie.mkv",
            segmentFallbacks: [new[] { fallback }],
            exactSegmentSizes: new long[] { 5 },
            knownMissingSegmentIndices: new HashSet<int> { 0 });
        using var output = new MemoryStream();

        await stream.CopyToAsync(output);

        Assert.Equal("local", Encoding.ASCII.GetString(output.ToArray()));
        Assert.Equal(0, client.BodyRequestCount);
    }

    [Fact]
    public async Task ThirdConsecutiveKnownMissingSegment_FailsWithoutProviderRequests()
    {
        var ids = new[] { "missing-one", "missing-two", "missing-three" };
        var client = new FakeNntpClient(new Dictionary<string, byte[]>());
        await using var stream = MultiSegmentStream.Create(
            ids.AsMemory(),
            client,
            articleBufferSize: 0,
            estimatedSegmentSize: 5,
            failFastOnFirstSegment: false,
            usePipelinedBodyRequests: false,
            CancellationToken.None,
            fileName: "movie.mkv",
            exactSegmentSizes: new long[] { 5, 5, 5 },
            knownMissingSegmentIndices: new HashSet<int> { 0, 1, 2 });

        await Assert.ThrowsAsync<UsenetArticleNotFoundException>(
            async () => await stream.CopyToAsync(Stream.Null));

        Assert.Equal(0, client.BodyRequestCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public async Task KnownMissingFirstSegment_PreservesFailFastBehavior(int articleBufferSize)
    {
        const string segmentId = "missing@test";
        var client = new FakeNntpClient(new Dictionary<string, byte[]>());
        await using var stream = MultiSegmentStream.Create(
            new[] { segmentId }.AsMemory(),
            client,
            articleBufferSize,
            estimatedSegmentSize: 5,
            failFastOnFirstSegment: true,
            usePipelinedBodyRequests: articleBufferSize > 0,
            CancellationToken.None,
            fileName: "movie.mkv",
            exactSegmentSizes: new long[] { 5 },
            knownMissingSegmentIndices: new HashSet<int> { 0 });

        await Assert.ThrowsAsync<UsenetArticleNotFoundException>(
            async () => await stream.CopyToAsync(Stream.Null));

        Assert.Equal(0, client.BodyRequestCount);
    }

    [Fact]
    public async Task SeekIntoKnownMissingSegment_SkipsDirectProviderFetch()
    {
        string[] ids = ["first@test", "missing@test", "last@test"];
        var ranges = new[] { new LongRange(0, 3), new LongRange(3, 6), new LongRange(6, 9) };
        var client = new FakeNntpClient(
            new Dictionary<string, byte[]>
            {
                [ids[0]] = "one"u8.ToArray(),
                [ids[2]] = "two"u8.ToArray(),
            },
            useCachedYencStreams: true,
            segmentRanges: ids.Zip(ranges).ToDictionary(pair => pair.First, pair => pair.Second));
        await using var stream = new NzbFileStream(
            ids,
            fileSize: 9,
            client,
            articleBufferSize: 4,
            segmentByteRanges: ranges,
            usePipelinedBodyRequests: false,
            fileName: "movie.mkv",
            knownMissingSegmentIndices: new HashSet<int> { 1 });
        stream.Seek(3, SeekOrigin.Begin);
        var destination = new byte[3];

        var read = await stream.ReadAsync(destination);

        Assert.Equal(3, read);
        Assert.Equal(new byte[3], destination);
        Assert.Equal(0, client.BodyRequestCounts.GetValueOrDefault(ids[1]));
    }

    [Fact]
    public async Task ThirdConsecutiveMiss_SecondStreamFailsFastWithZeroBodyRequests()
    {
        PlaybackHoleTracker.ResetForTests();
        var path = $"/view/fail-fast-{Guid.NewGuid():N}.mkv";
        var ids = new[] { "miss-a@test", "miss-b@test", "miss-c@test", "miss-d@test" };
        var sizes = new long[] { 5, 5, 5, 5 };
        try
        {
            var first = new FakeNntpClient(new Dictionary<string, byte[]>());
            await using (var stream = CreateGapFillStream(ids, first, path, sizes, articleBufferSize: 4))
            {
                await Assert.ThrowsAsync<UsenetArticleNotFoundException>(
                    async () => await stream.CopyToAsync(Stream.Null));
            }

            Assert.True(first.BodyRequestCount > 0);
            Assert.True(PlaybackHoleTracker.ShouldFailFast(path, out var stored));
            Assert.IsType<UsenetArticleNotFoundException>(stored);

            var second = new FakeNntpClient(new Dictionary<string, byte[]>());
            await using var stream2 = CreateGapFillStream(ids, second, path, sizes, articleBufferSize: 4);
            await Assert.ThrowsAsync<UsenetArticleNotFoundException>(
                async () => await stream2.CopyToAsync(Stream.Null));

            Assert.Equal(0, second.BodyRequestCount);
        }
        finally
        {
            PlaybackHoleTracker.ResetForTests();
        }
    }

    [Fact]
    public async Task PipelinedStream_AlreadyFailFast_IssuesNoBodyRequests()
    {
        PlaybackHoleTracker.ResetForTests();
        var path = $"/view/fail-fast-{Guid.NewGuid():N}.mkv";
        var ids = new[] { "miss-a@test", "miss-b@test", "miss-c@test", "miss-d@test" };
        try
        {
            foreach (var id in ids.Take(GapFillLimits.MaxConsecutiveZeroFills))
                PlaybackHoleTracker.RecordHole(path, id, new UsenetArticleNotFoundException(id));

            var client = new FakeNntpClient(ids.ToDictionary(id => id, _ => new byte[5]));
            await using var stream = MultiSegmentStream.Create(
                ids.AsMemory(),
                client,
                articleBufferSize: 4,
                estimatedSegmentSize: 5,
                failFastOnFirstSegment: false,
                usePipelinedBodyRequests: true,
                CancellationToken.None,
                fileName: path,
                exactSegmentSizes: new long[] { 5, 5, 5, 5 });

            await Assert.ThrowsAsync<UsenetArticleNotFoundException>(
                async () => await stream.CopyToAsync(Stream.Null));

            Assert.Equal(0, client.BatchRequestCount);
            Assert.Equal(0, client.BodyRequestCount);
        }
        finally
        {
            PlaybackHoleTracker.ResetForTests();
        }
    }

    [Fact]
    public async Task PipelinedFailFastAfterBatchIssue_OwnsEveryIssuedResponse()
    {
        PlaybackHoleTracker.ResetForTests();
        var path = $"/view/fail-fast-{Guid.NewGuid():N}.mkv";
        var ids = new[] { "seg-a@test", "seg-b@test", "seg-c@test", "seg-d@test" };
        try
        {
            var client = new TrackerTrippingBatchClient(path);
            var stream = MultiSegmentStream.Create(
                ids.AsMemory(),
                client,
                articleBufferSize: 4,
                estimatedSegmentSize: 5,
                failFastOnFirstSegment: false,
                usePipelinedBodyRequests: true,
                CancellationToken.None,
                fileName: path,
                exactSegmentSizes: new long[] { 5, 5, 5, 5 });

            await Assert.ThrowsAsync<UsenetArticleNotFoundException>(
                async () => await stream.CopyToAsync(Stream.Null));
            await stream.DisposeAsync();

            // Every response the batch put on the wire must be consumed or disposed,
            // or UsenetSharp's pump stays blocked behind the unread body and the batch
            // completion callback (which returns the pooled connection) never fires.
            Assert.Equal(ids.Length, client.IssuedStreamCount);
            Assert.Equal(ids.Length, client.DisposedStreamCount);
            Assert.Equal(1, client.CompletionCallbackCount);

            var followUp = await client.DecodedBodyAsync("follow-up@test", null, CancellationToken.None);
            Assert.NotNull(followUp.Stream);
            await followUp.Stream.DisposeAsync();
        }
        finally
        {
            PlaybackHoleTracker.ResetForTests();
        }
    }

    private static Stream CreateGapFillStream(
        string[] ids,
        INntpClient client,
        string path,
        long[] sizes,
        int articleBufferSize) =>
        MultiSegmentStream.Create(
            ids.AsMemory(),
            client,
            articleBufferSize,
            estimatedSegmentSize: 5,
            failFastOnFirstSegment: false,
            usePipelinedBodyRequests: false,
            CancellationToken.None,
            fileName: path,
            exactSegmentSizes: sizes);

    /// <summary>
    /// Models the batch backpressure contract: the completion callback fires only once
    /// every handed-out body stream is disposed, and the single pooled connection cannot
    /// serve a follow-up request until then. The tracker is tripped after the batch is
    /// on the wire but before the segment tasks accept their responses.
    /// </summary>
    private sealed class TrackerTrippingBatchClient(string path) : NntpClient
    {
        private readonly List<TrackingYencStream> _streams = [];
        private int _disposedStreams;
        private int _batchOutstanding;

        public int BatchRequestCount { get; private set; }
        public int CompletionCallbackCount { get; private set; }
        public int IssuedStreamCount => _streams.Count;
        public int DisposedStreamCount => _disposedStreams;

        public override Task<UsenetDecodedBodyBatch> DecodedBodiesAsync(
            IReadOnlyList<SegmentId> segmentIds,
            ArticleBodyCompletionHandler? onConnectionReadyAgain,
            CancellationToken cancellationToken)
        {
            BatchRequestCount++;
            Interlocked.Exchange(ref _batchOutstanding, 1);

            var responses = new List<Task<UsenetDecodedBodyResponse>>();
            foreach (var segmentId in segmentIds)
            {
                var stream = new TrackingYencStream(OnStreamDisposed);
                _streams.Add(stream);
                responses.Add(Task.FromResult(new UsenetDecodedBodyResponse
                {
                    SegmentId = segmentId.ToString(),
                    ResponseCode = (int)UsenetResponseType.ArticleRetrievedBodyFollows,
                    ResponseMessage = $"222 <{segmentId}>",
                    Stream = stream,
                }));
            }

            void CompleteBatch()
            {
                if (Interlocked.Exchange(ref _batchOutstanding, 0) != 1) return;
                CompletionCallbackCount++;
                onConnectionReadyAgain?.Invoke(ArticleBodyResult.Retrieved);
            }

            cancellationToken.Register(static state => ((Action)state!)(), (Action)CompleteBatch);

            foreach (var segmentId in segmentIds.Take(GapFillLimits.MaxConsecutiveZeroFills))
                PlaybackHoleTracker.RecordHole(
                    path, segmentId.ToString(), new UsenetArticleNotFoundException(segmentId.ToString()));

            return Task.FromResult(new UsenetDecodedBodyBatch { Responses = responses });

            void OnStreamDisposed()
            {
                if (Interlocked.Increment(ref _disposedStreams) == _streams.Count)
                    CompleteBatch();
            }
        }

        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId,
            ArticleBodyCompletionHandler? onConnectionReadyAgain,
            CancellationToken cancellationToken)
        {
            if (Volatile.Read(ref _batchOutstanding) != 0)
            {
                return Task.FromException<UsenetDecodedBodyResponse>(
                    new InvalidOperationException("The pooled connection is still held by the abandoned batch."));
            }

            onConnectionReadyAgain?.Invoke(ArticleBodyResult.Retrieved);
            return Task.FromResult(new UsenetDecodedBodyResponse
            {
                SegmentId = segmentId.ToString(),
                ResponseCode = (int)UsenetResponseType.ArticleRetrievedBodyFollows,
                ResponseMessage = $"222 <{segmentId}>",
                Stream = new YencStream(new MemoryStream([1, 2, 3, 4, 5], writable: false)),
            });
        }

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
            SegmentId segmentId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

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

        public override void Dispose()
        {
        }

        private sealed class TrackingYencStream(Action onDisposed)
            : YencStream(new MemoryStream([1, 2, 3, 4, 5], writable: false))
        {
            protected override void Dispose(bool disposing)
            {
                onDisposed();
                base.Dispose(disposing);
            }
        }
    }
}
