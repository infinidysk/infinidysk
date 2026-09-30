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
    public async Task Scan_CheckpointsDuplicateRecords_AndRetainsOrphanCoverage()
    {
        var folder = Folder();
        var identity = new NativeCacheIdentity("checkpoint-orphan", "v1", 3);
        await using (var store = new NativeCacheStore(Path.Combine(_root, "original.db"), [folder]))
            Assert.True(await store.WriteBlockAsync(identity, 0, new byte[3]));
        var journal = Journal(folder, identity);
        var record = await File.ReadAllTextAsync(journal);
        await File.AppendAllTextAsync(journal, string.Concat(Enumerable.Repeat(record, 100)));
        await using var recovered = new NativeCacheStore(Path.Combine(_root, "recovered.db"), [folder]);
        Assert.Equal(1, await recovered.ScanAsync(folder.Id));
        Assert.Equal(record, await File.ReadAllTextAsync(journal));
        Assert.Equal(3, await recovered.GetCoverageAsync(identity));
    }

    [Fact]
    public async Task HardCap_DeclinesPublication_QueuesExactEntry_AndCheckpointAllowsRefill()
    {
        var folder = Folder();
        var identity = new NativeCacheIdentity("checkpoint-cap", "v1", 3);
        await using var store = new NativeCacheStore(Path.Combine(_root, "original.db"), [folder]);
        Assert.True(await store.WriteBlockAsync(identity, 0, new byte[3]));
        var journal = Journal(folder, identity);
        var record = await File.ReadAllTextAsync(journal);
        await File.AppendAllTextAsync(journal, string.Concat(Enumerable.Repeat(record, 1000)));
        await File.WriteAllBytesAsync(Path.Combine(Path.GetDirectoryName(journal)!, "content.data"), [1, 2, 3]);
        Assert.Equal(0, await store.ReadBlockAsync(identity, 0, new byte[3]));
        var length = new FileInfo(journal).Length;
        Assert.False(await store.WriteBlockAsync(identity, 0, new byte[3]));
        Assert.Equal(length, new FileInfo(journal).Length);
        Assert.Equal(1, store.PendingCheckpointCount);
        Assert.Equal(1, await store.ProcessOneCheckpointAsync());
        Assert.Equal(0, store.PendingCheckpointCount);
        Assert.Equal(0, new FileInfo(journal).Length);
        Assert.True(await store.WriteBlockAsync(identity, 0, new byte[3]));
    }

    [Fact]
    public async Task SoftLimit_CoalescesBoundedQueue_AndProcessesOnlyOneExactEntry()
    {
        var folder = Folder();
        var scanned = new List<string>();
        await using var store = new NativeCacheStore(Path.Combine(_root, "original.db"), [folder])
        { BeforeScanReadAsync = (key, _) => { scanned.Add(key); return Task.CompletedTask; } };
        for (var i = 0; i < 129; i++)
        {
            var identity = new NativeCacheIdentity("queued-" + i, "v1", 3);
            Assert.True(await store.WriteBlockAsync(identity, 0, new byte[3]));
            var journal = Journal(folder, identity);
            var record = await File.ReadAllTextAsync(journal);
            await File.AppendAllTextAsync(journal, string.Concat(Enumerable.Repeat(record, 80)));
            await File.WriteAllBytesAsync(Path.Combine(Path.GetDirectoryName(journal)!, "content.data"), [1, 2, 3]);
            Assert.Equal(0, await store.ReadBlockAsync(identity, 0, new byte[3]));
            Assert.True(await store.WriteBlockAsync(identity, 0, new byte[3]));
        }
        Assert.Equal(128, store.PendingCheckpointCount);
        Assert.Equal(1, await store.ProcessOneCheckpointAsync());
        Assert.Single(scanned);
        Assert.Equal(127, store.PendingCheckpointCount);
    }

    [Theory]
    [InlineData("before-rename")]
    [InlineData("after-rename")]
    public async Task CheckpointFailure_PreservesRecoverableJournal(string failurePhase)
    {
        var folder = Folder();
        var identity = new NativeCacheIdentity("checkpoint-failure", "v1", 3);
        await using (var store = new NativeCacheStore(Path.Combine(_root, "original.db"), [folder])
        { CheckpointPhaseAsync = (phase, _) => phase == failurePhase ? Task.FromException(new IOException("injected")) : Task.CompletedTask })
        {
            Assert.True(await store.WriteBlockAsync(identity, 0, new byte[3]));
            var record = await File.ReadAllTextAsync(Journal(folder, identity));
            await File.AppendAllTextAsync(Journal(folder, identity), record);
            Assert.Equal(0, await store.ScanAsync(folder.Id));
        }
        await using var recovered = new NativeCacheStore(Path.Combine(_root, "recovered.db"), [folder]);
        Assert.Equal(1, await recovered.ScanAsync(folder.Id));
        Assert.Equal(3, await recovered.ReadBlockAsync(identity, 0, new byte[3]));
    }

    [Fact]
    public async Task CheckpointScratchAdmission_WaitsForInFlightWriterAdmission()
    {
        var folder = Folder();
        var scanEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scanResume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writerResume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var checkpoint = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pauseWriter = false;
        await using var store = new NativeCacheStore(Path.Combine(_root, "original.db"), [folder])
        {
            BeforeScanReadAsync = async (_, ct) => { scanEntered.TrySetResult(); await scanResume.Task.WaitAsync(ct); },
            BeforeWriteReserveAsync = async ct => { if (pauseWriter) { writerEntered.SetResult(); await writerResume.Task.WaitAsync(ct); } },
            CheckpointPhaseAsync = (_, _) => { checkpoint.TrySetResult(); return Task.CompletedTask; }
        };
        Assert.True(await store.WriteBlockAsync(new("scan", "v1", 3), 0, new byte[3]));
        var scan = store.ScanAsync(folder.Id);
        await scanEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        pauseWriter = true;
        var write = store.WriteBlockAsync(new("write", "v1", 3), 0, new byte[3]);
        await writerEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        scanResume.SetResult();
        try { await Assert.ThrowsAsync<TimeoutException>(() => checkpoint.Task.WaitAsync(TimeSpan.FromMilliseconds(200))); }
        finally { writerResume.SetResult(); }
        Assert.True(await write);
        await scan;
        Assert.True(checkpoint.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task CheckpointWithoutScratchSpace_LeavesAuthoritativeJournalUnchanged()
    {
        var folder = Folder();
        var available = long.MaxValue;
        await using var store = new NativeCacheStore(Path.Combine(_root, "original.db"), [folder])
        { AvailableBytesOverride = _ => available };
        var identity = new NativeCacheIdentity("no-scratch", "v1", 3);
        Assert.True(await store.WriteBlockAsync(identity, 0, new byte[3]));
        var journal = Journal(folder, identity);
        var record = await File.ReadAllTextAsync(journal);
        await File.AppendAllTextAsync(journal, record);
        available = 0;
        Assert.Equal(0, await store.ScanAsync(folder.Id));
        Assert.Equal(record + record, await File.ReadAllTextAsync(journal));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ScanWithoutCompaction_AccountsForCrashScratch_AndWritableClearRemovesIt(bool readOnly)
    {
        var folder = Folder();
        var identity = new NativeCacheIdentity("crash-scratch", "v1", 3);
        await using (var store = new NativeCacheStore(Path.Combine(_root, "original.db"), [folder]))
            Assert.True(await store.WriteBlockAsync(identity, 0, new byte[3]));
        var directory = Path.GetDirectoryName(Journal(folder, identity))!;
        await File.WriteAllBytesAsync(Path.Combine(directory, "ranges.checkpoint.tmp"), new byte[128 * 1024]);
        long bytes;
        await using (var recovered = new NativeCacheStore(Path.Combine(_root, "recovered.db"),
            [folder with { ReadOnly = readOnly, MaxBytes = readOnly ? folder.MaxBytes : 1 }]))
        {
            Assert.Equal(1, await recovered.ScanAsync(folder.Id));
            bytes = (await recovered.GetStatusAsync()).Single().CommittedBytes;
            Assert.True(bytes >= 128 * 1024 + 64 * 1024);
        }
        await using var writable = new NativeCacheStore(Path.Combine(_root, "recovered.db"), [folder]);
        Assert.Equal(1, await writable.EvictAsync(folder.Id, clear: true));
        Assert.False(Directory.Exists(directory));
    }

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
