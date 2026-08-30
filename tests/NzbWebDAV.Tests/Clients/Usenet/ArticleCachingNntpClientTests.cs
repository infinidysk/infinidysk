using System.Text;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Streams;
using NzbWebDAV.Tests.Fakes;
using NzbWebDAV.Tests.TestUtils;
using UsenetSharp.Models;

namespace NzbWebDAV.Tests.Clients.Usenet;

public class ArticleCachingNntpClientTests
{
    private static readonly UsenetArticleHeader FixedArticleHeaders = new()
    {
        Headers = new Dictionary<string, string>
        {
            ["Subject"] = "cache-test",
            ["Message-ID"] = "<segment@test>",
        },
    };

    [SkippableFact]
    public async Task DecodedBodyAsync_CachesDecodedBytesAfterFirstRead()
    {
        Skip.IfNot(RapidYenc.IsAvailable, "rapidyenc native library not available on this platform");
        var inner = new FakeNntpClient(new Dictionary<string, byte[]>
        {
            ["segment"] = Encoding.ASCII.GetBytes("cached payload")
        });
        using var client = new ArticleCachingNntpClient(inner);

        var first = await client.DecodedBodyAsync("segment", CancellationToken.None);
        var firstBytes = await ReadAllAsync(first.Stream!);
        var second = await client.DecodedBodyAsync("segment", CancellationToken.None);
        var secondBytes = await ReadAllAsync(second.Stream!);

        Assert.Equal("cached payload", Encoding.ASCII.GetString(firstBytes));
        Assert.Equal(firstBytes, secondBytes);
        Assert.Equal(1, inner.BodyRequestCount);
    }

    [SkippableFact]
    public async Task DecodedBodiesAsync_PreservesOrderAcrossCachedAndMissingSegments()
    {
        Skip.IfNot(RapidYenc.IsAvailable, "rapidyenc native library not available on this platform");
        var inner = new FakeNntpClient(new Dictionary<string, byte[]>
        {
            ["one"] = Encoding.ASCII.GetBytes("one"),
            ["two"] = Encoding.ASCII.GetBytes("two")
        });
        using var client = new ArticleCachingNntpClient(inner);
        var cached = await client.DecodedBodyAsync("one", CancellationToken.None);
        await ReadAllAsync(cached.Stream!);

        var batch = await client.DecodedBodiesAsync(
            ["one", "two"], onConnectionReadyAgain: null, CancellationToken.None);
        var responses = await Task.WhenAll(batch.Responses);
        var bodies = new List<string>();
        foreach (var response in responses)
            bodies.Add(Encoding.ASCII.GetString(await ReadAllAsync(response.Stream!)));

        Assert.Equal(new[] { "one", "two" }, bodies);
        Assert.Equal(1, inner.BatchRequestCount);
    }

    [Fact]
    public async Task DecodedBodyAsync_CacheHit_ThrowingCallbackReturnsCachedBody()
    {
        const string segmentId = "segment";
        byte[] payload = "cached payload"u8.ToArray();
        var inner = new FakeNntpClient(
            new Dictionary<string, byte[]> { [segmentId] = payload },
            useCachedYencStreams: true);
        using var client = new ArticleCachingNntpClient(inner);

        var primed = await client.DecodedBodyAsync(segmentId, CancellationToken.None);
        await ReadAllAsync(primed.Stream!);

        var recorder = new ThrowingRecorder();
        var response = await client.DecodedBodyAsync(segmentId, recorder.Invoke, CancellationToken.None);
        var bytes = await ReadAllAsync(response.Stream!);

        Assert.Equal((int)UsenetResponseType.ArticleRetrievedBodyFollows, response.ResponseCode);
        Assert.Equal(payload, bytes);
        Assert.Equal(1, recorder.Count);
        Assert.Equal(ArticleBodyResult.Retrieved, recorder.Result);
        Assert.Equal(1, inner.BodyRequestCount);
    }

