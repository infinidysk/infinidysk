using Microsoft.Data.Sqlite;
using NzbWebDAV.Services.NativeCache;

namespace NzbWebDAV.Tests.Services;

public sealed class NativeCachePressureTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "native-pressure-" + Guid.NewGuid().ToString("N"));
    private NativeCacheFolder Folder(string id = "cache", long quota = 300_000, long reserve = 100_000)
    {
        var path = Path.Combine(_root, id);
        Directory.CreateDirectory(path);
        return new() { Id = id, Path = path, MaxBytes = quota, MinFreeBytes = reserve, Priority = id == "a" ? 10 : 0 };
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SameFilesystem_ReservationsFenceOtherFolders(bool warm)
    {
        await using var store = new NativeCacheStore(Path.Combine(_root, "index.db"), [Folder("a"), Folder("b")])
        { AvailableBytesOverride = _ => 500_000 };
        using var reserved = await store.ReserveWarmAsync(new("warm-a", "v1", 150_000), 150_000);
        Assert.NotNull(reserved);
        if (warm)
        {
            using var competing = await store.ReserveWarmAsync(new("warm-b", "v1", 150_000), 150_000);
            Assert.Null(competing);
        }
        else Assert.False(await store.WriteBlockAsync(new("foreground", "v1", 3), 0, new byte[3]));
        Assert.True(await store.WriteBlockAsync(new("warm-a", "v1", 150_000), 0, new byte[150_000]));
    }

    [Fact]
    public async Task SameFilesystem_PreservesLargestConfiguredFreeSpaceFloor()
    {
        await using var store = new NativeCacheStore(Path.Combine(_root, "index.db"), [Folder("a"), Folder("b", reserve: 300_000)])
        { AvailableBytesOverride = _ => 500_000 };
        using var reservation = await store.ReserveWarmAsync(new("warm", "v1", 150_000), 150_000);
        Assert.Null(reservation);
    }

    [Fact]
    public async Task SameFilesystem_DirtyReservationsFenceNewWrites()
    {
        var catalogue = Path.Combine(_root, "index.db");
        await using var store = new NativeCacheStore(catalogue, [Folder("a"), Folder("b")])
        { AvailableBytesOverride = _ => 500_000 };
        Assert.True(await store.WriteBlockAsync(new("dirty", "v1", 3), 0, new byte[3]));
        using var database = new SqliteConnection($"Data Source={catalogue};Pooling=False");
        database.Open();
        using var command = database.CreateCommand();
        command.CommandText = "UPDATE Entries SET Dirty=1,Bytes=300000,PendingBytes=300000";
        command.ExecuteNonQuery();
        Assert.False(await store.WriteBlockAsync(new("other", "v1", 3), 0, new byte[3]));
    }

    [Fact]
    public async Task Pressure_StopsAfterReachingLowWater()
    {
        var folder = Folder(quota: 23_500_000, reserve: 0);
        await using var store = new NativeCacheStore(Path.Combine(_root, "index.db"), [folder]);
        for (var index = 0; index < 5; index++)
            Assert.True(await store.WriteBlockAsync(new("movie-" + index, "v1", NativeCacheStore.BlockSize), 0, new byte[NativeCacheStore.BlockSize]));
        Assert.Equal(1, await store.EvictPressureAsync(folder.Id));
        Assert.Equal(4, (await store.GetStatusAsync()).Single().Entries);
    }

    [Fact]
    public async Task Eviction_SkipsLeasedFirstPage_AndReachesNextEligibleEntry()
    {
        var folder = Folder(quota: 100_000_000, reserve: 0);
        await using var store = new NativeCacheStore(Path.Combine(_root, "index.db"), [folder]);
        var identities = Enumerable.Range(0, 65).Select(index => new NativeCacheIdentity("movie-" + index, "v1", 3)).OrderBy(identity => identity.Key).ToArray();
        foreach (var identity in identities) Assert.True(await store.WriteBlockAsync(identity, 0, new byte[3]));
        var leases = identities.Take(64).Select(store.AcquireLease).ToArray();
        try { Assert.Equal(1, await store.EvictAsync(folder.Id, clear: true)); }
        finally { foreach (var lease in leases) lease.Dispose(); }
        Assert.Equal(64, (await store.GetStatusAsync()).Single().Entries);
    }

    [Fact]
    public async Task DirtyButAlreadyAllocatedFile_DoesNotCauseUnrelatedPressureEviction()
    {
        var folder = Folder(quota: 20_000_000, reserve: 0);
        var catalogue = Path.Combine(_root, "index.db");
        await using var store = new NativeCacheStore(catalogue, [folder]) { AvailableBytesOverride = _ => 5_000_000 };
        var large = new NativeCacheIdentity("large", "v1", NativeCacheStore.BlockSize);
        var healthy = new NativeCacheIdentity("healthy", "v1", 3);
        Assert.True(await store.WriteBlockAsync(large, 0, new byte[NativeCacheStore.BlockSize]));
        Assert.True(await store.WriteBlockAsync(healthy, 0, new byte[3]));
        using var database = new SqliteConnection($"Data Source={catalogue};Pooling=False");
        database.Open();
        using var command = database.CreateCommand();
        command.CommandText = "UPDATE Entries SET Dirty=1 WHERE Key=$key";
        command.Parameters.AddWithValue("$key", large.Key);
        command.ExecuteNonQuery();
        using var lease = store.AcquireLease(large);
        Assert.Equal(0, await store.EvictPressureAsync(folder.Id));
        Assert.Equal(3, await store.GetCoverageAsync(healthy));
    }

    [Fact]
    public async Task Eviction_BoundsExaminedUndeletableEntries_AndResumesNextCall()
    {
        var folder = Folder(quota: 100_000_000, reserve: 0);
        var catalogue = Path.Combine(_root, "index.db");
        await using var store = new NativeCacheStore(catalogue, [folder]);
        using var database = new SqliteConnection($"Data Source={catalogue};Pooling=False");
        database.Open();
        for (var index = 0; index < 301; index++)
        {
            var key = index.ToString("x64");
            using var command = database.CreateCommand();
            command.CommandText = "INSERT INTO Entries(Key,Folder,Length,Bytes,Access) VALUES($key,$folder,3,1,1)";
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$folder", folder.Id);
            command.ExecuteNonQuery();
            var directory = Path.Combine(folder.Path, "v1", key[..2], key);
            Directory.CreateDirectory(directory);
            if (index < 300) await File.WriteAllTextAsync(Path.Combine(directory, "unexpected-file"), "preserve");
        }
        Assert.Equal(0, await store.EvictAsync(folder.Id, clear: true));
        Assert.True(store.HasPendingClearPage(folder.Id));
        Assert.Equal(301, (await store.GetStatusAsync()).Single().Entries);
        Assert.Equal(1, await store.EvictAsync(folder.Id, clear: true));
        Assert.False(store.HasPendingClearPage(folder.Id));
        Assert.Equal(300, (await store.GetStatusAsync()).Single().Entries);
    }

    [Fact]
    public async Task Pressure_ContinuesAcrossTicksUntilLowWater_NotJustBelowHighWater()
    {
        var folder = Folder(quota: 1_000_000_000, reserve: 0);
        var catalogue = Path.Combine(_root, "index.db");
        await using var store = new NativeCacheStore(catalogue, [folder]);
        SeedCatalogue(catalogue, folder.Id, 1000, bytes: 900_000);
        Assert.Equal(64, await store.EvictPressureAsync(folder.Id));
        Assert.InRange((await store.GetStatusAsync()).Single().CommittedBytes, 800_000_001, 899_999_999);
        Assert.Equal(48, await store.EvictPressureAsync(folder.Id));
        Assert.Equal(0, await store.EvictPressureAsync(folder.Id));
    }

    [Fact]
    public async Task OfflineRoot_RetiresPendingClearCursor_WithoutReclaimingMetadata()
    {
        var folder = Folder(quota: 100_000_000, reserve: 0);
        var catalogue = Path.Combine(_root, "index.db");
        await using var store = new NativeCacheStore(catalogue, [folder]);
        SeedCatalogue(catalogue, folder.Id, 100, bytes: 1);
        Assert.Equal(64, await store.EvictAsync(folder.Id, clear: true));
        Assert.True(store.HasPendingClearPage(folder.Id));
        Directory.Move(folder.Path, folder.Path + "-detached");
        Assert.Equal(0, await store.EvictAsync(folder.Id, clear: true));
        Assert.False(store.HasPendingClearPage(folder.Id));
        Assert.Equal(36, (await store.GetStatusAsync()).Single().Entries);
    }

    private static void SeedCatalogue(string catalogue, string folderId, int count, int bytes)
    {
        using var database = new SqliteConnection($"Data Source={catalogue};Pooling=False");
        database.Open();
        using var transaction = database.BeginTransaction();
        for (var index = 0; index < count; index++)
        {
            using var command = database.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO Entries(Key,Folder,Length,Bytes,Access) VALUES($key,$folder,3,$bytes,1)";
            command.Parameters.AddWithValue("$key", index.ToString("x64"));
            command.Parameters.AddWithValue("$folder", folderId);
            command.Parameters.AddWithValue("$bytes", bytes);
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
