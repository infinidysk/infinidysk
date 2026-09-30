using NzbWebDAV.Services.NativeCache;

namespace NzbWebDAV.Tests.Services;

public sealed class NativeCacheBrowserTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "native-cache-browser-" + Guid.NewGuid().ToString("N"));
    public NativeCacheBrowserTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task Browser_FiltersSparseFiles_PagesNames_AndReportsOnlyConfirmedEvictions()
    {
        var path = Path.Combine(_root, "media");
        Directory.CreateDirectory(path);
        var folder = new NativeCacheFolder { Id = "disk", Name = "Disk", Path = path, MaxBytes = 100_000_000, MinFreeBytes = 0 };
        await using var store = new NativeCacheStore(Path.Combine(_root, "catalogue.db"), [folder]);
        var alpha = new NativeCacheIdentity("alpha", "v1", 3) { DisplayName = "Alpha.mkv" };
        var beta = new NativeCacheIdentity("beta", "v1", NativeCacheStore.BlockSize + 3) { DisplayName = "Beta.mkv" };
        Assert.True(await store.WriteBlockAsync(alpha, 0, new byte[] { 1, 2, 3 }));
        Assert.True(await store.WriteBlockAsync(beta, 0, new byte[NativeCacheStore.BlockSize]));
        var totals = await store.GetBrowserTotalsAsync();
        Assert.Equal(2, totals.LiveFiles);
        Assert.Equal(2, totals.VerifiedBlocks);
        Assert.Equal(0, totals.RetiredEntries);
        var first = await store.BrowseFilesAsync(null, null, "all", "name", null, 1);
        Assert.Equal(2, first.TotalCount);
        Assert.Equal("Alpha.mkv", Assert.Single(first.Items).DisplayName);
        Assert.NotNull(first.NextCursor);
        var second = await store.BrowseFilesAsync(null, null, "all", "name", first.NextCursor, 1);
        Assert.Equal("Beta.mkv", Assert.Single(second.Items).DisplayName);
        Assert.Null(second.NextCursor);
        var coverageFirst = await store.BrowseFilesAsync(null, null, "all", "coverage", null, 1);
        Assert.Equal(alpha.Key, Assert.Single(coverageFirst.Items).Key);
        var coverageSecond = await store.BrowseFilesAsync(null, null, "all", "coverage", coverageFirst.NextCursor, 1);
        Assert.Equal(beta.Key, Assert.Single(coverageSecond.Items).Key);
        Assert.Single((await store.BrowseFilesAsync(null, "bet", "partial", "recent", null, 10)).Items);
        Assert.Single((await store.BrowseFilesAsync(null, null, "complete", "recent", null, 10)).Items);
        Assert.Empty((await store.BrowseFilesAsync(null, null, "empty", "recent", null, 10)).Items);
        await Assert.ThrowsAsync<ArgumentException>(() => store.BrowseFilesAsync(null, null, "all", "name", "not-base64", 10));
        await store.SetPinnedKeyAsync(alpha.Key, true);
        Assert.Equal(1, await store.EvictAsync(folder.Id, clear: true));
        var events = await store.GetEvictionsAsync(null, "clear", null, 10);
        var evicted = Assert.Single(events.Items);
        Assert.Equal(beta.Key, evicted.Key);
        Assert.Equal("clear", evicted.Reason);
        Assert.Equal(NativeCacheStore.BlockSize, evicted.VerifiedBytes);
        Assert.Equal(alpha.Key, Assert.Single((await store.BrowseFilesAsync(null, null, "all", "recent", null, 10)).Items).Key);
    }

    [Fact]
    public async Task TrafficDeltas_PersistAcrossReopen_AndFailedFlushCanBeRestored()
    {
        var path = Path.Combine(_root, "media");
        Directory.CreateDirectory(path);
        var folder = new NativeCacheFolder { Id = "disk", Path = path, MinFreeBytes = 0 };
        var catalogue = Path.Combine(_root, "traffic.db");
        var statistics = new NativeCacheStatistics();
        statistics.Hit(4096);
        statistics.Miss();
        statistics.SourceBytes(2048);
        statistics.Committed(1024);
        var delta = statistics.DrainTraffic();
        Assert.Equal(0, statistics.Pending24h().HitBlocks);
        statistics.RestoreTraffic(delta);
        Assert.Equal(1, statistics.Pending24h().HitBlocks);
        await using (var store = new NativeCacheStore(catalogue, [folder]))
            await store.SaveTrafficAsync(statistics.DrainTraffic());
        await using (var reopened = new NativeCacheStore(catalogue, [folder]))
        {
            var saved = await reopened.GetTraffic24hAsync();
            Assert.Equal(1, saved.HitBlocks);
            Assert.Equal(4096, saved.HitBytes);
            Assert.Equal(1, saved.MissBlocks);
            Assert.Equal(2048, saved.MissBytes);
            Assert.Equal(1024, saved.CommittedBytes);
        }
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
