using System.Text;
using MemoryPack;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Streams;
using UsenetSharp.Models;
using UsenetSharp.Streams;

namespace NzbWebDAV.Tests.Streams;

public class SegmentFallbackTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MultiSegmentStream_FallsBackWhenPrimaryArticleMissing(
        bool usePipelinedBodyRequests)
    {
        var client = new RawBodyNntpClient(new Dictionary<string, byte[]>
        {
            // primary "one" is intentionally absent → 430
            ["one-fallback"] = Encoding.ASCII.GetBytes("hello"),
            ["two"] = Encoding.ASCII.GetBytes("world"),
        });

        await using var stream = MultiSegmentStream.Create(
            new[] { "one", "two" }.AsMemory(),
            client,
            articleBufferSize: 4,
            estimatedSegmentSize: 5,
            failFastOnFirstSegment: false,
            usePipelinedBodyRequests: usePipelinedBodyRequests,
            cancellationToken: CancellationToken.None,
            fileName: "fallback.bin",
            segmentFallbacks: [["one-fallback"], []]);

        using var destination = new MemoryStream();
        await stream.CopyToAsync(destination);

        Assert.Equal("helloworld", Encoding.ASCII.GetString(destination.ToArray()));
        Assert.Contains("one", client.RequestedSegmentIds);
        Assert.Contains("one-fallback", client.RequestedSegmentIds);
        Assert.Contains("two", client.RequestedSegmentIds);
        // Primary 430 + fallback success + second segment.
        Assert.True(client.BodyRequestCount >= 3,
            $"Expected BODY for primary, fallback, and second segment; got {client.BodyRequestCount}");
    }

    [Fact]
    public async Task MultiSegmentStream_Unbuffered_FallsBackWhenPrimaryMissing()
    {
        var client = new RawBodyNntpClient(new Dictionary<string, byte[]>
        {
            ["alt"] = Encoding.ASCII.GetBytes("abcde"),
        });

        await using var stream = MultiSegmentStream.Create(
            new[] { "missing" }.AsMemory(),
            client,
            articleBufferSize: 0,
            estimatedSegmentSize: 5,
            failFastOnFirstSegment: false,
            usePipelinedBodyRequests: false,
            cancellationToken: CancellationToken.None,
            segmentFallbacks: [["alt"]]);

        var buffer = new byte[5];
        var read = await stream.ReadAsync(buffer);

        Assert.Equal(5, read);
        Assert.Equal("abcde", Encoding.ASCII.GetString(buffer));
        Assert.Equal(2, client.BodyRequestCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MultiSegmentStream_ConsecutiveMissingArticlesStopPrefetch(
        bool usePipelinedBodyRequests)
    {
        // The consumer aborts on the third consecutive zero-fill, but each consumed
        // zero-fill releases prefetch-ceiling bytes first, so the producer may legally
        // top up one more batch before it observes the cancellation. With a window of
        // articleBufferSize * batchWidth (16) plus that top-up, up to ~24 requests can
        // fire; use enough segments that even the worst-case interleaving stays below
        // the total, keeping this assertion deterministic (see #1031).
        var segmentIds = Enumerable.Range(0, 40)
            .Select(index => $"missing-{index}")
            .ToArray();
        var client = new RawBodyNntpClient(new Dictionary<string, byte[]>());

        await using var stream = MultiSegmentStream.Create(
            segmentIds.AsMemory(),
            client,
            articleBufferSize: 4,
            estimatedSegmentSize: 5,
            failFastOnFirstSegment: false,
            usePipelinedBodyRequests: usePipelinedBodyRequests,
            cancellationToken: CancellationToken.None,
            fileName: $"dead-prefetch-{usePipelinedBodyRequests}.bin",
            exactSegmentSizes: Enumerable.Repeat(5L, segmentIds.Length).ToArray());

        await Assert.ThrowsAsync<UsenetArticleNotFoundException>(
            () =>
            {
                using var destination = new MemoryStream();
                return stream.CopyToAsync(destination);
            });

        // Wait for the producer loop to exit instead of sleeping: if the
        // consecutive-zero-fill cancellation regresses, the producer blocks at the
        // prefetch ceiling and this times out instead of passing vacuously.
        var multiSegmentStream = Assert.IsType<MultiSegmentStream>(stream);
        await multiSegmentStream.DownloadTaskForTests.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(
            client.BodyRequestCount < segmentIds.Length,
            $"Expected prefetch cancellation before all {segmentIds.Length} articles, got {client.BodyRequestCount} requests.");
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(4, true)]
    public async Task MultiSegmentStream_SuccessResetsConsecutiveZeroFillCount(
        int articleBufferSize,
        bool usePipelinedBodyRequests)
    {
        var client = new RawBodyNntpClient(new Dictionary<string, byte[]>
        {
            ["good"] = Encoding.ASCII.GetBytes("abcde"),
        });
        await using var stream = MultiSegmentStream.Create(
            new[] { "missing-one", "good", "missing-two", "missing-three", "missing-four" }.AsMemory(),
            client,
            articleBufferSize: articleBufferSize,
            estimatedSegmentSize: 5,
            failFastOnFirstSegment: false,
            usePipelinedBodyRequests: usePipelinedBodyRequests,
            cancellationToken: CancellationToken.None,
            fileName: $"reset-zero-fill-streak-{articleBufferSize}.bin",
            exactSegmentSizes: new long[] { 5, 5, 5, 5, 5 });

        var buffer = new byte[5];
        Assert.Equal(5, await stream.ReadAsync(buffer));
        Assert.Equal(5, await stream.ReadAsync(buffer));
        Assert.Equal("abcde", Encoding.ASCII.GetString(buffer));
        Assert.Equal(5, await stream.ReadAsync(buffer));
        Assert.Equal(5, await stream.ReadAsync(buffer));
        await Assert.ThrowsAsync<UsenetArticleNotFoundException>(
            async () => await stream.ReadAtLeastAsync(
                buffer, buffer.Length, throwOnEndOfStream: false));

        Assert.Equal(5, client.BodyRequestCount);
    }

    [Fact]
    public void DavNzbFile_MemoryPackRoundTrip_WithAndWithoutFallbacks()
    {
        var without = new DavNzbFile
        {
            Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            SegmentIds = ["a", "b"],
        };
        var with = new DavNzbFile
        {
            Id = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            SegmentIds = ["a", "b"],
            SegmentFallbackIds = [[], ["b-alt1", "b-alt2"]],
        };

        var withoutRoundTrip = MemoryPackSerializer.Deserialize<DavNzbFile>(
            MemoryPackSerializer.Serialize(without))!;
        var withRoundTrip = MemoryPackSerializer.Deserialize<DavNzbFile>(
            MemoryPackSerializer.Serialize(with))!;

        Assert.Equal(without.Id, withoutRoundTrip.Id);
        Assert.Equal(without.SegmentIds, withoutRoundTrip.SegmentIds);
        Assert.Null(withoutRoundTrip.SegmentFallbackIds);

        Assert.Equal(with.Id, withRoundTrip.Id);
        Assert.Equal(with.SegmentIds, withRoundTrip.SegmentIds);
        Assert.Equal(with.SegmentFallbackIds, withRoundTrip.SegmentFallbackIds);
    }

    [Fact]
    public void DavNzbFile_LegacyBlobWithoutFallbackField_DeserializesNullFallbacks()
    {
        var legacy = new DavNzbFile
        {
            Id = Guid.Parse("33333333-3333-3333-3333-333333333333"),
            SegmentIds = ["seg-1"],
            SegmentFallbackIds = null,
        };
        var bytes = MemoryPackSerializer.Serialize(legacy);
        var roundTrip = MemoryPackSerializer.Deserialize<DavNzbFile>(bytes)!;

        Assert.Null(roundTrip.SegmentFallbackIds);
        Assert.Equal(["seg-1"], roundTrip.SegmentIds);
    }

    /// <summary>
    /// Returns already-decoded body streams so tests do not require rapidyenc.
    /// </summary>
    private sealed class RawBodyNntpClient(
        IReadOnlyDictionary<string, byte[]> segments) : NntpClient
    {
        public int BodyRequestCount { get; private set; }
        public HashSet<string> RequestedSegmentIds { get; } = new(StringComparer.Ordinal);

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
            DecodedBodyAsync(segmentId, null, cancellationToken);

        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId,
            ArticleBodyCompletionHandler? onConnectionReadyAgain,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BodyRequestCount++;
            var key = segmentId.ToString();
            RequestedSegmentIds.Add(key);
            if (!segments.TryGetValue(key, out var bytes))
            {
                onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotFound);
                return Task.FromException<UsenetDecodedBodyResponse>(
                    new UsenetArticleNotFoundException(key, "430 No such article"));
            }

            onConnectionReadyAgain?.Invoke(ArticleBodyResult.Retrieved);
            return Task.FromResult(new UsenetDecodedBodyResponse
            {
                SegmentId = key,
                ResponseCode = (int)UsenetResponseType.ArticleRetrievedBodyFollows,
                ResponseMessage = "222 fake body",
                Stream = new RawYencStream(bytes),
            });
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

        public override Task<UsenetExclusiveConnection> AcquireExclusiveConnectionAsync(
            IReadOnlyList<SegmentId> segmentIds, CancellationToken cancellationToken) =>
            Task.FromResult(new UsenetExclusiveConnection(null));

        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId,
            UsenetExclusiveConnection exclusiveConnection,
            CancellationToken cancellationToken) =>
            DecodedBodyAsync(segmentId, exclusiveConnection.OnConnectionReadyAgain, cancellationToken);

        public override Task<UsenetDecodedBodyBatch> DecodedBodiesAsync(
            IReadOnlyList<SegmentId> segmentIds,
            UsenetExclusiveConnection exclusiveConnection,
            CancellationToken cancellationToken) =>
            DecodedBodiesAsync(
                segmentIds, exclusiveConnection.OnConnectionReadyAgain, cancellationToken);

        public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
            SegmentId segmentId,
            UsenetExclusiveConnection exclusiveConnection,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override void Dispose()
        {
        }
    }

    /// <summary>
    /// Already-decoded body stream typed as <see cref="YencStream"/> so tests
    /// do not require the rapidyenc native library.
    /// </summary>
    private sealed class RawYencStream(byte[] bytes) : YencStream(new MemoryStream(bytes, writable: false))
    {
        private int _offset;

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_offset >= bytes.Length) return 0;
            var count = Math.Min(buffer.Length, bytes.Length - _offset);
            bytes.AsSpan(_offset, count).CopyTo(buffer.Span);
            _offset += count;
            return await Task.FromResult(count).ConfigureAwait(false);
        }
    }
}