    [Fact]
    public async Task DecodedBodiesAsync_AllCached_ThrowingCallbackReturnsOrderedBodies()
    {
        var inner = new FakeNntpClient(
            new Dictionary<string, byte[]>
            {
                ["one"] = "one"u8.ToArray(),
                ["two"] = "two"u8.ToArray(),
            },
            useCachedYencStreams: true);
        using var client = new ArticleCachingNntpClient(inner);
        await ReadAllAsync((await client.DecodedBodyAsync("one", CancellationToken.None)).Stream!);
        await ReadAllAsync((await client.DecodedBodyAsync("two", CancellationToken.None)).Stream!);

        var recorder = new ThrowingRecorder();
        var batch = await client.DecodedBodiesAsync(
            ["one", "two"], recorder.Invoke, CancellationToken.None);
        var responses = await Task.WhenAll(batch.Responses);
        var bodies = new List<string>();
        foreach (var response in responses)
            bodies.Add(Encoding.ASCII.GetString(await ReadAllAsync(response.Stream!)));

        Assert.Equal(["one", "two"], bodies);
        Assert.Equal(1, recorder.Count);
        Assert.Equal(ArticleBodyResult.Retrieved, recorder.Result);
        Assert.Equal(2, inner.BodyRequestCount);
        Assert.Equal(0, inner.BatchRequestCount);
    }

    [Fact]
    public async Task DecodedArticleAsync_FullCacheHit_ThrowingCallbackReturnsCachedArticle()
    {
        const string segmentId = "article-segment";
        byte[] payload = "article-bytes"u8.ToArray();
        var inner = new CacheProbeNntpClient { Segments = { [segmentId] = payload } };
        using var client = new ArticleCachingNntpClient(inner);

        var primed = await client.DecodedArticleAsync(segmentId, CancellationToken.None);
        var primedBytes = await ReadAllAsync(primed.Stream);
        Assert.Equal(FixedArticleHeaders.Headers, primed.ArticleHeaders.Headers);

        var recorder = new ThrowingRecorder();
        var response = await client.DecodedArticleAsync(segmentId, recorder.Invoke, CancellationToken.None);
        var bytes = await ReadAllAsync(response.Stream);

        Assert.Equal(payload, primedBytes);
        Assert.Equal(payload, bytes);
        Assert.Equal(FixedArticleHeaders.Headers, response.ArticleHeaders.Headers);
        Assert.Equal(1, recorder.Count);
        Assert.Equal(ArticleBodyResult.Retrieved, recorder.Result);
        Assert.Equal(1, inner.ArticleRequestCount);
    }

    [Fact]
    public async Task DecodedBodyAsync_CancelledWhileWaiting_ThrowingCallbackPreservesCancellation()
    {
        const string segmentId = "held-segment";
        byte[] payload = "held-bytes"u8.ToArray();
        var inner = new CacheProbeNntpClient
        {
            Segments = { [segmentId] = payload },
            GateFirstBody = true,
        };
        using var client = new ArticleCachingNntpClient(inner);

        var first = client.DecodedBodyAsync(segmentId, CancellationToken.None);
        await inner.BodyEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        using var cts = new CancellationTokenSource();
        var recorder = new ThrowingRecorder();
        var waiting = client.DecodedBodyAsync(segmentId, recorder.Invoke, cts.Token);
        await WaitUntilAsync(() => !waiting.IsCompleted, TimeSpan.FromSeconds(5));
        await cts.CancelAsync();

        var cancelled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.True(cancelled.CancellationToken == cts.Token || cancelled is TaskCanceledException);
        Assert.Equal(1, recorder.Count);
        Assert.Equal(ArticleBodyResult.NotRetrieved, recorder.Result);
        Assert.Equal(1, inner.BodyRequestCount);

        inner.BodyContinue.TrySetResult();
        await ReadAllAsync((await first).Stream!);

        var after = await client.DecodedBodyAsync(segmentId, CancellationToken.None);
        await ReadAllAsync(after.Stream!);
        Assert.Equal(1, inner.BodyRequestCount);
    }

    [Fact]
    public async Task DecodedArticleAsync_BodyCachedHeadThrows_ThrowingCallbackPreservesHeadException()
    {
        const string segmentId = "head-segment";
        byte[] payload = "body-only"u8.ToArray();
        var inner = new CacheProbeNntpClient { Segments = { [segmentId] = payload } };
        using var client = new ArticleCachingNntpClient(inner);

        await ReadAllAsync((await client.DecodedBodyAsync(segmentId, CancellationToken.None)).Stream!);
        var sentinel = new InvalidOperationException("sentinel-head");
        inner.HeadException = sentinel;

        var recorder = new ThrowingRecorder();
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.DecodedArticleAsync(segmentId, recorder.Invoke, CancellationToken.None));

