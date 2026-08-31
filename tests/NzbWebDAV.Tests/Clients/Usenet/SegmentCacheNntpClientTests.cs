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

    [Fact]
    public async Task ReadyHit_IncrementsHitAndBytesServedOnce()
    {
        var cacheDir = NewCacheDir();
        const string segmentId = "hit-counter";
        byte[] content = "hit-counter-bytes"u8.ToArray();
        var statistics = new SegmentCacheStatistics();

        try
        {
            WriteCacheEntry(cacheDir, segmentId, content);
            var inner = new FakeNntpClient(new Dictionary<string, byte[]>(), useCachedYencStreams: true);
            using var client = CreateClient(inner, cacheDir, statistics);
            await client.CatalogLoadTask.WaitAsync(TimeSpan.FromSeconds(5));

            var response = await client.DecodedBodyAsync(segmentId, CancellationToken.None);
            await ReadAndDisposeAsync(response.Stream!);

            var snapshot = statistics.GetSnapshot();
            Assert.Equal(1, snapshot.Hits);
            Assert.Equal(content.Length, snapshot.BytesServed);
            Assert.Equal(0, snapshot.Misses);
            Assert.Equal(0, inner.BodyRequestCount);
        }
        finally
        {
            DeleteCacheDir(cacheDir);
        }
    }

    [Fact]
    public async Task ReadyMiss_IncrementsMissOnce()
    {
        var cacheDir = NewCacheDir();
        const string segmentId = "miss-counter";
        byte[] content = "provider-miss-bytes"u8.ToArray();
        var statistics = new SegmentCacheStatistics();

        try
        {
            Directory.CreateDirectory(cacheDir);
            var inner = new FakeNntpClient(new Dictionary<string, byte[]> { [segmentId] = content }, useCachedYencStreams: true);
            using var client = CreateClient(inner, cacheDir, statistics);
            await client.CatalogLoadTask.WaitAsync(TimeSpan.FromSeconds(5));

            var response = await client.DecodedBodyAsync(segmentId, CancellationToken.None);
            await ReadAndDisposeAsync(response.Stream!);

            var snapshot = statistics.GetSnapshot();
            Assert.Equal(1, snapshot.Misses);
            Assert.Equal(0, snapshot.Hits);
            Assert.Equal(1, snapshot.WriteAttempts);
            Assert.Equal(1, snapshot.WriteCommits);
        }
        finally
        {
            DeleteCacheDir(cacheDir);
        }
    }

    [Fact]
    public async Task LookupDuringBlockedCatalog_IncrementsUnavailableNotMiss()
    {
        var cacheDir = NewCacheDir();
        var loadStarted = new ManualResetEventSlim();
        var allowLoad = new ManualResetEventSlim();
        const string segmentId = "blocked-catalog";
        byte[] content = "blocked-catalog-bytes"u8.ToArray();
        var statistics = new SegmentCacheStatistics();

        try
        {
            WriteCacheEntry(cacheDir, segmentId, content);
            var inner = new FakeNntpClient(new Dictionary<string, byte[]> { [segmentId] = content }, useCachedYencStreams: true);
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
                },
                statistics);

            Assert.True(loadStarted.Wait(TimeSpan.FromSeconds(5)));
            var response = await client.DecodedBodyAsync(segmentId, CancellationToken.None);
            await ReadAndDisposeAsync(response.Stream!);

            var snapshot = statistics.GetSnapshot();
            Assert.Equal(1, snapshot.LookupUnavailable);
            Assert.Equal(0, snapshot.Misses);
            Assert.Equal(0, snapshot.Hits);
            Assert.Equal(1, inner.BodyRequestCount);
        }
        finally
        {
            allowLoad.Set();
            DeleteCacheDir(cacheDir);
            loadStarted.Dispose();
            allowLoad.Dispose();
        }
    }

    [Fact]
    public async Task CorruptCacheFile_IncrementsReadFailureAndDropsEntry()
    {
        var cacheDir = NewCacheDir();
        const string segmentId = "corrupt-entry";
        byte[] content = "corrupt-entry-bytes"u8.ToArray();
        var statistics = new SegmentCacheStatistics();

        try
        {
            WriteCacheEntry(cacheDir, segmentId, content);
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(segmentId)));
            File.WriteAllText(Path.Join(cacheDir, hash[..2], hash) + ".h", "{not-json");
            var inner = new FakeNntpClient(new Dictionary<string, byte[]> { [segmentId] = content }, useCachedYencStreams: true);
            using var client = CreateClient(inner, cacheDir, statistics);
            await client.CatalogLoadTask.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(content.Length, client.CurrentBytes);

            var response = await client.DecodedBodyAsync(segmentId, CancellationToken.None);
            response.Stream!.Dispose();

            var snapshot = statistics.GetSnapshot();
            Assert.Equal(1, snapshot.ReadFailures);
            Assert.Equal(0, snapshot.Hits);
            Assert.Equal(0, client.CurrentBytes);
            Assert.Equal(0, snapshot.Entries);
        }
        finally
        {
            DeleteCacheDir(cacheDir);
        }
    }

    [Fact]
    public async Task PartialRead_RecordsOneAttemptAndOneSkip()
    {
        var cacheDir = NewCacheDir();
        const string segmentId = "partial-skip";
        byte[] content = Enumerable.Repeat((byte)7, 1024).ToArray();
        var statistics = new SegmentCacheStatistics();

        try
        {
            Directory.CreateDirectory(cacheDir);
            var inner = new FakeNntpClient(new Dictionary<string, byte[]> { [segmentId] = content }, useCachedYencStreams: true);
            using var client = CreateClient(inner, cacheDir, statistics);
            await client.CatalogLoadTask.WaitAsync(TimeSpan.FromSeconds(5));

            var response = await client.DecodedBodyAsync(segmentId, CancellationToken.None);
            Assert.NotNull(response.Stream);
            var buffer = new byte[16];
            Assert.Equal(16, await response.Stream.ReadAsync(buffer));
            response.Stream.Dispose();

            var snapshot = statistics.GetSnapshot();
            Assert.Equal(1, snapshot.WriteAttempts);
            Assert.Equal(1, snapshot.WriteSkipped);
            Assert.Equal(0, snapshot.WriteCommits);
            Assert.Equal(0, snapshot.WriteFailures);
        }
        finally
        {
            DeleteCacheDir(cacheDir);
        }
    }

    [SkippableFact]
    public async Task WriteOpenFailure_RecordsOneFailureWithoutFailingPlayback()
    {
        Skip.If(OperatingSystem.IsWindows(), "Unix file modes are not enforced on Windows.");
        var cacheDir = NewCacheDir();
        const string segmentId = "write-fail";
        byte[] content = "write-fail-bytes"u8.ToArray();
        var statistics = new SegmentCacheStatistics();

        try
        {
            Directory.CreateDirectory(cacheDir);
            var inner = new FakeNntpClient(new Dictionary<string, byte[]> { [segmentId] = content }, useCachedYencStreams: true);
            using var client = CreateClient(inner, cacheDir, statistics);
            await client.CatalogLoadTask.WaitAsync(TimeSpan.FromSeconds(5));
            SetUnixMode(cacheDir, UnixFileMode.UserRead | UnixFileMode.UserExecute);

            var response = await client.DecodedBodyAsync(segmentId, CancellationToken.None);
            await using var output = new MemoryStream();
            await response.Stream!.CopyToAsync(output);
            response.Stream.Dispose();

            Assert.Equal(content, output.ToArray());
            var snapshot = statistics.GetSnapshot();
            Assert.Equal(1, snapshot.WriteAttempts);
            Assert.Equal(1, snapshot.WriteFailures);
            Assert.Equal(0, snapshot.WriteCommits);
        }
        finally
        {
            SetUnixMode(cacheDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            DeleteCacheDir(cacheDir);
        }
    }

    [Fact]
    public async Task CapacityEviction_RecordsExactEntryAndByteCounts()
    {
        var cacheDir = NewCacheDir();
        byte[] first = "first-cache-entry"u8.ToArray();
        byte[] second = "second-cache-entry!!"u8.ToArray();
        var statistics = new SegmentCacheStatistics();

        try
        {
            Directory.CreateDirectory(cacheDir);
            var inner = new FakeNntpClient(
                new Dictionary<string, byte[]>
                {
                    ["first"] = first,
                    ["second"] = second,
                },
                useCachedYencStreams: true);
            using var client = CreateClient(inner, cacheDir, statistics, maxBytes: second.Length);
            await client.CatalogLoadTask.WaitAsync(TimeSpan.FromSeconds(5));

            await ReadFullyAsync(client, "first");
            Assert.Equal(first.Length, client.CurrentBytes);
            await ReadFullyAsync(client, "second");

            var snapshot = statistics.GetSnapshot();
            Assert.Equal(1, snapshot.Evictions);
            Assert.Equal(first.Length, snapshot.BytesEvicted);
            Assert.Equal(second.Length, client.CurrentBytes);
            Assert.Equal(1, snapshot.Entries);
        }
        finally
        {
            DeleteCacheDir(cacheDir);
        }
    }

    [Fact]
    public async Task SuccessfulStaleTmpDeletion_IncrementsCleanup()
    {
        var cacheDir = NewCacheDir();
        var statistics = new SegmentCacheStatistics();

        try
        {
            Directory.CreateDirectory(cacheDir);
            File.WriteAllText(Path.Join(cacheDir, "stale.tmp"), "tmp");
            var inner = new FakeNntpClient(new Dictionary<string, byte[]>(), useCachedYencStreams: true);
            using var client = CreateClient(inner, cacheDir, statistics);
            await client.CatalogLoadTask.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(1, statistics.GetSnapshot().TemporaryFilesCleaned);
            Assert.False(File.Exists(Path.Join(cacheDir, "stale.tmp")));
        }
        finally
        {
            DeleteCacheDir(cacheDir);
        }
    }

    [SkippableFact]
    public async Task FailedStaleTmpDeletion_DoesNotIncrementCleanup()
    {
        Skip.If(OperatingSystem.IsWindows(), "Unix file modes are not enforced on Windows.");
        var cacheDir = NewCacheDir();
        var lockedDir = Path.Join(cacheDir, "locked");
        var statistics = new SegmentCacheStatistics();

        try
        {
            Directory.CreateDirectory(lockedDir);
            var tmpPath = Path.Join(lockedDir, "stale.tmp");
            File.WriteAllText(tmpPath, "tmp");
            SetUnixMode(lockedDir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            var inner = new FakeNntpClient(new Dictionary<string, byte[]>(), useCachedYencStreams: true);
            using var client = new SegmentCacheNntpClient(
                inner,
                cacheDir,
                maxBytes: 1024,
                usageTracker: null,
                metricsWriter: null,
                enumerateCacheFiles: () => [tmpPath],
                statistics);
            await client.CatalogLoadTask.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(0, statistics.GetSnapshot().TemporaryFilesCleaned);
            SetUnixMode(lockedDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Assert.True(File.Exists(tmpPath));
        }
        finally
        {
            try
            {
                SetUnixMode(lockedDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            catch
            {
                // ignore
            }

            DeleteCacheDir(cacheDir);
        }
    }

    [Fact]
    public async Task OrdinaryBatch_IncrementsBypassOnceAndForwards()
    {
        await AssertBatchBypassAsync(exclusive: false);
    }

    [Fact]
    public async Task ExclusiveBatch_IncrementsBypassOnceAndForwards()
    {
        await AssertBatchBypassAsync(exclusive: true);
    }

    [Fact]
    public async Task BatchSetupFailure_PropagatesUnchanged()
    {
        var cacheDir = NewCacheDir();
        var statistics = new SegmentCacheStatistics();
        var inner = new ThrowingBatchNntpClient();

        try
        {
            Directory.CreateDirectory(cacheDir);
            using var client = CreateClient(inner, cacheDir, statistics);
            await client.CatalogLoadTask.WaitAsync(TimeSpan.FromSeconds(5));

            var error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => client.DecodedBodiesAsync(["a", "b"], onConnectionReadyAgain: null, CancellationToken.None));
            Assert.Equal("batch-setup", error.Message);
            Assert.Equal(1, statistics.GetSnapshot().BatchBypassRequests);
            Assert.Equal(2, statistics.GetSnapshot().BatchBypassArticles);
        }
        finally
        {
            DeleteCacheDir(cacheDir);
        }
    }

    private static async Task AssertBatchBypassAsync(bool exclusive)
    {
        var cacheDir = NewCacheDir();
        var statistics = new SegmentCacheStatistics();
        var segments = new Dictionary<string, byte[]>
        {
            ["a"] = "aaa"u8.ToArray(),
            ["b"] = "bbb"u8.ToArray(),
            ["c"] = "ccc"u8.ToArray(),
        };

        try
        {
            Directory.CreateDirectory(cacheDir);
            var inner = new FakeNntpClient(segments, useCachedYencStreams: true);
            using var client = CreateClient(inner, cacheDir, statistics);
            await client.CatalogLoadTask.WaitAsync(TimeSpan.FromSeconds(5));

            var ids = new SegmentId[] { "a", "b", "c" };
            var batch = exclusive
                ? await client.DecodedBodiesAsync(ids, new UsenetExclusiveConnection(null), CancellationToken.None)
                : await client.DecodedBodiesAsync(ids, onConnectionReadyAgain: null, CancellationToken.None);

            Assert.Equal(3, batch.Responses.Count);
            Assert.Equal(1, inner.BatchRequestCount);
            Assert.Equal(3, inner.BodyRequestCount);
            var snapshot = statistics.GetSnapshot();
            Assert.Equal(1, snapshot.BatchBypassRequests);
            Assert.Equal(3, snapshot.BatchBypassArticles);
            Assert.Equal(0, snapshot.Hits);
        }
        finally
        {
            DeleteCacheDir(cacheDir);
        }
    }

    private static SegmentCacheNntpClient CreateClient(
        INntpClient inner,
        string cacheDir,
        SegmentCacheStatistics statistics,
        long maxBytes = 1024 * 1024) =>
        new(inner, cacheDir, maxBytes, usageTracker: null, metricsWriter: null, enumerateCacheFiles: null, statistics);

    private static string NewCacheDir() =>
        Path.Join(Path.GetTempPath(), "nzbdav-segment-cache-" + Guid.NewGuid().ToString("N"));

    private static void DeleteCacheDir(string cacheDir)
    {
        if (Directory.Exists(cacheDir))
            Directory.Delete(cacheDir, recursive: true);
    }

    private static void SetUnixMode(string path, UnixFileMode mode)
    {
        if (OperatingSystem.IsWindows()) return;
        File.SetUnixFileMode(path, mode);
    }

    private static async Task ReadFullyAsync(SegmentCacheNntpClient client, string segmentId)
    {
        var response = await client.DecodedBodyAsync(segmentId, CancellationToken.None);
        await ReadAndDisposeAsync(response.Stream!);
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

    private sealed class ThrowingBatchNntpClient : NntpClient
    {
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
            throw new NotSupportedException();

        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId,
            ArticleBodyCompletionHandler? onConnectionReadyAgain,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetDecodedBodyBatch> DecodedBodiesAsync(
            IReadOnlyList<SegmentId> segmentIds,
            ArticleBodyCompletionHandler? onConnectionReadyAgain,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("batch-setup");

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
    }
}
