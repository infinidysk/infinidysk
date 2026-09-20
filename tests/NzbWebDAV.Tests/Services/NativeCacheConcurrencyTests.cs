using System.Reflection;
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
    public async Task ForegroundWrite_DoesNotWaitBehindAnotherWriter()
    {
        await using var store = new NativeCacheStore(Path.Combine(_root, "index.db"), [Folder()]);
        var writer = (SemaphoreSlim)typeof(NativeCacheStore).GetField("_writer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;
        await writer.WaitAsync();
        Task<bool>? pending = null;
        try
        {
            pending = store.WriteBlockAsync(new("other", "v1", 3), 0, new byte[3]);
            Assert.False(await pending.WaitAsync(TimeSpan.FromSeconds(2)));
        }
        finally { writer.Release(); if (pending is not null) await pending; }
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
