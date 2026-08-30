using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Streams;
using NzbWebDAV.Tests.Fakes;
using NzbWebDAV.Tests.TestUtils;
using UsenetSharp.Models;

namespace NzbWebDAV.Tests.Clients.Usenet;

public sealed class SegmentCacheNntpClientTests
{
    [Fact]
    public async Task CatalogHydration_DoesNotBlockConstruction_AndServesEntriesAfterLoad()
    {
        var cacheDir = Path.Join(
            Path.GetTempPath(), "nzbdav-segment-cache-" + Guid.NewGuid().ToString("N"));
        var loadStarted = new ManualResetEventSlim();
        var allowLoad = new ManualResetEventSlim();
        const string segmentId = "segment-1";
        byte[] content = "cached-content"u8.ToArray();

        try
        {
            WriteCacheEntry(cacheDir, segmentId, content);
            var inner = new FakeNntpClient(new Dictionary<string, byte[]>
            {
                [segmentId] = content,
            }, useCachedYencStreams: true);
            using var client = new SegmentCacheNntpClient(
                inner,
                cacheDir,
                maxBytes: 1024 * 1024,
                usageTracker: null,
                metricsWriter: null,
                enumerateCacheFiles: () =>
                {
                    loadStarted.Set();
                    allowLoad.Wait();
                    return Directory.EnumerateFiles(cacheDir, "*", SearchOption.AllDirectories);
                });

            Assert.True(loadStarted.Wait(TimeSpan.FromSeconds(5)));
            Assert.False(client.IsCatalogReady);

            var beforeHydration = await client.DecodedBodyAsync(segmentId, CancellationToken.None);
            beforeHydration.Stream?.Dispose();
            Assert.Equal(1, inner.BodyRequestCount);

            allowLoad.Set();
            await client.CatalogLoadTask.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(client.IsCatalogReady);

            var afterHydration = await client.DecodedBodyAsync(segmentId, CancellationToken.None);
            Assert.Equal(1, inner.BodyRequestCount);
            Assert.NotNull(afterHydration.Stream);

            await using var output = new MemoryStream();
            await afterHydration.Stream.CopyToAsync(output);
            Assert.Equal(content, output.ToArray());
        }
        finally
        {
            allowLoad.Set();
            if (Directory.Exists(cacheDir))
                Directory.Delete(cacheDir, recursive: true);
            loadStarted.Dispose();
            allowLoad.Dispose();
        }
    }

    [Fact]
    public async Task CatalogHydration_DoesNotDoubleCountEntryFinalizedDuringLoad()
    {
        var cacheDir = Path.Join(
            Path.GetTempPath(), "nzbdav-segment-cache-" + Guid.NewGuid().ToString("N"));
        var loadStarted = new ManualResetEventSlim();
        var allowLoad = new ManualResetEventSlim();
        const string segmentId = "segment-written-during-load";
        byte[] content = "new-cache-content"u8.ToArray();

        try
        {
            var inner = new FakeNntpClient(new Dictionary<string, byte[]>
            {
                [segmentId] = content,
            }, useCachedYencStreams: true);
            using var client = new SegmentCacheNntpClient(
                inner,
                cacheDir,
                maxBytes: 1024 * 1024,
                usageTracker: null,
                metricsWriter: null,
                enumerateCacheFiles: () =>
                {
                    loadStarted.Set();
                    allowLoad.Wait();
                    return Directory.EnumerateFiles(cacheDir, "*", SearchOption.AllDirectories);
                });

            Assert.True(loadStarted.Wait(TimeSpan.FromSeconds(5)));
            var response = await client.DecodedBodyAsync(segmentId, CancellationToken.None);
            Assert.NotNull(response.Stream);
            await response.Stream.CopyToAsync(Stream.Null);
            response.Stream.Dispose();
            Assert.Equal(content.Length, client.CurrentBytes);

            allowLoad.Set();
            await client.CatalogLoadTask.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(content.Length, client.CurrentBytes);
        }
        finally
        {
            allowLoad.Set();
            if (Directory.Exists(cacheDir))
                Directory.Delete(cacheDir, recursive: true);
            loadStarted.Dispose();
            allowLoad.Dispose();
        }
    }

    [Fact]
    public async Task DecodedBodyAsync_CacheHit_ThrowingCallbackReturnsCachedBody()
    {
        var cacheDir = Path.Join(
            Path.GetTempPath(), "nzbdav-segment-cache-" + Guid.NewGuid().ToString("N"));
        const string segmentId = "cached-hit";
        byte[] content = "cached-hit-bytes"u8.ToArray();

        try
        {
            WriteCacheEntry(cacheDir, segmentId, content);
            var inner = new FakeNntpClient(new Dictionary<string, byte[]>(), useCachedYencStreams: true);
            using var client = new SegmentCacheNntpClient(inner, cacheDir, maxBytes: 1024 * 1024);
            await client.CatalogLoadTask.WaitAsync(TimeSpan.FromSeconds(5));

            var recorder = new ArticleBodyCompletionRecorder(throwOnInvoke: true);
            var response = await client.DecodedBodyAsync(segmentId, recorder.Invoke, CancellationToken.None);
            Assert.NotNull(response.Stream);
            await using var output = new MemoryStream();
            await response.Stream.CopyToAsync(output);

            Assert.Equal(content, output.ToArray());
            Assert.Equal(1, recorder.Count);
            Assert.Equal(ArticleBodyResult.Retrieved, recorder.Result);
            Assert.Equal(0, inner.BodyRequestCount);
        }
        finally
        {
            if (Directory.Exists(cacheDir))
                Directory.Delete(cacheDir, recursive: true);
        }
    }

