using NzbWebDAV.Services.NativeCache;

namespace NzbWebDAV.Tests.Services;

public sealed class NativeCacheStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "native-cache-tests-" + Guid.NewGuid().ToString("N"));

    public NativeCacheStoreTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task StatusDeadline_CoalescesStalledCalls_AndRecovers()
    {
        var folder = CreateFolder();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        await using var store = new NativeCacheStore(Path.Combine(_root, "status.db"), [folder])
        {
            StatusProbeTimeout = TimeSpan.FromMilliseconds(30),
            StatusProbeOverride = _ => { Interlocked.Increment(ref calls); entered.TrySetResult(); release.Wait(); return true; }
        };
        try
        {
            var first = store.GetStatusAsync();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Contains("unknown", Assert.Single(await first).Error!);
            var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => store.GetStatusAsync()));
            Assert.All(results, result => Assert.False(Assert.Single(result).Online));
            Assert.Equal(1, calls);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.GetStatusAsync(cancellation.Token));
        }
        finally { release.Set(); }
        Assert.True(Assert.Single(await store.GetStatusAsync()).Online);
    }

    [Fact]
    public async Task EntryGeneration_SurvivesReopen_AndExplicitCatalogueRecovery()
    {
        var folder = CreateFolder();
        var identity = new NativeCacheIdentity("generation-diagnostics", "source:revision:repair-epoch", 3);
        var catalogue = Path.Combine(_root, "catalogue.db");
        await using (var store = new NativeCacheStore(catalogue, [folder]))
            Assert.True(await store.WriteBlockAsync(identity, 0, new byte[3]));
        await using (var reopened = new NativeCacheStore(catalogue, [folder]))
            Assert.Equal(identity.Generation, Assert.Single(await reopened.ListEntriesAsync(folder.Id, null, 1)).Generation);
        await using var recovered = new NativeCacheStore(Path.Combine(_root, "recovered.db"), [folder]);
        Assert.Equal(1, await recovered.ScanAsync(folder.Id));
        Assert.Equal(identity.Generation, Assert.Single(await recovered.ListEntriesAsync(folder.Id, null, 1)).Generation);
    }

    [Fact]
    public async Task LegacyEntryGeneration_RemainsUnknownUntilExplicitScan()
    {
        var folder = CreateFolder();
        var identity = new NativeCacheIdentity("legacy-generation", "original-generation", 3);
        var catalogue = Path.Combine(_root, "catalogue.db");
        await using (var store = new NativeCacheStore(catalogue, [folder]))
            Assert.True(await store.WriteBlockAsync(identity, 0, new byte[3]));
        using (var database = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={catalogue};Pooling=False"))
        {
            database.Open();
            using var command = database.CreateCommand();
            command.CommandText = "ALTER TABLE Entries DROP COLUMN Generation";
            command.ExecuteNonQuery();
        }
        await using var reopened = new NativeCacheStore(catalogue, [folder]);
        Assert.Null(Assert.Single(await reopened.ListEntriesAsync(folder.Id, null, 1)).Generation);
        Assert.Equal(1, await reopened.ScanAsync(folder.Id));
        Assert.Equal(identity.Generation, Assert.Single(await reopened.ListEntriesAsync(folder.Id, null, 1)).Generation);
    }

    [Fact]
    public async Task VerifiedRanges_UseBoundedOffsetPages_WithoutInventingSparseCoverage()
    {
        var folder = CreateFolder();
        await using var store = new NativeCacheStore(Path.Combine(_root, "catalogue.db"), [folder]);
        var identity = new NativeCacheIdentity("range-pages", "generation-one", NativeCacheStore.BlockSize * 2L + 3);
        Assert.True(await store.WriteBlockAsync(identity, 0, new byte[NativeCacheStore.BlockSize]));
        Assert.True(await store.WriteBlockAsync(identity, NativeCacheStore.BlockSize * 2L, new byte[3]));
        var first = await store.ListVerifiedRangesAsync(identity.Key, -1, 1);
        Assert.Single(first);
        Assert.Equal(0, first[0].Offset);
        Assert.Equal(NativeCacheStore.BlockSize, first[0].Count);
        var second = await store.ListVerifiedRangesAsync(identity.Key, first[0].Offset, 1);
        Assert.Single(second);
        Assert.Equal(NativeCacheStore.BlockSize * 2L, second[0].Offset);
        Assert.Equal(3, second[0].Count);
        Assert.Empty(await store.ListVerifiedRangesAsync(identity.Key, second[0].Offset, 100));
        await Assert.ThrowsAsync<ArgumentException>(() => store.ListVerifiedRangesAsync(identity.Key, -1, 101));
        await Assert.ThrowsAsync<ArgumentException>(() => store.ListVerifiedRangesAsync("../unsafe", -1, 1));
        await Assert.ThrowsAsync<ArgumentException>(() => store.ListVerifiedRangesAsync(identity.Key, -2, 1));
    }

    [Fact]
    public async Task CataloguePages_AreBoundedAndExposePinnedEntries()
    {
        var folder = CreateFolder();
        await using var store = new NativeCacheStore(Path.Combine(_root, "index", "catalogue.db"), [folder]);
        var identities = Enumerable.Range(0, 3).Select(index => new NativeCacheIdentity("movie-" + index, "v1", 3)).ToArray();
        foreach (var identity in identities) Assert.True(await store.WriteBlockAsync(identity, 0, new byte[] { 1, 2, 3 }));
        var first = await store.ListEntriesAsync(folder.Id, null, 2);
        Assert.Equal(2, first.Count);
        var last = await store.ListEntriesAsync(folder.Id, first[^1].Key, 2);
        Assert.Single(last);
        Assert.Empty(first.Select(entry => entry.Key).Intersect(last.Select(entry => entry.Key)));
        await store.SetPinnedKeyAsync(first[0].Key, true);
        Assert.True((await store.ListEntriesAsync(folder.Id, null, 2))[0].Pinned);
        Assert.All(first, entry => Assert.Equal(3, entry.VerifiedBytes));
    }

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

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReplacedLiveRoot_EvenWithCopiedMarker_IsNeverWritten(bool symlink)
    {
        Skip.IfNot(OperatingSystem.IsLinux());
        var folder = CreateFolder();
        await using var store = new NativeCacheStore(Path.Combine(_root, "catalogue.db"), [folder]);
        var moved = folder.Path + "-original";
        Directory.Move(folder.Path, moved);
        var target = symlink ? folder.Path + "-replacement" : folder.Path;
        Directory.CreateDirectory(target);
        File.Copy(Path.Combine(moved, ".infinidysk-volume"), Path.Combine(target, ".infinidysk-volume"));
        if (symlink) Directory.CreateSymbolicLink(folder.Path, target);
        Assert.False(await store.WriteBlockAsync(new("item", "generation", 3), 0, new byte[3]));
        Assert.False((await store.GetStatusAsync()).Single().Online);
        Assert.Equal([".infinidysk-volume"], Directory.GetFileSystemEntries(target).Select(Path.GetFileName));
    }

    [Fact]
    public async Task ReplacedRootAfterRestart_CopiedMarkerRequiresExplicitNewRegistration()
    {
        var folder = CreateFolder();
        var catalogue = Path.Combine(_root, "catalogue.db");
        await using (var initial = new NativeCacheStore(catalogue, [folder]))
            Assert.True(await initial.WriteBlockAsync(new("original", "v1", 3), 0, new byte[3]));
        Directory.Move(folder.Path, folder.Path + "-original");
        Directory.CreateDirectory(folder.Path);
        File.Copy(Path.Combine(folder.Path + "-original", ".infinidysk-volume"), Path.Combine(folder.Path, ".infinidysk-volume"));
        await using (var reopened = new NativeCacheStore(catalogue, [folder]))
        {
            Assert.False((await reopened.GetStatusAsync()).Single().Online);
            Assert.False(await reopened.WriteBlockAsync(new("replacement", "v1", 3), 0, new byte[3]));
            Assert.Equal(0, await reopened.EvictAsync(folder.Id, clear: true));
            Assert.True((await reopened.GetStatusAsync()).Single().CommittedBytes > 0);
        }
        await using var explicitlyRegistered = new NativeCacheStore(catalogue, [folder with { Id = "replacement-root" }]);
        Assert.True((await explicitlyRegistered.GetStatusAsync()).Single().Online);
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
    public async Task Clear_AlreadyRemovedEntry_ReclaimsCatalogueReservation()
    {
        var folder = CreateFolder();
        await using var store = new NativeCacheStore(Path.Combine(_root, "catalogue.db"), [folder]);
        var identity = new NativeCacheIdentity("missing-entry", "v1", 3);
        Assert.True(await store.WriteBlockAsync(identity, 0, new byte[3]));
        Directory.Delete(Path.Combine(folder.Path, "v1", identity.Key[..2], identity.Key), recursive: true);
        Assert.Equal(1, await store.EvictAsync(folder.Id, clear: true));
        Assert.Equal(0, (await store.GetStatusAsync()).Single().CommittedBytes);
        Assert.Equal(0, await store.GetCoverageAsync(identity));
    }

    [Fact]
    public async Task WarmReservation_ProtectsSpaceFromOtherFiles()
    {
        var folder = CreateFolder() with { MaxBytes = 200_000 };
        await using var store = new NativeCacheStore(Path.Combine(_root, "catalogue.db"), [folder]);
        var identity = new NativeCacheIdentity("warm", "v1", 60_000);
        using var reservation = await store.ReserveWarmAsync(identity, identity.Length);
        Assert.NotNull(reservation);
        Assert.False(await store.WriteBlockAsync(new("other", "v1", 3), 0, new byte[3]));
        Assert.True(await store.WriteBlockAsync(identity, 0, new byte[60_000]));
        Assert.Equal(identity.Length, await store.FindNextMissingOffsetAsync(identity, 0, identity.Length));
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
    public async Task ReplacedMountpoint_IsNotClaimedOnRestart()
    {
        var folder = CreateFolder();
        var catalogue = Path.Combine(_root, "catalogue.db");
        await using (var store = new NativeCacheStore(catalogue, [folder]))
            Assert.True(await store.WriteBlockAsync(new("old", "v1", 3), 0, new byte[3]));
        Directory.Move(folder.Path, folder.Path + "-detached");
        Directory.CreateDirectory(folder.Path);
        await using var restarted = new NativeCacheStore(catalogue, [folder]);
        Assert.False(await restarted.WriteBlockAsync(new("new", "v1", 3), 0, new byte[3]));
        Assert.Empty(Directory.EnumerateFileSystemEntries(folder.Path));
    }

    [Fact]
    public async Task PressureEviction_RetainsPinnedAndActiveFiles()
    {
        var folder = CreateFolder() with { MaxBytes = 160_000, MaxAgeDays = 0 };
        await using var store = new NativeCacheStore(Path.Combine(_root, "catalogue.db"), [folder]);
        var first = new NativeCacheIdentity("first", "v1", 3);
        Assert.True(await store.WriteBlockAsync(first, 0, new byte[3]));
        await store.SetPinnedAsync(first, true);
        Assert.Equal(0, await store.EvictPressureAsync(folder.Id));
        Assert.Equal(3, await store.GetCoverageAsync(first));
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
