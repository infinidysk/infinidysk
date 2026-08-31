using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services.Repair;
using NzbWebDAV.Tests.Fakes;
using NzbWebDAV.Tests.TestUtils;
using UsenetSharp.Models;

namespace NzbWebDAV.Tests.Clients.Usenet;

public sealed class BatchLocalDataChainTests
{
    [Fact]
    public async Task PatchCacheNetworkMixedBatch_UsesPatchThenCacheThenOneNetworkMiss()
    {
        var root = Path.Join(Path.GetTempPath(), "nzbdav-chain-" + Guid.NewGuid().ToString("N"));
        var cacheDir = Path.Join(root, "cache");
        var patchDir = Path.Join(root, "patch");
        var a = "a@test";
        var b = "b@test";
        var c = "c@test";
        try
        {
            Directory.CreateDirectory(cacheDir);
            Directory.CreateDirectory(patchDir);
            WriteCache(cacheDir, c, "ccc"u8.ToArray());
            var store = new RepairPatchStore(patchDir, 1024 * 1024);
            await store.EnsureCatalogLoadedAsync(CancellationToken.None);
            store.CommitPatch(a, "aaa"u8.ToArray(), Header("aaa"u8.ToArray()));

            var inner = new FakeNntpClient(new Dictionary<string, byte[]>
            {
                [b] = "bbb"u8.ToArray(),
                [c] = "ccc"u8.ToArray(),
            }, useCachedYencStreams: true);
            var statistics = new SegmentCacheStatistics();
            using var cache = new SegmentCacheNntpClient(
                inner, cacheDir, 1024 * 1024, usageTracker: null, metricsWriter: null, enumerateCacheFiles: null, statistics);
            await cache.CatalogLoadTask.WaitAsync(TimeSpan.FromSeconds(5));
            using var repair = new RepairedSegmentNntpClient(cache, store);

            var recorder = new ArticleBodyCompletionRecorder();
            var batch = await repair.DecodedBodiesAsync([a, b, c], recorder.Invoke, CancellationToken.None);
            await batch.DrainAsync();

            Assert.Equal(1, inner.BatchRequestCount);
            Assert.Equal(["b@test"], inner.RequestedSegmentIds.OrderBy(x => x).ToArray());
            Assert.Equal(1, recorder.Count);
            Assert.Equal(ArticleBodyResult.Retrieved, recorder.Result);
            Assert.Equal(1, statistics.GetSnapshot().Hits);
            Assert.Equal(1, statistics.GetSnapshot().Misses);
            Assert.Equal(1, statistics.GetSnapshot().WriteCommits);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PatchHit_IsNeverWrittenIntoSegmentCache()
    {
        var root = Path.Join(Path.GetTempPath(), "nzbdav-chain-" + Guid.NewGuid().ToString("N"));
        var cacheDir = Path.Join(root, "cache");
        var patchDir = Path.Join(root, "patch");
        const string id = "patched@test";
        try
        {
            Directory.CreateDirectory(cacheDir);
            Directory.CreateDirectory(patchDir);
            var store = new RepairPatchStore(patchDir, 1024 * 1024);
            await store.EnsureCatalogLoadedAsync(CancellationToken.None);
            store.CommitPatch(id, "patch"u8.ToArray(), Header("patch"u8.ToArray()));
            var inner = new FakeNntpClient(new Dictionary<string, byte[]>(), useCachedYencStreams: true);
            var statistics = new SegmentCacheStatistics();
            using var cache = new SegmentCacheNntpClient(
                inner, cacheDir, 1024 * 1024, usageTracker: null, metricsWriter: null, enumerateCacheFiles: null, statistics);
            await cache.CatalogLoadTask.WaitAsync(TimeSpan.FromSeconds(5));
            using var repair = new RepairedSegmentNntpClient(cache, store);

            var batch = await repair.DecodedBodiesAsync([id], onConnectionReadyAgain: null, CancellationToken.None);
            await batch.DrainAsync();

            Assert.Equal(0, inner.BatchRequestCount);
            Assert.Equal(0, statistics.GetSnapshot().WriteAttempts);
            Assert.Equal(0, statistics.GetSnapshot().Hits);
            Assert.Equal(0, statistics.GetSnapshot().Misses);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task FullyLocalOrdinaryBatch_BypassesDownloadingPermitAndProvider()
    {
        var root = Path.Join(Path.GetTempPath(), "nzbdav-chain-" + Guid.NewGuid().ToString("N"));
        var cacheDir = Path.Join(root, "cache");
        try
        {
            Directory.CreateDirectory(cacheDir);
            WriteCache(cacheDir, "a", "aaa"u8.ToArray());
            var inner = new FakeNntpClient(new Dictionary<string, byte[]>(), useCachedYencStreams: true);
            var config = CreateConfig();
            using var downloading = new DownloadingNntpClient(inner, config);
            using var cache = new SegmentCacheNntpClient(downloading, cacheDir, 1024 * 1024);
            await cache.CatalogLoadTask.WaitAsync(TimeSpan.FromSeconds(5));

            var batch = await cache.DecodedBodiesAsync(["a"], onConnectionReadyAgain: null, CancellationToken.None);
            await batch.DrainAsync();
            Assert.Equal(0, inner.BatchRequestCount);
            Assert.Equal(0, inner.BodyRequestCount);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task FullyLocalExclusiveBatch_ReleasesExistingPermitExactlyOnce()
    {
        var root = Path.Join(Path.GetTempPath(), "nzbdav-chain-" + Guid.NewGuid().ToString("N"));
        var cacheDir = Path.Join(root, "cache");
        try
        {
            Directory.CreateDirectory(cacheDir);
            WriteCache(cacheDir, "a", "aaa"u8.ToArray());
            var inner = new FakeNntpClient(new Dictionary<string, byte[]>(), useCachedYencStreams: true);
            using var cache = new SegmentCacheNntpClient(inner, cacheDir, 1024 * 1024);
            await cache.CatalogLoadTask.WaitAsync(TimeSpan.FromSeconds(5));
            var recorder = new ArticleBodyCompletionRecorder();
            var exclusive = new UsenetExclusiveConnection(recorder.Invoke);
            var batch = await cache.DecodedBodiesAsync(["a"], exclusive, CancellationToken.None);
            await batch.DrainAsync();
            Assert.Equal(1, recorder.Count);
            Assert.Equal(ArticleBodyResult.Retrieved, recorder.Result);
            Assert.Equal(0, inner.BatchRequestCount);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static ConfigManager CreateConfig()
    {
        var config = new ConfigManager();
        config.UpdateValues(
        [
            new ConfigItem
            {
                ConfigName = ConfigKeys.UsenetProviders,
                ConfigValue =
                    """{"providers":[{"host":"nntp.example","port":563,"useSsl":true,"user":"u","pass":"p","maxConnections":2,"type":1}]}""",
            },
            new ConfigItem { ConfigName = ConfigKeys.UsenetMaxQueueConnections, ConfigValue = "2" },
            new ConfigItem { ConfigName = ConfigKeys.UsenetMaxDownloadConnections, ConfigValue = "2" },
        ]);
        return config;
    }

    private static void WriteCache(string cacheDir, string segmentId, byte[] content)
    {
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(segmentId)));
        var directory = Path.Join(cacheDir, hash[..2]);
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Join(directory, hash), content);
        File.WriteAllText(
            Path.Join(directory, hash) + ".h",
            System.Text.Json.JsonSerializer.Serialize(Header(content), new System.Text.Json.JsonSerializerOptions { IncludeFields = true }));
    }

    private static UsenetYencHeader Header(byte[] content) => new()
    {
        FileName = "test.bin",
        FileSize = content.Length,
        LineLength = 128,
        PartNumber = 1,
        TotalParts = 1,
        PartOffset = 0,
        PartSize = content.Length,
    };
}