        Assert.Same(sentinel, thrown);
        Assert.Equal(1, recorder.Count);
        Assert.Equal(ArticleBodyResult.Retrieved, recorder.Result);
        Assert.Equal(0, inner.ArticleRequestCount);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(10);
        }

        Assert.Fail($"Condition not met within {timeout.TotalSeconds:0.#}s");
    }

    private static async Task<byte[]> ReadAllAsync(Stream stream)
    {
        await using (stream)
        {
            using var destination = new MemoryStream();
            await stream.CopyToAsync(destination);
            return destination.ToArray();
        }
    }

    private sealed class ThrowingRecorder
    {
        public int Count;
        public ArticleBodyResult? Result;
        public string? FailureReason;

        public void Invoke(ArticleBodyResult result, string? failureReason)
        {
            Count++;
            Result = result;
            FailureReason = failureReason;
            throw new InvalidOperationException("callback failure");
        }
    }

    private sealed class CacheProbeNntpClient : NntpClient
    {
        public Dictionary<string, byte[]> Segments { get; } = new(StringComparer.Ordinal);
        public int BodyRequestCount { get; private set; }
        public int ArticleRequestCount { get; private set; }
        public bool GateFirstBody { get; set; }
        public Exception? HeadException { get; set; }
        public TaskCompletionSource BodyEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource BodyContinue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override Task ConnectAsync(string host, int port, bool useSsl, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public override Task<UsenetResponse> AuthenticateAsync(
            string user, string pass, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetStatResponse> StatAsync(
            SegmentId segmentId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetHeadResponse> HeadAsync(
            SegmentId segmentId, CancellationToken cancellationToken)
        {
            if (HeadException != null)
                throw HeadException;

            return Task.FromResult(new UsenetHeadResponse
            {
                SegmentId = segmentId.ToString(),
                ResponseCode = (int)UsenetResponseType.ArticleRetrievedHeadFollows,
                ResponseMessage = "221",
                ArticleHeaders = FixedArticleHeaders,
            });
        }

        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId, CancellationToken cancellationToken) =>
            DecodedBodyAsync(segmentId, null, cancellationToken);

        public override async Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId,
            ArticleBodyCompletionHandler? onConnectionReadyAgain,
            CancellationToken cancellationToken)
        {
            BodyRequestCount++;
            if (GateFirstBody && BodyRequestCount == 1)
            {
                BodyEntered.TrySetResult();
                await BodyContinue.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            var response = CreateBody(segmentId);
            onConnectionReadyAgain?.Invoke(ArticleBodyResult.Retrieved);
            return response;
        }

        public override Task<UsenetDecodedBodyBatch> DecodedBodiesAsync(
            IReadOnlyList<SegmentId> segmentIds,
            ArticleBodyCompletionHandler? onConnectionReadyAgain,
            CancellationToken cancellationToken)
        {
            var responses = segmentIds
                .Select(id => DecodedBodyAsync(id, cancellationToken))
                .ToArray();
            onConnectionReadyAgain?.Invoke(ArticleBodyResult.Retrieved);
            return Task.FromResult(new UsenetDecodedBodyBatch { Responses = responses });
        }

        public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
            SegmentId segmentId, CancellationToken cancellationToken) =>
            DecodedArticleAsync(segmentId, null, cancellationToken);

        public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
            SegmentId segmentId,
            ArticleBodyCompletionHandler? onConnectionReadyAgain,
            CancellationToken cancellationToken)
        {
            ArticleRequestCount++;
            var bytes = Segments[segmentId.ToString()];
            onConnectionReadyAgain?.Invoke(ArticleBodyResult.Retrieved);
            return Task.FromResult(new UsenetDecodedArticleResponse
            {
                SegmentId = segmentId.ToString(),
                ResponseCode = (int)UsenetResponseType.ArticleRetrievedHeadAndBodyFollow,
                ResponseMessage = "220",
                ArticleHeaders = FixedArticleHeaders,
                Stream = CreateStream(bytes),
            });
        }

        public override Task<UsenetDateResponse> DateAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override void Dispose()
        {
        }

        private UsenetDecodedBodyResponse CreateBody(SegmentId segmentId)
        {
            var bytes = Segments[segmentId.ToString()];
            return new UsenetDecodedBodyResponse
            {
                SegmentId = segmentId.ToString(),
                ResponseCode = (int)UsenetResponseType.ArticleRetrievedBodyFollows,
                ResponseMessage = "222",
                Stream = CreateStream(bytes),
            };
        }

        private static CachedYencStream CreateStream(byte[] bytes) =>
            new(
                new UsenetYencHeader
                {
                    FileName = "fake.bin",
                    FileSize = bytes.Length,
                    LineLength = 128,
                    PartNumber = 1,
                    TotalParts = 1,
                    PartOffset = 0,
                    PartSize = bytes.Length,
                },
                new MemoryStream(bytes, writable: false));
    }
}
