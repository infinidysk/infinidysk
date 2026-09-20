using NzbWebDAV.Services.NativeCache;

namespace NzbWebDAV.Tests.Services;

public sealed class NativeCacheStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "native-cache-tests-" + Guid.NewGuid().ToString("N"));

    public NativeCacheStoreTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task CommittedBlock_SurvivesReopen_AndOtherGenerationMisses()
    {
        var folder = CreateFolder();
        var identity = new NativeCacheIdentity("item", "generation-one", 4);
        await using (var store = new NativeCacheStore(Path.Combine(_root, "catalogue.db"), [folder]))
        {
            Assert.True(await store.WriteBlockAsync(identity, 0, new byte[] { 1, 2, 3, 4 }));
        }
        await using var reopened = new NativeCacheStore(Path.Combine(_root, "catalogue.db"), [folder]);
        var buffer = new byte[4];
        Assert.Equal(4, await reopened.ReadBlockAsync(identity, 0, buffer));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, buffer);
        Assert.Equal(0, await reopened.ReadBlockAsync(identity with { Generation = "generation-two" }, 0, buffer));
    }

    [Fact]
    public async Task SparseHole_IsNeverAHit()
    {
        var folder = CreateFolder();
        await using var store = new NativeCacheStore(Path.Combine(_root, "catalogue.db"), [folder]);
        var identity = new NativeCacheIdentity("item", "generation", NativeCacheStore.BlockSize + 3);
        Assert.True(await store.WriteBlockAsync(identity, NativeCacheStore.BlockSize, new byte[] { 1, 2, 3 }));
        Assert.Equal(0, await store.ReadBlockAsync(identity, 0, new byte[NativeCacheStore.BlockSize]));
        Assert.Equal(3, await store.ReadBlockAsync(identity, NativeCacheStore.BlockSize, new byte[3]));
    }

    [Fact]
    public async Task CorruptedBlock_IsAMiss_NotReturnedToPlayback()
    {
        var folder = CreateFolder();
        await using var store = new NativeCacheStore(Path.Combine(_root, "catalogue.db"), [folder]);
        var identity = new NativeCacheIdentity("item", "generation", 3);
        Assert.True(await store.WriteBlockAsync(identity, 0, new byte[] { 1, 2, 3 }));
        var file = Directory.GetFiles(folder.Path, "content.data", SearchOption.AllDirectories).Single();
        await File.WriteAllBytesAsync(file, new byte[] { 9, 2, 3 });
        Assert.Equal(0, await store.ReadBlockAsync(identity, 0, new byte[3]));
        Assert.Equal(0, await store.GetCoverageAsync(identity));
        Assert.True(await store.WriteBlockAsync(identity, 0, new byte[] { 1, 2, 3 }));
        Assert.Equal(3, await store.ReadBlockAsync(identity, 0, new byte[3]));
    }

    [Fact]
    public async Task ReadOnlyAndMissingRoots_AreNeverWritten()
    {
        var folder = CreateFolder() with { ReadOnly = true };
        var missing = folder with { Id = "missing", Path = Path.Combine(_root, "not-mounted"), ReadOnly = false };
        await using var store = new NativeCacheStore(Path.Combine(_root, "catalogue.db"), [folder, missing]);
        Assert.False(await store.WriteBlockAsync(new NativeCacheIdentity("item", "generation", 3), 0, new byte[3]));
        Assert.Empty(Directory.EnumerateFileSystemEntries(folder.Path));
        Assert.False(Directory.Exists(missing.Path));
    }

    [Fact]
    public async Task IncompleteBlock_IsRejected()
    {
        await using var store = new NativeCacheStore(Path.Combine(_root, "catalogue.db"), [CreateFolder()]);
        await Assert.ThrowsAsync<ArgumentException>(() => store.WriteBlockAsync(
            new NativeCacheIdentity("item", "generation", 100), 0, new byte[3]));
    }

    [Fact]
    public async Task FolderPriority_AndQuota_ChooseOneDestinationPerFile()
    {
        var first = CreateFolder() with { Priority = 10, MaxBytes = 1 };
        var secondPath = Path.Combine(_root, "second");
        Directory.CreateDirectory(secondPath);
        var second = first with { Id = "second", Path = secondPath, Priority = 0, MaxBytes = 100_000_000 };
        await using var store = new NativeCacheStore(Path.Combine(_root, "catalogue.db"), [first, second]);
        Assert.True(await store.WriteBlockAsync(new NativeCacheIdentity("item", "generation", 3), 0, new byte[3]));
        Assert.Empty(Directory.GetFiles(first.Path, "content.data", SearchOption.AllDirectories));
        Assert.Single(Directory.GetFiles(second.Path, "content.data", SearchOption.AllDirectories));
    }

    [Fact]
    public void DuplicateOrNestedRoots_AreRejected()
    {
        var first = CreateFolder();
        var nested = first with { Id = "nested", Path = Path.Combine(first.Path, "nested") };
        Assert.Throws<ArgumentException>(() => NativeCacheFolder.Validate([first, nested]));
        Assert.Throws<ArgumentException>(() => NativeCacheFolder.Validate([first, first]));
    }

    [Fact]
    public async Task Clear_RetainsActiveAndPinnedEntries()
    {
        var folder = CreateFolder();
        await using var store = new NativeCacheStore(Path.Combine(_root, "catalogue.db"), [folder]);
        var id = new NativeCacheIdentity("item", "generation", 3);
        await store.WriteBlockAsync(id, 0, new byte[3]);
        using (store.AcquireLease(id)) Assert.Equal(0, await store.EvictAsync(folder.Id, clear: true));
        await store.SetPinnedAsync(id, true);
        Assert.Equal(0, await store.EvictAsync(folder.Id, clear: true));
        await store.SetPinnedAsync(id, false);
        Assert.Equal(1, await store.EvictAsync(folder.Id, clear: true));
        Assert.Equal(0, await store.GetCoverageAsync(id));
    }

    [Fact]
    public async Task ExplicitScan_ImportsVerifiedJournalIntoNewCatalogue()
    {
        var folder = CreateFolder();
        var id = new NativeCacheIdentity("item", "generation", 3);
        await using (var original = new NativeCacheStore(Path.Combine(_root, "first.db"), [folder]))
            await original.WriteBlockAsync(id, 0, new byte[] { 3, 2, 1 });
        await using var imported = new NativeCacheStore(Path.Combine(_root, "import.db"), [folder with { ReadOnly = true }]);
        Assert.Equal(1, await imported.ScanAsync(folder.Id));
        Assert.Equal(3, await imported.ReadBlockAsync(id, 0, new byte[3]));
    }

    [Fact]
    public async Task Dispose_IsIdempotent()
    {
        var store = new NativeCacheStore(Path.Combine(_root, "catalogue.db"), [CreateFolder()]);
        await store.DisposeAsync();
        await store.DisposeAsync();
    }

    [Fact]
    public async Task Scan_DoesNotMergeBlocksAcrossFolderReplicas()
    {
        var first = CreateFolder();
        var secondPath = Path.Combine(_root, "replica");
        Directory.CreateDirectory(secondPath);
        var second = first with { Id = "replica", Path = secondPath };
        var id = new NativeCacheIdentity("item", "generation", NativeCacheStore.BlockSize + 3);
        await using (var a = new NativeCacheStore(Path.Combine(_root, "a.db"), [first]))
            await a.WriteBlockAsync(id, 0, new byte[NativeCacheStore.BlockSize]);
        await using (var b = new NativeCacheStore(Path.Combine(_root, "b.db"), [second]))
            await b.WriteBlockAsync(id, NativeCacheStore.BlockSize, new byte[3]);
        await using var combined = new NativeCacheStore(Path.Combine(_root, "combined.db"),
            [first with { ReadOnly = true }, second with { ReadOnly = true }]);
        await combined.ScanAsync(first.Id);
        await combined.ScanAsync(second.Id);
        Assert.Equal(NativeCacheStore.BlockSize, await combined.GetCoverageAsync(id));
    }

    private NativeCacheFolder CreateFolder()
    {
        var path = Path.Combine(_root, "media");
        Directory.CreateDirectory(path);
        return new NativeCacheFolder { Id = "media", Name = "Media", Path = path, MaxBytes = 100_000_000, MinFreeBytes = 0 };
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