    [Fact]
    public async Task DecodedBodyAsync_ExclusiveCacheHit_ThrowingCallbackReturnsCachedBody()
    {
        var cacheDir = Path.Join(
            Path.GetTempPath(), "nzbdav-segment-cache-" + Guid.NewGuid().ToString("N"));
        const string segmentId = "exclusive-hit";
        byte[] content = "exclusive-hit-bytes"u8.ToArray();

        try
        {
            WriteCacheEntry(cacheDir, segmentId, content);
            var inner = new FakeNntpClient(new Dictionary<string, byte[]>(), useCachedYencStreams: true);
            using var client = new SegmentCacheNntpClient(inner, cacheDir, maxBytes: 1024 * 1024);
            await client.CatalogLoadTask.WaitAsync(TimeSpan.FromSeconds(5));

            var recorder = new ArticleBodyCompletionRecorder(throwOnInvoke: true);
            var exclusive = new UsenetExclusiveConnection(recorder.Invoke);
            var response = await client.DecodedBodyAsync(segmentId, exclusive, CancellationToken.None);
            Assert.NotNull(response.Stream);
            await using var output = new MemoryStream();
            await response.Stream.CopyToAsync(output);

            Assert.Equal(content, output.ToArray());
            Assert.Equal(1, recorder.Count);
            Assert.Equal(ArticleBodyResult.Retrieved, recorder.Result);
            Assert.Equal(0, inner.BodyRequestCount);
        }
        finally
        {
            if (Directory.Exists(cacheDir))
                Directory.Delete(cacheDir, recursive: true);
        }
    }

    [Theory]
    [InlineData(ArticleBodyResult.Retrieved, null)]
    [InlineData(ArticleBodyResult.Cancelled, null)]
    [InlineData(ArticleBodyResult.NotFound, null)]
    [InlineData(ArticleBodyResult.NotRetrieved, "SocketException")]
    public async Task DecodedBodyAsync_CacheMiss_ForwardsTerminalStatusOnce(
        ArticleBodyResult result, string? failureReason)
    {
        var cacheDir = Path.Join(
            Path.GetTempPath(), "nzbdav-segment-cache-" + Guid.NewGuid().ToString("N"));
        const string segmentId = "cache-miss";
        byte[] content = "provider-bytes"u8.ToArray();

        try
        {
            Directory.CreateDirectory(cacheDir);
            var inner = new ScriptedStatusNntpClient(result, failureReason, content);
            using var client = new SegmentCacheNntpClient(inner, cacheDir, maxBytes: 1024 * 1024);
            await client.CatalogLoadTask.WaitAsync(TimeSpan.FromSeconds(5));

            var recorder = new ArticleBodyCompletionRecorder();
            var response = await client.DecodedBodyAsync(segmentId, recorder.Invoke, CancellationToken.None);
            if (response.Stream != null)
                await ReadAndDisposeAsync(response.Stream);

            Assert.Equal(1, recorder.Count);
            Assert.Equal(result, recorder.Result);
            Assert.Equal(failureReason, recorder.FailureReason);
            Assert.Equal(1, inner.BodyRequestCount);
            Assert.Equal(1, inner.CompletionCount);
        }
        finally
        {
            if (Directory.Exists(cacheDir))
                Directory.Delete(cacheDir, recursive: true);
        }
    }

    private static async Task ReadAndDisposeAsync(Stream stream)
    {
        await using (stream)
            await stream.CopyToAsync(Stream.Null);
    }

    private static void WriteCacheEntry(string cacheDir, string segmentId, byte[] content)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(segmentId)));
        var directory = Path.Join(cacheDir, hash[..2]);
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Join(directory, hash), content);

        var header = new UsenetYencHeader
        {
            FileName = "fake.bin",
            FileSize = content.Length,
            LineLength = 128,
            PartNumber = 1,
            PartOffset = 0,
            PartSize = content.Length,
            TotalParts = 1,
        };
        File.WriteAllText(
            Path.Join(directory, hash) + ".h",
            JsonSerializer.Serialize(header, new JsonSerializerOptions { IncludeFields = true }));
    }

    private sealed class ScriptedStatusNntpClient(
        ArticleBodyResult result,
        string? failureReason,
        byte[] content) : NntpClient
    {
        public int BodyRequestCount { get; private set; }
        public int CompletionCount { get; private set; }

        public override Task ConnectAsync(string host, int port, bool useSsl, CancellationToken cancellationToken) =>
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
            BodyRequestCount++;
            var success = result == ArticleBodyResult.Retrieved;
            var response = new UsenetDecodedBodyResponse
            {
                SegmentId = segmentId.ToString(),
                ResponseCode = success
                    ? (int)UsenetResponseType.ArticleRetrievedBodyFollows
                    : (int)UsenetResponseType.NoArticleWithThatMessageId,
                ResponseMessage = success ? "222" : "430",
                Stream = success ? CreateStream(content) : null,
            };
            onConnectionReadyAgain?.Invoke(result, failureReason);
            CompletionCount++;
            return Task.FromResult(response);
        }

        public override Task<UsenetDecodedBodyBatch> DecodedBodiesAsync(
            IReadOnlyList<SegmentId> segmentIds,
            ArticleBodyCompletionHandler? onConnectionReadyAgain,
            CancellationToken cancellationToken) =>
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
