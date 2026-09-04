using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Contexts;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Models;
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
        using var loadStarted = new ManualResetEventSlim();
        using var allowLoad = new ManualResetEventSlim();
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
            Assert.Equal(0, client.CurrentBytes);
            Assert.Equal(1, statistics.GetSnapshot().ReadFailures);

            var response = await client.DecodedBodyAsync(segmentId, CancellationToken.None);
            await using (response.Stream)
                await response.Stream!.CopyToAsync(Stream.Null);

            var afterCommit = statistics.GetSnapshot();
            Assert.Equal(1, afterCommit.ReadFailures);
            Assert.Equal(0, afterCommit.Hits);
            Assert.Equal(1, afterCommit.WriteCommits);
            Assert.Equal(0, afterCommit.WriteFailures);
            Assert.Equal(1, afterCommit.Entries);
            Assert.Equal(1, inner.BodyRequestCount);

            var reread = await client.DecodedBodyAsync(segmentId, CancellationToken.None);
            await using (reread.Stream)
                await reread.Stream!.CopyToAsync(Stream.Null);

            var snapshot = statistics.GetSnapshot();
            Assert.Equal(1, snapshot.Hits);
            Assert.Equal(1, snapshot.WriteCommits);
            Assert.Equal(1, inner.BodyRequestCount);
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

    [Fact]
    public async Task WriteBehind_BlockedPersistenceDoesNotDelayPlaybackAndPublishesAfterDrain()
    {
        var cacheDir = NewCacheDir();
        const string segmentId = "write-behind-blocked";
        byte[] content = Enumerable.Repeat((byte)0x5A, 128 * 1024).ToArray();
        var statistics = new SegmentCacheStatistics();
        var persistStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePersist = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            Directory.CreateDirectory(cacheDir);
            var inner = new FakeNntpClient(
                new Dictionary<string, byte[]> { [segmentId] = content },
                useCachedYencStreams: true);
            using var client = new SegmentCacheNntpClient(
                inner,
                cacheDir,
                maxBytes: 1024 * 1024,
                usageTracker: null,
                metricsWriter: null,
                enumerateCacheFiles: null,
                statistics,
                writeBehindBytes: 1024 * 1024,
                beforeWriteBehindPersist: async cancellationToken =>
                {
                    persistStarted.TrySetResult();
                    await releasePersist.Task.WaitAsync(cancellationToken);
                });
            await client.CatalogLoadTask.WaitAsync(TimeSpan.FromSeconds(5));

            var response = await client.DecodedBodyAsync(segmentId, CancellationToken.None);
            await using var output = new MemoryStream();
            await response.Stream!.CopyToAsync(output);
            await response.Stream.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Equal(content, output.ToArray());
            await persistStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Null(await client.TryGetLocalDecodedBodyAsync(segmentId, CancellationToken.None));
            var blocked = statistics.GetSnapshot();
            var blockedWriter = Assert.IsType<SegmentCacheWriteBehindSnapshot>(
                statistics.GetWriterSnapshot());
            Assert.Equal(0, blocked.WriteCommits);
            Assert.Equal(1024 * 1024, blockedWriter.BudgetBytes);
            Assert.Equal(1, blockedWriter.ActiveJobs);
            Assert.True(blocked.QueuedWriteBytes >= content.Length);

            releasePersist.TrySetResult();
            client.Retire();
            await client.DrainWriteBehindForTestsAsync().WaitAsync(TimeSpan.FromSeconds(5));

            var cached = await client.TryGetLocalDecodedBodyAsync(segmentId, CancellationToken.None);
            Assert.NotNull(cached);
            Assert.NotNull(cached.Stream);
            await using var cachedOutput = new MemoryStream();
            await cached.Stream.CopyToAsync(cachedOutput);
            Assert.Equal(content, cachedOutput.ToArray());
            var drained = statistics.GetSnapshot();
            var drainedWriter = Assert.IsType<SegmentCacheWriteBehindSnapshot>(
                statistics.GetWriterSnapshot());
            Assert.Equal(1, drained.WriteCommits);
            Assert.Equal(0, drained.QueuedWriteBytes);
            Assert.Equal(0, drainedWriter.QueuedJobs);
            Assert.Equal(0, drainedWriter.ActiveJobs);
        }
        finally
        {
            releasePersist.TrySetResult();
            DeleteCacheDir(cacheDir);
        }
    }

    [Fact]
    public async Task WriteBehind_ByteBudgetPressureSkipsCacheWithoutDelayingPlayback()
    {
        var cacheDir = NewCacheDir();
        const string segmentId = "write-behind-pressure";
        byte[] content = Enumerable.Repeat((byte)0x3C, 64 * 1024).ToArray();
        var statistics = new SegmentCacheStatistics();

        try
        {
            Directory.CreateDirectory(cacheDir);
            var inner = new FakeNntpClient(
                new Dictionary<string, byte[]> { [segmentId] = content },
                useCachedYencStreams: true);
            using var client = new SegmentCacheNntpClient(
                inner,
                cacheDir,
                maxBytes: 1024 * 1024,
                usageTracker: null,
                metricsWriter: null,
                enumerateCacheFiles: null,
                statistics,
                writeBehindBytes: 1);
            await client.CatalogLoadTask.WaitAsync(TimeSpan.FromSeconds(5));

            var response = await client.DecodedBodyAsync(segmentId, CancellationToken.None);
            await using var output = new MemoryStream();
            await response.Stream!.CopyToAsync(output);
            await response.Stream.DisposeAsync();

            Assert.Equal(content, output.ToArray());
            var snapshot = statistics.GetSnapshot();
            Assert.Equal(1, snapshot.WriteAttempts);
            Assert.Equal(1, snapshot.WriteSkipped);
            Assert.Equal(
                1,
                Assert.IsType<SegmentCacheWriteBehindSnapshot>(
                    statistics.GetWriterSnapshot()).CapacitySkips);
            Assert.Equal(0, snapshot.QueuedWriteBytes);
        }
        finally
        {
            DeleteCacheDir(cacheDir);
        }
    }

    [Fact]
    public async Task WriteBehind_PartialReadReleasesReservation()
    {
        var cacheDir = NewCacheDir();
        const string segmentId = "write-behind-partial";
        byte[] content = Enumerable.Repeat((byte)0x7E, 64 * 1024).ToArray();
        var statistics = new SegmentCacheStatistics();

        try
        {
            Directory.CreateDirectory(cacheDir);
            var inner = new FakeNntpClient(
                new Dictionary<string, byte[]> { [segmentId] = content },
                useCachedYencStreams: true);
            using var client = new SegmentCacheNntpClient(
                inner,
                cacheDir,
                maxBytes: 1024 * 1024,
                usageTracker: null,
                metricsWriter: null,
                enumerateCacheFiles: null,
                statistics,
                writeBehindBytes: 1024 * 1024);
            await client.CatalogLoadTask.WaitAsync(TimeSpan.FromSeconds(5));

            var response = await client.DecodedBodyAsync(segmentId, CancellationToken.None);
            Assert.Equal(16, await response.Stream!.ReadAsync(new byte[16]));
            await response.Stream.DisposeAsync();

            var snapshot = statistics.GetSnapshot();
            Assert.Equal(1, snapshot.WriteAttempts);
            Assert.Equal(1, snapshot.WriteSkipped);
            Assert.Equal(0, snapshot.QueuedWriteBytes);
            var writer = Assert.IsType<SegmentCacheWriteBehindSnapshot>(
                statistics.GetWriterSnapshot());
            Assert.Equal(0, writer.QueuedJobs);
            Assert.Equal(0, writer.ActiveJobs);
        }
        finally
        {
            DeleteCacheDir(cacheDir);
        }
    }

    [Fact]
    public async Task WriteBehind_PersistenceFailureDoesNotFailPlaybackAndReleasesReservation()
    {
        var cacheDir = NewCacheDir();
        const string segmentId = "write-behind-failure";
        byte[] content = Enumerable.Repeat((byte)0x42, 64 * 1024).ToArray();
        var statistics = new SegmentCacheStatistics();

        try
        {
            Directory.CreateDirectory(cacheDir);
            var inner = new FakeNntpClient(
                new Dictionary<string, byte[]> { [segmentId] = content },
                useCachedYencStreams: true);
            using var client = new SegmentCacheNntpClient(
                inner,
                cacheDir,
                maxBytes: 1024 * 1024,
                usageTracker: null,
                metricsWriter: null,
                enumerateCacheFiles: null,
                statistics,
                writeBehindBytes: 1024 * 1024,
                beforeWriteBehindPersist: _ => throw new IOException("simulated storage failure"));
            await client.CatalogLoadTask.WaitAsync(TimeSpan.FromSeconds(5));

            var response = await client.DecodedBodyAsync(segmentId, CancellationToken.None);
            await using var output = new MemoryStream();
            await response.Stream!.CopyToAsync(output);
            await response.Stream.DisposeAsync();
            client.Retire();
            await client.DrainWriteBehindForTestsAsync().WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(content, output.ToArray());
            Assert.Null(await client.TryGetLocalDecodedBodyAsync(segmentId, CancellationToken.None));
            var snapshot = statistics.GetSnapshot();
            Assert.Equal(1, snapshot.WriteFailures);
            Assert.Equal(0, snapshot.QueuedWriteBytes);
            var writer = Assert.IsType<SegmentCacheWriteBehindSnapshot>(
                statistics.GetWriterSnapshot());
            Assert.Equal(0, writer.QueuedJobs);
            Assert.Equal(0, writer.ActiveJobs);
        }
        finally
        {
            DeleteCacheDir(cacheDir);
        }
    }

    [Fact]
    public async Task WriteBehind_OverlongBodyIsDrainedButNotPublished()
    {
        var cacheDir = NewCacheDir();
        const string segmentId = "write-behind-overlong";
        byte[] content = Enumerable.Repeat((byte)0x24, 64 * 1024).ToArray();
        var declaredRange = new LongRange(0, content.Length / 2);
        var statistics = new SegmentCacheStatistics();

        try
        {
            Directory.CreateDirectory(cacheDir);
            var inner = new FakeNntpClient(
                new Dictionary<string, byte[]> { [segmentId] = content },
                useCachedYencStreams: true,
                segmentRanges: new Dictionary<string, LongRange> { [segmentId] = declaredRange });
            using var client = new SegmentCacheNntpClient(
                inner,
                cacheDir,
                maxBytes: 1024 * 1024,
                usageTracker: null,
                metricsWriter: null,
                enumerateCacheFiles: null,
                statistics,
                writeBehindBytes: 1024 * 1024);
            await client.CatalogLoadTask.WaitAsync(TimeSpan.FromSeconds(5));

            var response = await client.DecodedBodyAsync(segmentId, CancellationToken.None);
            await using var output = new MemoryStream();
            await response.Stream!.CopyToAsync(output);
            await response.Stream.DisposeAsync();

            Assert.Equal(content, output.ToArray());
            Assert.Null(await client.TryGetLocalDecodedBodyAsync(segmentId, CancellationToken.None));
            var snapshot = statistics.GetSnapshot();
            Assert.Equal(1, snapshot.WriteSkipped);
            Assert.Equal(0, snapshot.WriteCommits);
            Assert.Equal(0, snapshot.QueuedWriteBytes);
        }
        finally
        {
            DeleteCacheDir(cacheDir);
        }
    }

    [Fact]
    public async Task WriteBehind_SourceValidationFailureIsNotPublishedAndReleasesReservation()
    {
        var cacheDir = NewCacheDir();
        const string segmentId = "write-behind-source-failure";
        byte[] content = Enumerable.Repeat((byte)0x66, 64 * 1024).ToArray();
        var statistics = new SegmentCacheStatistics();

        try
        {
            Directory.CreateDirectory(cacheDir);
            var inner = new FakeNntpClient(
                new Dictionary<string, byte[]> { [segmentId] = content },
                useCachedYencStreams: true,
                decodedStreamFactory: (_, bytes) => new ThrowOnDisposeMemoryStream(bytes));
            using var client = new SegmentCacheNntpClient(
                inner,
                cacheDir,
                maxBytes: 1024 * 1024,
                usageTracker: null,
                metricsWriter: null,
                enumerateCacheFiles: null,
                statistics,
                writeBehindBytes: 1024 * 1024);
            await client.CatalogLoadTask.WaitAsync(TimeSpan.FromSeconds(5));

            var response = await client.DecodedBodyAsync(segmentId, CancellationToken.None);
            await response.Stream!.CopyToAsync(Stream.Null);
            await Assert.ThrowsAsync<IOException>(() => response.Stream.DisposeAsync().AsTask());

            Assert.Null(await client.TryGetLocalDecodedBodyAsync(segmentId, CancellationToken.None));
            var snapshot = statistics.GetSnapshot();
            Assert.Equal(1, snapshot.WriteSkipped);
            Assert.Equal(0, snapshot.WriteCommits);
            Assert.Equal(0, snapshot.QueuedWriteBytes);
            var writer = Assert.IsType<SegmentCacheWriteBehindSnapshot>(
                statistics.GetWriterSnapshot());
            Assert.Equal(0, writer.QueuedJobs);
            Assert.Equal(0, writer.ActiveJobs);
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
            Skip.IfNot(
                DirectoryWriteEnforced(cacheDir),
                "Test is running as root; file modes are not enforced.");

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
            var tmpPath = Path.Join(cacheDir, "stale.tmp");
            File.WriteAllText(tmpPath, "tmp");
            File.SetLastWriteTimeUtc(tmpPath, DateTime.UtcNow - SegmentCacheNntpClient.TemporaryFileGracePeriod - TimeSpan.FromMinutes(1));
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
            File.SetLastWriteTimeUtc(tmpPath, DateTime.UtcNow - SegmentCacheNntpClient.TemporaryFileGracePeriod - TimeSpan.FromMinutes(1));
            SetUnixMode(lockedDir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            Skip.IfNot(
                FileDeleteEnforced(tmpPath),
                "Test is running as root; file modes are not enforced.");
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
            catch (IOException)
            {
                // Best-effort restore before recursive delete.
            }
            catch (UnauthorizedAccessException)
            {
                // Best-effort restore before recursive delete.
            }

            DeleteCacheDir(cacheDir);
        }
    }

    [Fact]
    public async Task LiveTempDuringCatalogScan_IsNotDeleted()
    {
        var cacheDir = NewCacheDir();
        var statistics = new SegmentCacheStatistics();

        try
        {
            Directory.CreateDirectory(cacheDir);
            var tmpPath = Path.Join(cacheDir, "live.tmp");
            File.WriteAllText(tmpPath, "tmp");
            var inner = new FakeNntpClient(new Dictionary<string, byte[]>(), useCachedYencStreams: true);
            using var client = CreateClient(inner, cacheDir, statistics);
            await client.CatalogLoadTask.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(0, statistics.GetSnapshot().TemporaryFilesCleaned);
            Assert.True(File.Exists(tmpPath));
        }
        finally
        {
            DeleteCacheDir(cacheDir);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OrdinaryAndExclusiveBatch_AllMiss_RequestsOneInnerBatchWithoutBypass(bool exclusive)
    {
        await AssertAllMissOverlayAsync(exclusive);
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
            Assert.Equal(0, statistics.GetSnapshot().BatchBypassRequests);
            Assert.Equal(0, statistics.GetSnapshot().BatchBypassArticles);
        }
        finally
        {
            DeleteCacheDir(cacheDir);
        }
    }

    private static async Task AssertAllMissOverlayAsync(bool exclusive)
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
            var recorder = new ArticleBodyCompletionRecorder();
            var batch = exclusive
                ? await client.DecodedBodiesAsync(ids, new UsenetExclusiveConnection(recorder.Invoke), CancellationToken.None)
                : await client.DecodedBodiesAsync(ids, recorder.Invoke, CancellationToken.None);

            Assert.Equal(3, batch.Responses.Count);
            Assert.Equal(1, inner.BatchRequestCount);
            Assert.Equal(3, inner.BodyRequestCount);
            await batch.DrainAsync();
            var snapshot = statistics.GetSnapshot();
            Assert.Equal(0, snapshot.BatchBypassRequests);
            Assert.Equal(0, snapshot.BatchBypassArticles);
            Assert.Equal(0, snapshot.Hits);
            Assert.Equal(3, snapshot.Misses);
            Assert.Equal(3, snapshot.WriteCommits);
            Assert.Equal(1, recorder.Count);
            Assert.Equal(ArticleBodyResult.Retrieved, recorder.Result);

            var warm = exclusive
                ? await client.DecodedBodiesAsync(ids, new UsenetExclusiveConnection(null), CancellationToken.None)
                : await client.DecodedBodiesAsync(ids, onConnectionReadyAgain: null, CancellationToken.None);
            await warm.DrainAsync();
            Assert.Equal(1, inner.BatchRequestCount);
            Assert.Equal(3, statistics.GetSnapshot().Hits);
        }
        finally
        {
            DeleteCacheDir(cacheDir);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DecodedBodiesAsync_AllHit_RequestsNoInnerBatch_AndCompletesOnce(bool exclusive)
    {
        var cacheDir = NewCacheDir();
        var statistics = new SegmentCacheStatistics();
        const string a = "hit-a";
        const string b = "hit-b";
        byte[] bytesA = "aaaa"u8.ToArray();
        byte[] bytesB = "bbbb"u8.ToArray();

        try
        {
            WriteCacheEntry(cacheDir, a, bytesA);
            WriteCacheEntry(cacheDir, b, bytesB);
            var inner = new FakeNntpClient(new Dictionary<string, byte[]>(), useCachedYencStreams: true);
            using var client = CreateClient(inner, cacheDir, statistics);
            await client.CatalogLoadTask.WaitAsync(TimeSpan.FromSeconds(5));
            var recorder = new ArticleBodyCompletionRecorder();
            var ids = new SegmentId[] { a, b };
            var batch = exclusive
                ? await client.DecodedBodiesAsync(ids, new UsenetExclusiveConnection(recorder.Invoke), CancellationToken.None)
                : await client.DecodedBodiesAsync(ids, recorder.Invoke, CancellationToken.None);

            Assert.Equal(0, inner.BatchRequestCount);
            Assert.Equal(0, inner.BodyRequestCount);
            await batch.DrainAsync();
            var snapshot = statistics.GetSnapshot();
            Assert.Equal(2, snapshot.Hits);
            Assert.Equal(0, snapshot.Misses);
            Assert.Equal(0, snapshot.BatchBypassRequests);
            Assert.Equal(1, recorder.Count);
            Assert.Equal(ArticleBodyResult.Retrieved, recorder.Result);
        }
        finally
        {
            DeleteCacheDir(cacheDir);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DecodedBodiesAsync_MixedHitMissHit_RequestsOnlyMisses(bool exclusive)
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
            WriteCacheEntry(cacheDir, "a", segments["a"]);
            WriteCacheEntry(cacheDir, "c", segments["c"]);
            var inner = new FakeNntpClient(segments, useCachedYencStreams: true);
            using var client = CreateClient(inner, cacheDir, statistics);
            await client.CatalogLoadTask.WaitAsync(TimeSpan.FromSeconds(5));
            var ids = new SegmentId[] { "a", "b", "c" };
            var batch = exclusive
                ? await client.DecodedBodiesAsync(ids, new UsenetExclusiveConnection(null), CancellationToken.None)
                : await client.DecodedBodiesAsync(ids, onConnectionReadyAgain: null, CancellationToken.None);

            Assert.Equal(1, inner.BatchRequestCount);
            Assert.Equal(["b"], inner.RequestedSegmentIds.OrderBy(x => x).ToArray());
            await batch.DrainAsync();
            var snapshot = statistics.GetSnapshot();
            Assert.Equal(2, snapshot.Hits);
            Assert.Equal(1, snapshot.Misses);
            Assert.Equal(1, snapshot.WriteCommits);
        }
        finally
        {
            DeleteCacheDir(cacheDir);
        }
    }

    [Fact]
    public async Task DecodedBodiesAsync_AttributionContext_BypassesLookupAndPopulation()
    {
        var cacheDir = NewCacheDir();
        var statistics = new SegmentCacheStatistics();
        WriteCacheEntry(cacheDir, "a", "cached"u8.ToArray());
        var inner = new FakeNntpClient(new Dictionary<string, byte[]> { ["a"] = "remote"u8.ToArray() }, useCachedYencStreams: true);
        try
        {
            using var client = CreateClient(inner, cacheDir, statistics);
            await client.CatalogLoadTask.WaitAsync(TimeSpan.FromSeconds(5));
            MultiProviderNntpClient.AttributionContext.Value = new MultiProviderNntpClient.ResponderAttribution();
            try
            {
                var batch = await client.DecodedBodiesAsync(["a"], onConnectionReadyAgain: null, CancellationToken.None);
                await batch.DrainAsync();
            }
            finally
            {
                MultiProviderNntpClient.AttributionContext.Value = null;
            }

            Assert.Equal(1, inner.BatchRequestCount);
            Assert.Equal(1, statistics.GetSnapshot().BatchBypassRequests);
            Assert.Equal(0, statistics.GetSnapshot().Hits);
        }
        finally
        {
            DeleteCacheDir(cacheDir);
        }
    }

    [Fact]
    public async Task DecodedBodiesAsync_FetchAttributionContext_DoesNotBypassCache()
    {
        var cacheDir = NewCacheDir();
        var statistics = new SegmentCacheStatistics();
        WriteCacheEntry(cacheDir, "a", "cached"u8.ToArray());
        var inner = new FakeNntpClient(new Dictionary<string, byte[]>(), useCachedYencStreams: true);
        try
        {
            using var client = CreateClient(inner, cacheDir, statistics);
            await client.CatalogLoadTask.WaitAsync(TimeSpan.FromSeconds(5));
            using (FetchAttributionContext.Begin("movie.bin"))
            {
                var batch = await client.DecodedBodiesAsync(["a"], onConnectionReadyAgain: null, CancellationToken.None);
                await batch.DrainAsync();
            }

            Assert.Equal(0, inner.BatchRequestCount);
            Assert.Equal(1, statistics.GetSnapshot().Hits);
            Assert.Equal(0, statistics.GetSnapshot().BatchBypassRequests);
        }
        finally
        {
            DeleteCacheDir(cacheDir);
        }
    }

    [Fact]
    public async Task DecodedBodiesAsync_ResponseIdMismatch_DoesNotCreateCacheEntry()
    {
        var cacheDir = NewCacheDir();
        var statistics = new SegmentCacheStatistics();
        var inner = new FakeNntpClient(new Dictionary<string, byte[]> { ["a"] = "aaa"u8.ToArray() }, useCachedYencStreams: true)
        {
            ForcedResponseSegmentId = "other",
        };
        try
        {
            using var client = CreateClient(inner, cacheDir, statistics);
            await client.CatalogLoadTask.WaitAsync(TimeSpan.FromSeconds(5));
            var batch = await client.DecodedBodiesAsync(["a"], onConnectionReadyAgain: null, CancellationToken.None);
            await batch.DrainAsync();
            Assert.Equal(0, statistics.GetSnapshot().WriteCommits);
        }
        finally
        {
            DeleteCacheDir(cacheDir);
        }
    }

    [Fact]
    public async Task DecodedBodiesAsync_CompleteBodies_SurviveRestartHydration()
    {
        var cacheDir = NewCacheDir();
        var segments = new Dictionary<string, byte[]>
        {
            ["a"] = "aaa"u8.ToArray(),
            ["b"] = "bbb"u8.ToArray(),
        };

        try
        {
            Directory.CreateDirectory(cacheDir);
            var inner = new FakeNntpClient(segments, useCachedYencStreams: true);
            using (var client = CreateClient(inner, cacheDir, new SegmentCacheStatistics()))
            {
                await client.CatalogLoadTask.WaitAsync(TimeSpan.FromSeconds(5));
                var batch = await client.DecodedBodiesAsync(["a", "b"], onConnectionReadyAgain: null, CancellationToken.None);
                await batch.DrainAsync();
            }

            var restartInner = new FakeNntpClient(new Dictionary<string, byte[]>(), useCachedYencStreams: true);
            var statistics = new SegmentCacheStatistics();
            using var restarted = CreateClient(restartInner, cacheDir, statistics);
            await restarted.CatalogLoadTask.WaitAsync(TimeSpan.FromSeconds(5));
            var warm = await restarted.DecodedBodiesAsync(["a", "b"], onConnectionReadyAgain: null, CancellationToken.None);
            await warm.DrainAsync();
            Assert.Equal(0, restartInner.BatchRequestCount);
            Assert.Equal(2, statistics.GetSnapshot().Hits);
        }
        finally
        {
            DeleteCacheDir(cacheDir);
        }
    }

    [Fact]
    public async Task DecodedBodiesAsync_ConcurrentSameId_CommitsOneValidEntryWithoutDeadlock()
    {
        var cacheDir = NewCacheDir();
        var statistics = new SegmentCacheStatistics();
        var segments = new Dictionary<string, byte[]> { ["a"] = "aaaa"u8.ToArray() };

        try
        {
            Directory.CreateDirectory(cacheDir);
            var inner = new FakeNntpClient(segments, useCachedYencStreams: true);
            using var client = CreateClient(inner, cacheDir, statistics);
            await client.CatalogLoadTask.WaitAsync(TimeSpan.FromSeconds(5));
            var firstTask = client.DecodedBodiesAsync(["a"], onConnectionReadyAgain: null, CancellationToken.None);
            var secondTask = client.DecodedBodiesAsync(["a"], onConnectionReadyAgain: null, CancellationToken.None);
            var first = await firstTask;
            var second = await secondTask;
            await Task.WhenAll(first.DrainAsync(), second.DrainAsync());
            Assert.Equal(1, statistics.GetSnapshot().WriteCommits);
            Assert.True(inner.BatchRequestCount >= 1);
        }
        finally
        {
            DeleteCacheDir(cacheDir);
        }
    }

    [Fact]
    public async Task TruncatedBlobAfterHydration_DropsEntryAndRefetchesOnce()
    {
        var cacheDir = NewCacheDir();
        const string segmentId = "truncated-blob";
        byte[] content = "cached-then-truncated"u8.ToArray();
        var statistics = new SegmentCacheStatistics();

        try
        {
            Directory.CreateDirectory(cacheDir);
            WriteCacheEntry(cacheDir, segmentId, content);
            var inner = new FakeNntpClient(
                new Dictionary<string, byte[]> { [segmentId] = content },
                useCachedYencStreams: true);
            using var client = CreateClient(inner, cacheDir, statistics);
            await client.CatalogLoadTask.WaitAsync(TimeSpan.FromSeconds(5));

            var blobPath = CacheBlobPath(cacheDir, segmentId);
            using (var stream = new FileStream(blobPath, FileMode.Open, FileAccess.Write, FileShare.None))
                stream.SetLength(Math.Max(1, content.Length / 2));

            await ReadFullyAsync(client, segmentId);
            var afterRepair = statistics.GetSnapshot();
            Assert.Equal(1, afterRepair.ReadFailures);
            Assert.Equal(1, inner.BodyRequestCount);

            await ReadFullyAsync(client, segmentId);
            var snapshot = statistics.GetSnapshot();
            Assert.Equal(1, inner.BodyRequestCount);
            Assert.Equal(1, snapshot.ReadFailures);
            Assert.Equal(1, snapshot.Hits);
            Assert.Equal(content.Length, snapshot.BytesServed);
            Assert.Equal(content.Length, client.CurrentBytes);
        }
        finally
        {
            DeleteCacheDir(cacheDir);
        }
    }

    [Fact]
    public async Task DegradedCatalog_ReindexesExistingPairOnCommit()
    {
        var cacheDir = NewCacheDir();
        const string segmentId = "preexisting-pair";
        byte[] content = "already-on-disk"u8.ToArray();
        var statistics = new SegmentCacheStatistics();

        try
        {
            Directory.CreateDirectory(cacheDir);
            WriteCacheEntry(cacheDir, segmentId, content);
            var inner = new FakeNntpClient(
                new Dictionary<string, byte[]> { [segmentId] = content },
                useCachedYencStreams: true);
            using var client = new SegmentCacheNntpClient(
                inner,
                cacheDir,
                maxBytes: 1024 * 1024,
                usageTracker: null,
                metricsWriter: null,
                enumerateCacheFiles: () => throw new IOException("catalog scan failed"),
                statistics);
            await client.CatalogLoadTask.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(client.IsCatalogReady);
            Assert.Equal(0, client.CurrentBytes);

            await ReadFullyAsync(client, segmentId);
            Assert.Equal(1, inner.BodyRequestCount);
            Assert.Equal(content.Length, client.CurrentBytes);

            await ReadFullyAsync(client, segmentId);
            var snapshot = statistics.GetSnapshot();
            Assert.Equal(1, inner.BodyRequestCount);
            Assert.Equal(1, snapshot.Hits);
            Assert.Equal(1, snapshot.Entries);
            Assert.Equal(content.Length, snapshot.CurrentBytes);
            Assert.Equal(1, snapshot.WriteSkipped);
        }
        finally
        {
            DeleteCacheDir(cacheDir);
        }
    }

    [Fact]
    public async Task CapacityEviction_FailedBodyDelete_KeepsIndexAndDoesNotCountEviction()
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
            using var client = new SegmentCacheNntpClient(
                inner,
                cacheDir,
                maxBytes: second.Length,
                usageTracker: null,
                metricsWriter: null,
                enumerateCacheFiles: null,
                statistics,
                tryDelete: path =>
                {
                    var name = Path.GetFileName(path);
                    return name is { Length: 64 } && name.IndexOf('.') < 0
                        ? SegmentCacheDeleteResult.Failed
                        : null;
                });
            await client.CatalogLoadTask.WaitAsync(TimeSpan.FromSeconds(5));

            await ReadFullyAsync(client, "first");
            await ReadFullyAsync(client, "second");

            var snapshot = statistics.GetSnapshot();
            Assert.Equal(0, snapshot.Evictions);
            Assert.Equal(0, snapshot.BytesEvicted);
            Assert.Equal(first.Length + second.Length, client.CurrentBytes);
            Assert.Equal(2, snapshot.Entries);
            Assert.True(File.Exists(CacheBlobPath(cacheDir, "first")));
        }
        finally
        {
            DeleteCacheDir(cacheDir);
        }
    }

    [Fact]
    public async Task CatalogReadyGauges_MatchIndexAfterCommitDuringLoad()
    {
        var cacheDir = NewCacheDir();
        var loadStarted = new ManualResetEventSlim();
        var allowLoad = new ManualResetEventSlim();
        const string segmentId = "written-during-catalog";
        byte[] content = "gauge-race-content"u8.ToArray();
        var statistics = new SegmentCacheStatistics();

        try
        {
            var inner = new FakeNntpClient(
                new Dictionary<string, byte[]> { [segmentId] = content },
                useCachedYencStreams: true);
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
            await ReadFullyAsync(client, segmentId);
            Assert.Equal(content.Length, client.CurrentBytes);

            allowLoad.Set();
            await client.CatalogLoadTask.WaitAsync(TimeSpan.FromSeconds(5));

            var snapshot = statistics.GetSnapshot();
            Assert.True(snapshot.CatalogReady);
            Assert.Equal(1, snapshot.Entries);
            Assert.Equal(content.Length, snapshot.CurrentBytes);
            Assert.Equal(client.CurrentBytes, snapshot.CurrentBytes);
        }
        finally
        {
            allowLoad.Set();
            DeleteCacheDir(cacheDir);
            loadStarted.Dispose();
            allowLoad.Dispose();
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

    private static bool DirectoryWriteEnforced(string directory)
    {
        var probe = Path.Join(directory, $".probe-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static bool FileDeleteEnforced(string path)
    {
        try
        {
            File.Delete(path);
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
        catch (IOException)
        {
            return true;
        }
    }

    private static async Task ReadFullyAsync(SegmentCacheNntpClient client, string segmentId)
    {
        var response = await client.DecodedBodyAsync(segmentId, CancellationToken.None);
        await ReadAndDisposeAsync(response.Stream!);
    }

    [Fact]
    public void PurgeDirectory_RemovesCacheArtifacts_AndLeavesUnrelatedFiles()
    {
        var cacheDir = Path.Join(
            Path.GetTempPath(), "nzbdav-segment-cache-" + Guid.NewGuid().ToString("N"));
        try
        {
            WriteCacheEntry(cacheDir, "segment-1", "one"u8.ToArray());
            WriteCacheEntry(cacheDir, "segment-2", "two"u8.ToArray());
            var blob = CacheBlobPath(cacheDir, "segment-1");
            var unique = Guid.NewGuid().ToString("N");
            File.WriteAllText(blob + "." + unique + ".tmp", "partial");
            File.WriteAllText(blob + ".h." + unique + ".tmp", "partial");

            var shard = Path.GetDirectoryName(blob)!;
            var unrelated = Path.Join(shard, "notes.txt");
            File.WriteAllText(unrelated, "keep");
            var foreignTemp = blob + ".notes.tmp";
            File.WriteAllText(foreignTemp, "keep");
            var otherDir = Path.Join(cacheDir, "backups");
            Directory.CreateDirectory(otherDir);
            File.WriteAllText(Path.Join(otherDir, "db.bak"), "keep");

            var result = SegmentCacheNntpClient.PurgeDirectory(cacheDir);

            Assert.Equal(6, result.Deleted);
            Assert.Equal(2, result.Skipped);
            Assert.Equal(0, result.Failed);
            Assert.Null(result.FailureReason);
            Assert.True(File.Exists(unrelated));
            Assert.True(File.Exists(foreignTemp));
            Assert.True(File.Exists(Path.Join(otherDir, "db.bak")));
            Assert.False(File.Exists(blob));
            Assert.False(File.Exists(CacheBlobPath(cacheDir, "segment-2")));
            Assert.False(Directory.Exists(Path.GetDirectoryName(CacheBlobPath(cacheDir, "segment-2"))));
            Assert.True(Directory.Exists(cacheDir));
        }
        finally
        {
            if (Directory.Exists(cacheDir))
                Directory.Delete(cacheDir, recursive: true);
        }
    }

    [Fact]
    public void PurgeDirectory_MissingDirectory_IsNoOp()
    {
        var cacheDir = Path.Join(
            Path.GetTempPath(), "nzbdav-segment-cache-" + Guid.NewGuid().ToString("N"));

        var result = SegmentCacheNntpClient.PurgeDirectory(cacheDir);

        Assert.Equal(0, result.Deleted);
        Assert.Equal(0, result.Failed);
        Assert.False(Directory.Exists(cacheDir));
    }

    [Fact]
    public void PurgeDirectory_IgnoresSymlinkedShardDirectory()
    {
        var root = Path.Join(
            Path.GetTempPath(), "nzbdav-segment-cache-" + Guid.NewGuid().ToString("N"));
        var cacheDir = Path.Join(root, "cache");
        var outside = Path.Join(root, "outside");
        try
        {
            Directory.CreateDirectory(cacheDir);
            // Layout a real cache entry outside the cache tree, then link a shard name to it.
            WriteCacheEntry(outside, "segment-1", "one"u8.ToArray());
            var outsideBlob = CacheBlobPath(outside, "segment-1");
            var outsideShard = Path.GetDirectoryName(outsideBlob)!;
            var linkedShard = Path.Join(cacheDir, Path.GetFileName(outsideShard));
            try
            {
                Directory.CreateSymbolicLink(linkedShard, outsideShard);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                return; // symlinks unavailable on this host
            }

            var result = SegmentCacheNntpClient.PurgeDirectory(cacheDir);

            Assert.Equal(0, result.Deleted);
            Assert.Equal(0, result.Failed);
            Assert.True(File.Exists(outsideBlob));
            Assert.True(File.Exists(outsideBlob + ".h"));
            Assert.True(Directory.Exists(linkedShard));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static async Task ReadAndDisposeAsync(Stream stream)
    {
        await using (stream)
            await stream.CopyToAsync(Stream.Null);
    }

    private sealed class ThrowOnDisposeMemoryStream(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
                throw new IOException("simulated source validation failure");
        }
    }

    private static string SegmentHash(string segmentId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(segmentId)));

    private static string CacheBlobPath(string cacheDir, string segmentId)
    {
        var hash = SegmentHash(segmentId);
        return Path.Join(cacheDir, hash[..2], hash);
    }

    private static void WriteCacheEntry(string cacheDir, string segmentId, byte[] content)
    {
        var hash = SegmentHash(segmentId);
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
