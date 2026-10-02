using System.Text;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Models.Nzb;
using NzbWebDAV.Queue.DeobfuscationSteps._1.FetchFirstSegment;
using NzbWebDAV.Queue.DeobfuscationSteps._2.GetPar2FileDescriptors;
using NzbWebDAV.Queue.DeobfuscationSteps._3.GetFileInfos;
using NzbWebDAV.Queue.FileProcessors;
using NzbWebDAV.Streams;
using NzbWebDAV.Tests.Fakes;
using NzbWebDAV.Tests.TestUtils;
using UsenetSharp.Exceptions;
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

        var recorder = new ArticleBodyCompletionRecorder(throwOnInvoke: true);
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

        var recorder = new ArticleBodyCompletionRecorder(throwOnInvoke: true);
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
    public async Task DecodedArticlesPipelinedAsync_CachesFirstArticlesForLaterBodyReads()
    {
        var inner = new FakeNntpClient(
            new Dictionary<string, byte[]> { ["segment"] = "first article"u8.ToArray() },
            useCachedYencStreams: true);
        using var client = new ArticleCachingNntpClient(inner);

        var pipelined = new List<string>();
        await foreach (var article in client.DecodedArticlesPipelinedAsync(["segment"], 4, CancellationToken.None))
            pipelined.Add(Encoding.ASCII.GetString(await ReadAllAsync(article.Stream!)));
        var requestsAfterPipeline = inner.BodyRequestCount + inner.BatchRequestCount;
        var body = await client.DecodedBodyAsync("segment", CancellationToken.None);

        Assert.Equal(["first article"], pipelined);
        Assert.Equal("first article", Encoding.ASCII.GetString(await ReadAllAsync(body.Stream!)));
        Assert.Equal(requestsAfterPipeline, inner.BodyRequestCount + inner.BatchRequestCount);
    }

    [Fact]
    public async Task DecodedArticlesPipelinedAsync_PrefixOnlyConsumer_LaterBodyReadIsCompleteWithoutRefetch()
    {
        var payload = Enumerable.Range(0, 64 * 1024).Select(i => (byte)i).ToArray();
        var inner = new CacheProbeNntpClient { Segments = { ["big"] = payload } };
        inner.PipelinedArticles.Add(("big", new ProbeStream(payload)));
        using var client = new ArticleCachingNntpClient(inner);

        await foreach (var article in client.DecodedArticlesPipelinedAsync(["big"], 4, CancellationToken.None))
        {
            await using var stream = article.Stream!;
            var prefix = new byte[16 * 1024];
            await stream.ReadExactlyAsync(prefix);
            Assert.Equal(payload.AsSpan(0, prefix.Length).ToArray(), prefix);
        }

        var body = await client.DecodedBodyAsync("big", CancellationToken.None);

        Assert.Equal(payload, await ReadAllAsync(body.Stream!));
        Assert.Equal(0, inner.BodyRequestCount);
        Assert.True(inner.PipelinedArticles[0].Inner.Disposed);
    }

    [Fact]
    public async Task DecodedArticlesPipelinedAsync_TailReadFailure_DefersOnlyThatArticleToRescue()
    {
        var bad = Enumerable.Repeat((byte)'b', 64 * 1024).ToArray();
        var good = "good"u8.ToArray();
        var inner = new CacheProbeNntpClient { Segments = { ["bad"] = bad, ["good"] = good } };
        inner.PipelinedArticles.Add(("bad", new ProbeStream(bad, failAfter: 16 * 1024)));
        inner.PipelinedArticles.Add(("good", new ProbeStream(good)));
        using var client = new ArticleCachingNntpClient(inner);

        var results = new List<(PipelinedArticleResult Article, string? Body)>();
        await foreach (var article in client.DecodedArticlesPipelinedAsync(["bad", "good"], 4, CancellationToken.None))
        {
            var body = article.Stream is null ? null : Encoding.ASCII.GetString(await ReadAllAsync(article.Stream));
            results.Add((article, body));
        }

        Assert.Equal(2, results.Count);
        Assert.False(results[0].Article.Found);
        Assert.False(results[0].Article.DefinitivelyMissing);
        Assert.Null(results[0].Article.Stream);
        Assert.True(results[1].Article.Found);
        Assert.Equal("good", results[1].Body);
        Assert.All(inner.PipelinedArticles, item => Assert.True(item.Inner.Disposed));

        await ReadAllAsync((await client.DecodedBodyAsync("bad", CancellationToken.None)).Stream!);
        Assert.Equal(1, inner.BodyRequestCount);
    }

    [Fact]
    public async Task DecodedArticlesPipelinedAsync_CancelledWhileWaitingForSegmentLock_DisposesArticleStream()
    {
        var payload = "held"u8.ToArray();
        var inner = new CacheProbeNntpClient { Segments = { ["held"] = payload }, GateFirstBody = true };
        inner.PipelinedArticles.Add(("held", new ProbeStream(payload)));
        using var client = new ArticleCachingNntpClient(inner);
        var holder = client.DecodedBodyAsync("held", CancellationToken.None);
        await inner.BodyEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        using var cts = new CancellationTokenSource();
        var enumerate = Task.Run(async () =>
        {
            await foreach (var article in client.DecodedArticlesPipelinedAsync(["held"], 4, cts.Token))
                await ReadAllAsync(article.Stream!);
        });
        await inner.PipelinedYielded.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => enumerate);
        Assert.True(inner.PipelinedArticles[0].Inner.Disposed);

        inner.BodyContinue.TrySetResult();
        await ReadAllAsync((await holder).Stream!);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public async Task DecodedBodiesAsync_MalformedInnerBatch_AbandonsAndReportsNotRetrieved(int responseCount)
    {
        var inner = new ControlledDecodedBodyBatchClient(
            responseCountOverride: responseCount,
            blockCompletionUntilStreamsDisposed: true);
        using var client = new ArticleCachingNntpClient(inner);
        using var caller = new CancellationTokenSource();
        var recorder = new ArticleBodyCompletionRecorder();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.DecodedBodiesAsync(["a", "b"], recorder.Invoke, caller.Token));

        Assert.Equal("The NNTP batch response count did not match the request count.", error.Message);
        Assert.Equal(1, inner.OrdinaryBatchCount);
        Assert.True(inner.LastCancellationToken.IsCancellationRequested);
        Assert.False(caller.IsCancellationRequested);
        Assert.Equal(responseCount, inner.DisposedStreamCount);
        Assert.True(inner.ProducerCompletion.IsCompleted);
        Assert.Equal(1, recorder.Count);
        Assert.Equal(ArticleBodyResult.NotRetrieved, recorder.Result);
        Assert.Equal("batch-response-count-mismatch", recorder.FailureReason);
    }

    [Fact]
    public async Task DecodedBodiesAsync_MalformedInnerBatchCompletionOom_StillReportsNotRetrieved()
    {
        var inner = new ControlledDecodedBodyBatchClient(
            responseCountOverride: 0,
            completionException: new OutOfMemoryException("batch-cleanup"));
        using var client = new ArticleCachingNntpClient(inner);
        var recorder = new ArticleBodyCompletionRecorder();

        await Assert.ThrowsAsync<OutOfMemoryException>(() =>
            client.DecodedBodiesAsync(["a", "b"], recorder.Invoke, CancellationToken.None));

        Assert.Equal(1, recorder.Count);
        Assert.Equal(ArticleBodyResult.NotRetrieved, recorder.Result);
        Assert.Equal("batch-response-count-mismatch", recorder.FailureReason);
    }

    [Fact]
    public async Task DecodedBodiesAsync_SetupCleanupOom_StillReportsNotRetrieved()
    {
        var inner = new ControlledDecodedBodyBatchClient(
            completionException: new OutOfMemoryException("cleanup"),
            responses: new ThrowingCountResponses());
        var recorder = new ArticleBodyCompletionRecorder();
        using var client = new ArticleCachingNntpClient(inner);

        await Assert.ThrowsAsync<OutOfMemoryException>(() =>
            client.DecodedBodiesAsync(["a"], recorder.Invoke, CancellationToken.None));

        Assert.Equal(1, recorder.Count);
        Assert.Equal(ArticleBodyResult.NotRetrieved, recorder.Result);
        Assert.Equal("cache-batch-setup", recorder.FailureReason);
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

        var recorder = new ArticleBodyCompletionRecorder(throwOnInvoke: true);
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
        var recorder = new ArticleBodyCompletionRecorder(throwOnInvoke: true);
        var waiting = client.DecodedBodyAsync(segmentId, recorder.Invoke, cts.Token);
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

        var recorder = new ArticleBodyCompletionRecorder(throwOnInvoke: true);
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.DecodedArticleAsync(segmentId, recorder.Invoke, CancellationToken.None));

        Assert.Same(sentinel, thrown);
        Assert.Equal(1, recorder.Count);
        Assert.Equal(ArticleBodyResult.Retrieved, recorder.Result);
        Assert.Equal(0, inner.ArticleRequestCount);
    }

    [Theory]
    [InlineData(false, "io")]
    [InlineData(true, "io")]
    [InlineData(false, "crc")]
    [InlineData(true, "crc")]
    [InlineData(false, "protocol")]
    [InlineData(true, "protocol")]
    [InlineData(false, "timeout")]
    [InlineData(true, "timeout")]
    public async Task FetchFirstSegments_OptionalTailFailure_PreservesImportDiscovery(
        bool pipelined, string failureKind)
    {
        const string optionalId = "optional@example.test";
        const string contentId = "content@example.test";
        var optionalBytes = Enumerable.Repeat((byte)'p', 64 * 1024).ToArray();
        byte[] contentBytes = "healthy-content"u8.ToArray();
        var failure = CreateDiscoveryFailure(failureKind);
        var config = CreateDiscoveryConfig(pipelined);

        using (var prefixProbe = new ProbeStream(optionalBytes, DiscoveryPrefixLength, failure))
        {
            var prefix = new byte[DiscoveryPrefixLength];
            await prefixProbe.ReadExactlyAsync(prefix);
            Assert.Equal(optionalBytes.AsSpan(0, prefix.Length).ToArray(), prefix);
        }

        ProbeStream? lastOptionalStream = null;
        using var inner = new CacheProbeNntpClient
        {
            Segments = { [optionalId] = optionalBytes, [contentId] = contentBytes },
            DecodedStreamFactory = (segmentId, bytes) =>
            {
                if (segmentId == optionalId)
                    return lastOptionalStream = new ProbeStream(bytes, DiscoveryPrefixLength, failure);
                return new MemoryStream(bytes, writable: false);
            },
        };
        if (pipelined)
        {
            inner.PipelinedArticles.Add((optionalId,
                new ProbeStream(optionalBytes, DiscoveryPrefixLength, failure)));
            inner.PipelinedArticles.Add((contentId, new ProbeStream(contentBytes)));
        }

        using var client = new ArticleCachingNntpClient(inner);
        var files = new List<NzbFile>
        {
            CreateDiscoveryFile(optionalId, "metadata.vol00+01.par2", optionalBytes.Length),
            CreateDiscoveryFile(contentId, "payload.bin", contentBytes.Length),
        };
        client.TrackNzbFiles(files);

        var results = await FetchFirstSegmentsStep.FetchFirstSegments(
            files, client, config, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, results.Count);
        Assert.Same(files[0], results[0].NzbFile);
        Assert.Same(files[1], results[1].NzbFile);
        Assert.True(results[0].MissingFirstSegment);
        Assert.Null(results[0].First16KB);
        Assert.Null(results[0].Header);
        Assert.Null(results[0].MissingEvidenceGeneration);
        Assert.Null(files[0].Segments[0].ByteRange);
        Assert.False(results[1].MissingFirstSegment);
        Assert.Equal(contentBytes, results[1].First16KB);
        Assert.NotNull(results[1].Header);
        Assert.NotNull(files[1].Segments[0].ByteRange);
        Assert.Equal(pipelined ? 1 : 2, inner.ArticleRequestCount);
        Assert.True(lastOptionalStream!.Disposed);
        Assert.All(inner.PipelinedArticles, item => Assert.True(item.Inner.Disposed));

        var descriptors = await GetPar2FileDescriptorsStep.GetPar2FileDescriptors(
            results, client, cancellationToken: CancellationToken.None);
        Assert.Empty(descriptors);
        Assert.Equal(0, inner.BodyRequestCount);

        var fileInfos = GetFileInfosStep.GetFileInfos(results, descriptors);
        var processed = await new FileProcessor(
            fileInfos[0], client, config, CancellationToken.None).ProcessAsync();
        var optionalResult = Assert.IsType<FileProcessor.Result>(processed);
        Assert.Equal((long)optionalBytes.Length, optionalResult.FileSize);
        Assert.Equal(1, inner.BodyRequestCount);

        var bodyFailure = await Record.ExceptionAsync(async () =>
        {
            var body = await client.DecodedBodyAsync(optionalId, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(5));
            await ReadAllAsync(body.Stream!);
        });
        Assert.Same(failure, bodyFailure);
        Assert.Equal(2, inner.BodyRequestCount);
    }

    [Theory]
    [InlineData(false, "payload.rar", "io")]
    [InlineData(true, "payload.rar", "io")]
    [InlineData(false, "opaque", "crc")]
    [InlineData(true, "opaque", "crc")]
    [InlineData(false, "metadata.par2", "cancel")]
    [InlineData(true, "metadata.par2", "cancel")]
    [InlineData(false, "metadata.par2", "oom")]
    [InlineData(true, "metadata.par2", "oom")]
    [InlineData(false, "metadata.par2", "unexpected")]
    [InlineData(true, "metadata.par2", "unexpected")]
    public async Task FetchFirstSegments_TailFailure_PropagatesOutsideOptionalReadPolicy(
        bool pipelined, string fileName, string failureKind)
    {
        const string segmentId = "guard@example.test";
        var bytes = Enumerable.Repeat((byte)'g', 64 * 1024).ToArray();
        var failure = CreateDiscoveryFailure(failureKind);
        using var inner = new CacheProbeNntpClient
        {
            Segments = { [segmentId] = bytes },
            DecodedStreamFactory = (_, payload) =>
                new ProbeStream(payload, DiscoveryPrefixLength, failure),
        };
        if (pipelined)
            inner.PipelinedArticles.Add((segmentId,
                new ProbeStream(bytes, DiscoveryPrefixLength, failure)));

        using var client = new ArticleCachingNntpClient(inner);
        var error = await Record.ExceptionAsync(() => FetchFirstSegmentsStep.FetchFirstSegments(
            [CreateDiscoveryFile(segmentId, fileName, bytes.Length)],
            client, CreateDiscoveryConfig(pipelined), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5)));

        if (failureKind == "io")
        {
            var retryable = Assert.IsType<RetryableDownloadException>(error);
            Assert.Same(failure, retryable.InnerException);
        }
        else if (failureKind == "cancel")
        {
            Assert.IsAssignableFrom<OperationCanceledException>(error);
        }
        else
        {
            Assert.Same(failure, error);
        }
    }

    private const int DiscoveryPrefixLength = 16 * 1024;

    private static ConfigManager CreateDiscoveryConfig(bool pipelined)
    {
        var config = new ConfigManager();
        config.UpdateValues(
        [
            new ConfigItem
            {
                ConfigName = ConfigKeys.UsenetPipeliningEnabled,
                ConfigValue = pipelined ? "true" : "false",
            },
            new ConfigItem
            {
                ConfigName = ConfigKeys.UsenetPipeliningDepth,
                ConfigValue = "4",
            },
            new ConfigItem
            {
                ConfigName = ConfigKeys.UsenetMaxQueueConnections,
                ConfigValue = "1",
            },
        ]);
        return config;
    }

    private static NzbFile CreateDiscoveryFile(string segmentId, string fileName, int length) => new()
    {
        Subject = $"\"{fileName}\" yEnc",
        Segments = { new NzbSegment { MessageId = segmentId, Bytes = length } },
    };

    private static Exception CreateDiscoveryFailure(string kind) => kind switch
    {
        "io" => new IOException("tail-read-failure"),
        "crc" => new InvalidDataException("tail-crc-mismatch"),
        "protocol" => new UsenetProtocolException("tail-missing-terminator"),
        "timeout" => new TimeoutException("tail-read-timeout"),
        "cancel" => new OperationCanceledException("tail-read-cancelled"),
        "oom" => new OutOfMemoryException("tail-allocation-failure"),
        "unexpected" => new InvalidOperationException("unexpected-tail-failure"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static async Task<byte[]> ReadAllAsync(Stream stream)
    {
        await using (stream)
        {
            using var destination = new MemoryStream();
            await stream.CopyToAsync(destination);
            return destination.ToArray();
        }
    }

    private sealed class ThrowingCountResponses : IReadOnlyList<Task<UsenetDecodedBodyResponse>>
    {
        public int Count => throw new InvalidOperationException("response-count");

        public Task<UsenetDecodedBodyResponse> this[int index] =>
            throw new ArgumentOutOfRangeException(nameof(index));

        public IEnumerator<Task<UsenetDecodedBodyResponse>> GetEnumerator() =>
            Enumerable.Empty<Task<UsenetDecodedBodyResponse>>().GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class ProbeStream(
        byte[] bytes,
        int failAfter = -1,
        Exception? readFailure = null) : Stream
    {
        private readonly MemoryStream _inner = new(bytes, writable: false);
        public bool Disposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) =>
            Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (failAfter < 0)
                return _inner.Read(buffer);
            var remaining = failAfter - (int)_inner.Position;
            if (remaining <= 0)
                throw readFailure ?? new IOException("tail-read-failure");
            return _inner.Read(buffer[..Math.Min(buffer.Length, remaining)]);
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Read(buffer.Span));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.FromResult(Read(buffer, offset, count));

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class CacheProbeNntpClient : NntpClient
    {
        public Dictionary<string, byte[]> Segments { get; } = new(StringComparer.Ordinal);
        public List<(string Id, ProbeStream Inner)> PipelinedArticles { get; } = [];
        public TaskCompletionSource PipelinedYielded { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int BodyRequestCount { get; private set; }
        public int ArticleRequestCount { get; private set; }
        public bool GateFirstBody { get; set; }
        public Exception? HeadException { get; set; }
        public Func<string, byte[], Stream>? DecodedStreamFactory { get; set; }
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
                Stream = CreateStream(bytes, DecodedStreamFactory?.Invoke(segmentId.ToString(), bytes)),
            });
        }

        public override async IAsyncEnumerable<PipelinedArticleResult> DecodedArticlesPipelinedAsync(
            IReadOnlyList<string> segmentIds,
            int depth,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var (id, inner) in PipelinedArticles.Where(item => segmentIds.Contains(item.Id)))
            {
                await Task.Yield();
                var bytes = Segments[id];
                PipelinedYielded.TrySetResult();
                yield return new PipelinedArticleResult
                {
                    SegmentId = id,
                    Found = true,
                    Stream = new CachedYencStream(
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
                        inner),
                    ArticleHeaders = FixedArticleHeaders,
                };
            }
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
                Stream = CreateStream(bytes, DecodedStreamFactory?.Invoke(segmentId.ToString(), bytes)),
            };
        }

        private static CachedYencStream CreateStream(byte[] bytes, Stream? decodedStream = null) =>
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
                decodedStream ?? new MemoryStream(bytes, writable: false));
    }
}
