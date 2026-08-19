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

public class FallbackSizeVerificationTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public async Task Fallback_WithMismatchedYencPartSize_IsSkipped_ThenNextDonorServed(int articleBufferSize)
    {
        var client = new FakeNntpClient(
            new Dictionary<string, byte[]>
            {
                ["bad"] = Encoding.ASCII.GetBytes("XXXXX"),
                ["good"] = Encoding.ASCII.GetBytes("hello"),
            },
            useCachedYencStreams: true,
            segmentRanges: new Dictionary<string, LongRange>
            {
                ["bad"] = LongRange.FromStartAndSize(0, 99),
                ["good"] = LongRange.FromStartAndSize(0, 5),
            });

        await using var stream = CreateStream(
            client,
            articleBufferSize,
            fallbacks: [["bad", "good"]],
            exactSegmentSizes: [5]);

        using var destination = new MemoryStream();
        await stream.CopyToAsync(destination);

        Assert.Equal("hello", Encoding.ASCII.GetString(destination.ToArray()));
        Assert.Contains("missing", client.RequestedSegmentIds);
        Assert.Contains("bad", client.RequestedSegmentIds);
        Assert.Contains("good", client.RequestedSegmentIds);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public async Task Fallback_WithMismatchedPartSize_AllDonorsBad_ZeroFills(int articleBufferSize)
    {
        var client = new FakeNntpClient(
            new Dictionary<string, byte[]>
            {
                ["bad-1"] = Encoding.ASCII.GetBytes("XXXXX"),
                ["bad-2"] = Encoding.ASCII.GetBytes("YYYYY"),
            },
            useCachedYencStreams: true,
            segmentRanges: new Dictionary<string, LongRange>
            {
                ["bad-1"] = LongRange.FromStartAndSize(0, 99),
                ["bad-2"] = LongRange.FromStartAndSize(0, 77),
            });

        await using var stream = CreateStream(
            client,
            articleBufferSize,
            fallbacks: [["bad-1", "bad-2"]],
            exactSegmentSizes: [5]);

        using var destination = new MemoryStream();
        await stream.CopyToAsync(destination);

        Assert.Equal(new byte[5], destination.ToArray());
        Assert.Contains("bad-1", client.RequestedSegmentIds);
        Assert.Contains("bad-2", client.RequestedSegmentIds);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public async Task Fallback_WithMatchingYencPartSize_IsServed(int articleBufferSize)
    {
        var client = new FakeNntpClient(
            new Dictionary<string, byte[]>
            {
                ["good"] = Encoding.ASCII.GetBytes("hello"),
            },
            useCachedYencStreams: true,
            segmentRanges: new Dictionary<string, LongRange>
            {
                ["good"] = LongRange.FromStartAndSize(0, 5),
            });

        await using var stream = CreateStream(
            client,
            articleBufferSize,
            fallbacks: [["good"]],
            exactSegmentSizes: [5]);

        using var destination = new MemoryStream();
        await stream.CopyToAsync(destination);

        Assert.Equal("hello", Encoding.ASCII.GetString(destination.ToArray()));
        Assert.Contains("good", client.RequestedSegmentIds);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public async Task Fallback_WithUnknownExactSize_BypassesVerification(int articleBufferSize)
    {
        var client = new FakeNntpClient(
            new Dictionary<string, byte[]>
            {
                ["odd-size"] = Encoding.ASCII.GetBytes("hello"),
            },
            useCachedYencStreams: true,
            segmentRanges: new Dictionary<string, LongRange>
            {
                ["odd-size"] = LongRange.FromStartAndSize(0, 99),
            });

        await using var stream = CreateStream(
            client,
            articleBufferSize,
            fallbacks: [["odd-size"]]);

        using var destination = new MemoryStream();
        await stream.CopyToAsync(destination);

        Assert.Equal("hello", Encoding.ASCII.GetString(destination.ToArray()));
        Assert.Contains("odd-size", client.RequestedSegmentIds);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public async Task Fallback_NonYencBody_BypassesVerification(int articleBufferSize)
    {
        var client = new PlainBodyNntpClient(new Dictionary<string, byte[]>
        {
            ["plain"] = Encoding.ASCII.GetBytes("hello"),
        });

        await using var stream = CreateStream(
            client,
            articleBufferSize,
            fallbacks: [["plain"]],
            exactSegmentSizes: [5]);

        using var destination = new MemoryStream();
        await stream.CopyToAsync(destination);

        Assert.Equal("hello", Encoding.ASCII.GetString(destination.ToArray()));
        Assert.Contains("plain", client.RequestedSegmentIds);
    }

    private static Stream CreateStream(
        NntpClient client,
        int articleBufferSize,
        string[][] fallbacks,
        long[]? exactSegmentSizes = null) =>
        MultiSegmentStream.Create(
            new[] { "missing" }.AsMemory(),
            client,
            articleBufferSize,
            estimatedSegmentSize: 5,
            failFastOnFirstSegment: false,
            usePipelinedBodyRequests: false,
            cancellationToken: CancellationToken.None,
            fileName: "verify.bin",
            segmentFallbacks: fallbacks,
            exactSegmentSizes: exactSegmentSizes);

    /// <summary>
    /// Returns already-decoded <see cref="MemoryStream"/> bodies that are not
    /// <c>YencStream</c>, so size verification must bypass rather than peek headers.
    /// </summary>
    private sealed class PlainBodyNntpClient(
        IReadOnlyDictionary<string, byte[]> segments) : NntpClient
    {
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
                Stream = new NonYencBodyStream(bytes),
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
    /// Already-decoded payload typed as <see cref="YencStream"/> (the BODY response
    /// surface) but not yEnc-encoded, so header peeking is unverifiable and must accept.
    /// </summary>
    private sealed class NonYencBodyStream(byte[] bytes) : YencStream(new MemoryStream(bytes, writable: false))
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
