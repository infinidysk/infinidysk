using NzbWebDAV.Services.NativeCache;

namespace NzbWebDAV.Tests.Services;

public sealed class NativeCacheJournalTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "native-journal-" + Guid.NewGuid().ToString("N"));
    private NativeCacheFolder Folder()
    {
        var path = Path.Combine(_root, "cache");
        Directory.CreateDirectory(path);
        return new() { Id = "cache", Path = path, MinFreeBytes = 0 };
    }
    private static string Journal(NativeCacheFolder folder, NativeCacheIdentity identity)
        => Path.Combine(folder.Path, "v1", identity.Key[..2], identity.Key, "ranges.journal");

    [Fact]
    public async Task Append_RepairsTornTail_AndNewBlockSurvivesCatalogueLoss()
    {
        var folder = Folder();
        var identity = new NativeCacheIdentity("torn", "v1", NativeCacheStore.BlockSize + 3);
        await using (var store = new NativeCacheStore(Path.Combine(_root, "original.db"), [folder]))
        {
            Assert.True(await store.WriteBlockAsync(identity, 0, new byte[NativeCacheStore.BlockSize]));
            await File.AppendAllTextAsync(Journal(folder, identity), "{\"Offset\":");
            Assert.True(await store.WriteBlockAsync(identity, NativeCacheStore.BlockSize, new byte[3]));
        }
        await using var recovered = new NativeCacheStore(Path.Combine(_root, "recovered.db"), [folder]);
        Assert.Equal(1, await recovered.ScanAsync(folder.Id));
        Assert.Equal(identity.Length, await recovered.GetCoverageAsync(identity));
        Assert.Equal(3, await recovered.ReadBlockAsync(identity, NativeCacheStore.BlockSize, new byte[3]));
    }

    [Fact]
    public async Task Scan_NeverImportsUnterminatedRecord_EvenIfJsonIsComplete()
    {
        var folder = Folder();
        var identity = new NativeCacheIdentity("uncommitted", "v1", 3);
        await using (var store = new NativeCacheStore(Path.Combine(_root, "original.db"), [folder]))
            Assert.True(await store.WriteBlockAsync(identity, 0, new byte[3]));
        var journal = Journal(folder, identity);
        await File.WriteAllTextAsync(journal, (await File.ReadAllTextAsync(journal)).TrimEnd('\n'));
        await using var recovered = new NativeCacheStore(Path.Combine(_root, "recovered.db"), [folder]);
        Assert.Equal(1, await recovered.ScanAsync(folder.Id));
        Assert.Equal(0, await recovered.GetCoverageAsync(identity));
    }

    [Fact]
    public async Task Append_PathologicalTailDeclinesBoundedRepair_WithoutPublishingNewCoverage()
    {
        var folder = Folder();
        var identity = new NativeCacheIdentity("oversized", "v1", NativeCacheStore.BlockSize + 3);
        await using var store = new NativeCacheStore(Path.Combine(_root, "original.db"), [folder]);
        Assert.True(await store.WriteBlockAsync(identity, 0, new byte[NativeCacheStore.BlockSize]));
        var journal = Journal(folder, identity);
        await File.AppendAllTextAsync(journal, new string('x', 256 * 1024));
        var length = new FileInfo(journal).Length;
        Assert.False(await store.WriteBlockAsync(identity, NativeCacheStore.BlockSize, new byte[3]));
        Assert.Equal(length, new FileInfo(journal).Length);
        Assert.Equal(NativeCacheStore.BlockSize, await store.GetCoverageAsync(identity));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
