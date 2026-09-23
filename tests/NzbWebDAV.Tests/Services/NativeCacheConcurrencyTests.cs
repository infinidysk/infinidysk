using Microsoft.Data.Sqlite;
using NzbWebDAV.Services.NativeCache;

namespace NzbWebDAV.Tests.Services;

public sealed class NativeCacheConcurrencyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "native-concurrency-" + Guid.NewGuid().ToString("N"));
    private NativeCacheFolder Folder()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "cache");
        Directory.CreateDirectory(path);
        return new() { Id = "cache", Path = path, MinFreeBytes = 0 };
    }

    [Fact]
    public async Task RetiredCleanup_DoesNotBypassTheHealthyReplacement()
    {
        var first = Folder() with { MaxBytes = NativeCacheStore.BlockSize + 200000, Priority = 10 };
        var path = Path.Combine(_root, "second");
        Directory.CreateDirectory(path);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var store = new NativeCacheStore(Path.Combine(_root, "index.db"),
            [first, new() { Id = "second", Path = path, MinFreeBytes = 0 }])
        {
            BeforeRetiredDeleteAsync = async _ => { entered.TrySetResult(); await release.Task; }
        };
        var identity = new NativeCacheIdentity("cleanup-hit", "v1", NativeCacheStore.BlockSize * 2L);
        var block = new byte[NativeCacheStore.BlockSize];
        Assert.True(await store.WriteBlockAsync(identity, 0, block));
        Assert.True(await store.RestartPartialWarmAsync(identity, block.Length, CancellationToken.None));
        Assert.True(await store.WriteBlockAsync(identity, 0, block));
        Assert.True(await store.WriteBlockAsync(identity, block.Length, block));
        var cleanup = store.ReclaimRetiredAsync(first.Id);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(block.Length, await store.ReadBlockAsync(identity, 0, block).WaitAsync(TimeSpan.FromSeconds(2)));
        }
        finally { release.TrySetResult(); await cleanup; }
    }

    [Fact]
    public async Task RetiredPlacement_SurvivesRestart_AndCannotBeImportedFromOldRoot()
    {
        var first = Folder() with { MaxBytes = NativeCacheStore.BlockSize + 200000, Priority = 10 };
        var path = Path.Combine(_root, "second");
        Directory.CreateDirectory(path);
        var second = new NativeCacheFolder { Id = "second", Path = path, MinFreeBytes = 0 };
        var identity = new NativeCacheIdentity("restart-move", "generation", NativeCacheStore.BlockSize * 2L);
        var catalogue = Path.Combine(_root, "index.db");
        long available = long.MaxValue;
        await using (var store = new NativeCacheStore(catalogue, [first, second]) { AvailableBytesOverride = _ => available })
        {
            Assert.True(await store.WriteBlockAsync(identity, 0, new byte[NativeCacheStore.BlockSize]));
            available = 0;
            Assert.False(await store.RestartPartialWarmAsync(identity, NativeCacheStore.BlockSize, CancellationToken.None));
            Assert.Equal(NativeCacheStore.BlockSize, await store.GetCoverageAsync(identity));
            available = long.MaxValue;
            Assert.True(await store.RestartPartialWarmAsync(identity, NativeCacheStore.BlockSize, CancellationToken.None));
        }
        await using var reopened = new NativeCacheStore(catalogue, [first, second]);
        Assert.Equal(0, await reopened.ScanAsync(first.Id));
        Assert.True((await reopened.GetStatusAsync()).Single(status => status.Id == first.Id).CommittedBytes > 0);
        Assert.True(await reopened.WriteBlockAsync(identity, NativeCacheStore.BlockSize, new byte[NativeCacheStore.BlockSize]));
        Assert.Equal("second", Assert.Single(await reopened.ListEntriesAsync(second.Id, null, 10)).FolderId);
        Assert.Equal(1, await reopened.ReclaimRetiredAsync(first.Id, clear: true));
        Assert.Equal(0, (await reopened.GetStatusAsync()).Single(status => status.Id == first.Id).CommittedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PartialWarm_RestartsOnEligibleFolder_AndRetainsOldAllocationUntilSafeCleanup(bool offline)
    {
        var first = Folder() with { MaxBytes = NativeCacheStore.BlockSize + 200000, Priority = 10 };
        var path = Path.Combine(_root, "second");
        Directory.CreateDirectory(path);
        var second = new NativeCacheFolder { Id = "second", Path = path, MinFreeBytes = 0 };
        await using var store = new NativeCacheStore(Path.Combine(_root, "index.db"), [first, second]);
        var identity = new NativeCacheIdentity("move", "generation", NativeCacheStore.BlockSize * 2L);
        var bytes = new byte[NativeCacheStore.BlockSize];
        new Random(38).NextBytes(bytes);
        Assert.True(await store.WriteBlockAsync(identity, 0, bytes));
        await store.SetPinnedAsync(identity, true);
        Assert.False(await store.RestartPartialWarmAsync(identity, NativeCacheStore.BlockSize, CancellationToken.None));
        await store.SetPinnedAsync(identity, false);
        if (offline) Directory.Move(first.Path, first.Path + "-offline");
        try
        {
            using (store.AcquireLease(identity))
            {
                Assert.True(await store.RestartPartialWarmAsync(identity, NativeCacheStore.BlockSize, CancellationToken.None));
                Assert.Equal(0, await store.GetCoverageAsync(identity));
                using var reservation = await store.ReserveWarmAsync(identity, identity.Length);
                Assert.NotNull(reservation);
                Assert.True(await store.WriteBlockAsync(identity, 0, bytes));
                Assert.True(await store.WriteBlockAsync(identity, NativeCacheStore.BlockSize, bytes));
                Assert.Equal(identity.Length, await store.GetCoverageAsync(identity));
                Assert.Equal("second", Assert.Single(await store.ListEntriesAsync("second", null, 10)).FolderId);
                await store.ReclaimRetiredAsync(first.Id);
                Assert.True((await store.GetStatusAsync()).Single(status => status.Id == first.Id).CommittedBytes > 0);
            }
        }
        finally { if (offline) Directory.Move(first.Path + "-offline", first.Path); }
        Assert.Equal(0, await store.ScanAsync(first.Id)); // Retired replicas cannot resurrect coverage.
        await store.ReclaimRetiredAsync(first.Id);
        Assert.Equal(0, (await store.GetStatusAsync()).Single(status => status.Id == first.Id).CommittedBytes);
        var actual = new byte[bytes.Length];
        Assert.Equal(bytes.Length, await store.ReadBlockAsync(identity, 0, actual));
        Assert.Equal(bytes, actual);
    }

    [Fact]
    public async Task ForegroundWrite_DoesNotWaitBehindAnotherWriter()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var store = new NativeCacheStore(Path.Combine(_root, "index.db"), [Folder()])
        {
            BeforeWriteReserveAsync = async _ => { entered.TrySetResult(); await release.Task; }
        };
        var first = store.WriteBlockAsync(new("first", "v1", 3), 0, new byte[3]);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(await store.WriteBlockAsync(new("other", "v1", 3), 0, new byte[3]).WaitAsync(TimeSpan.FromSeconds(2)));
        }
        finally { release.TrySetResult(); await first; }
    }

    [Fact]
    public async Task StalledWriter_DoesNotBlockAnIndependentFilesystem()
    {
        if (!OperatingSystem.IsLinux()) return; // Uses a separate tmpfs device to model an independent mount.
        var firstFolder = Folder();
        var secondPath = Path.Combine("/dev/shm", "native-isolation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(secondPath);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        try
        {
            await using var store = new NativeCacheStore(Path.Combine(_root, "index.db"),
                [firstFolder with { Priority = 10 }, new() { Id = "healthy", Path = secondPath, MinFreeBytes = 0 }])
            {
                BeforeWriteReserveAsync = async _ =>
                {
                    if (Interlocked.Increment(ref calls) == 1) { entered.TrySetResult(); await release.Task; }
                }
            };
            var first = store.WriteBlockAsync(new("first", "v1", 3), 0, new byte[3]);
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
                var other = new NativeCacheIdentity("other", "v1", 3);
                Assert.True(await store.WriteBlockAsync(other, 0, new byte[] { 1, 2, 3 }).WaitAsync(TimeSpan.FromSeconds(2)));
                Assert.Equal("healthy", Assert.Single(await store.ListEntriesAsync("healthy", null, 10)).FolderId);
                Assert.True((await store.ProbeAsync("healthy").WaitAsync(TimeSpan.FromSeconds(2))).Writable);
            }
            finally { release.TrySetResult(); await first; }
        }
        finally { Directory.Delete(secondPath, recursive: true); }
    }

    [Fact]
    public async Task StalledScan_AllowsOtherWrites_RejectsSameKey_ProtectsEviction_AndCleansCancellation()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var folder = Folder();
        await using var store = new NativeCacheStore(Path.Combine(_root, "index.db"), [folder])
        {
            BeforeScanReadAsync = async (_, ct) => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); }
        };
        var identity = new NativeCacheIdentity("scanned", "v1", NativeCacheStore.BlockSize + 1);
        Assert.True(await store.WriteBlockAsync(identity, 0, new byte[NativeCacheStore.BlockSize]));
        using var cancellation = new CancellationTokenSource();
        var scan = store.ScanAsync(folder.Id, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task<bool>? other = null;
        try
        {
            other = store.WriteBlockAsync(new("other", "v1", 3), 0, new byte[3]);
            Assert.True(await other.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.False(await store.WriteBlockAsync(identity, NativeCacheStore.BlockSize, new byte[1]).WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Equal(1, await store.EvictAsync(folder.Id, clear: true).WaitAsync(TimeSpan.FromSeconds(2)));
            var preserved = Assert.Single(await store.ListEntriesAsync(folder.Id, null, 10));
            Assert.Equal(identity.Key, preserved.Key);
            Assert.True(preserved.AllocatedBytes >= NativeCacheStore.BlockSize);
        }
        finally
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scan);
            if (other is not null) await other;
        }
        Assert.True(await store.WriteBlockAsync(identity, NativeCacheStore.BlockSize, new byte[1]));
    }

    [Fact]
    public async Task MissingRangeBytes_CountsOnlyCommittedBlocksInsideAlignedRange()
    {
        await using var store = new NativeCacheStore(Path.Combine(_root, "index.db"), [Folder()]);
        var identity = new NativeCacheIdentity("range", "v1", NativeCacheStore.BlockSize * 2L + 3);
        Assert.True(await store.WriteBlockAsync(identity, NativeCacheStore.BlockSize, new byte[NativeCacheStore.BlockSize]));
        Assert.Equal(NativeCacheStore.BlockSize, await store.GetMissingRangeBytesAsync(identity, 0, NativeCacheStore.BlockSize * 2L));
        Assert.Equal(3, await store.GetMissingRangeBytesAsync(identity, NativeCacheStore.BlockSize, identity.Length));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.GetMissingRangeBytesAsync(identity, 1, identity.Length));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.GetMissingRangeBytesAsync(identity, 0, 1));
    }

    [Fact]
    public async Task SuccessfulScan_ReconcilesCrashReservationExactly_AndIsIdempotent()
    {
        var folder = Folder();
        var catalogue = Path.Combine(_root, "index.db");
        await using var store = new NativeCacheStore(catalogue, [folder]);
        var identity = new NativeCacheIdentity("interrupted", "v1", 3);
        Assert.True(await store.WriteBlockAsync(identity, 0, new byte[3]));
        var exact = (await store.GetStatusAsync()).Single().CommittedBytes;
        using var database = new SqliteConnection($"Data Source={catalogue};Pooling=False");
        database.Open();
        using var command = database.CreateCommand();
        command.CommandText = "DELETE FROM Blocks; UPDATE Entries SET Bytes=Bytes+1000000,Dirty=1,PendingBytes=1000000";
        command.ExecuteNonQuery();
        Assert.Equal(1, await store.ScanAsync(folder.Id));
        Assert.Equal(exact, (await store.GetStatusAsync()).Single().CommittedBytes);
        command.CommandText = "SELECT Dirty FROM Entries";
        Assert.Equal(0L, command.ExecuteScalar());
        command.CommandText = "SELECT PendingBytes FROM Entries";
        Assert.Equal(0L, command.ExecuteScalar());
        Assert.Equal(1, await store.ScanAsync(folder.Id));
        Assert.Equal(exact, (await store.GetStatusAsync()).Single().CommittedBytes);
        Assert.Equal(3, await store.ReadBlockAsync(identity, 0, new byte[3]));
    }

    [Fact]
    public async Task ExplicitScan_CanReassignInactiveFolderId_OnlyAfterReverification()
    {
        var folder = Folder();
        var catalogue = Path.Combine(_root, "index.db");
        var identity = new NativeCacheIdentity("reregistered", "v1", 3);
        await using (var initial = new NativeCacheStore(catalogue, [folder]))
            Assert.True(await initial.WriteBlockAsync(identity, 0, new byte[3]));
        var registered = folder with { Id = "new-explicit-registration" };
        await using var store = new NativeCacheStore(catalogue, [registered]);
        Assert.Equal(0, await store.ReadBlockAsync(identity, 0, new byte[3]));
        Assert.Equal(1, await store.ScanAsync(registered.Id));
        Assert.Equal(3, await store.ReadBlockAsync(identity, 0, new byte[3]));
        Assert.Equal(3, (await store.ListEntriesAsync(registered.Id, null, 10)).Single().VerifiedBytes);
    }

    [Fact]
    public async Task ExplicitScan_RemovesCoverageForNowCorruptBlocks()
    {
        var folder = Folder();
        await using var store = new NativeCacheStore(Path.Combine(_root, "index.db"), [folder]);
        var identity = new NativeCacheIdentity("corrupted", "v1", 3);
        Assert.True(await store.WriteBlockAsync(identity, 0, new byte[3]));
        await File.WriteAllBytesAsync(Path.Combine(folder.Path, "v1", identity.Key[..2], identity.Key, "content.data"), [1, 2, 3]);
        Assert.Equal(1, await store.ScanAsync(folder.Id));
        Assert.Equal(0, await store.GetCoverageAsync(identity));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
}
