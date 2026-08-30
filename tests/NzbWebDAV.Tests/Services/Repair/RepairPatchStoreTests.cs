using System.Security.Cryptography;
using System.Text;
using NzbWebDAV.Services.Repair;
using UsenetSharp.Models;

namespace NzbWebDAV.Tests.Services.Repair;

public sealed class RepairPatchStoreTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private static UsenetYencHeader Header(int size) => new()
    {
        FileName = "test.bin",
        FileSize = size,
        LineLength = 128,
        PartNumber = 1,
        TotalParts = 1,
        PartOffset = 0,
        PartSize = size,
    };

    [Fact]
    public async Task CommitPatch_IsVisibleAfterAtomicCommit_AndSurvivesReload()
    {
        var dir = Path.Join(Path.GetTempPath(), "nzbdav-repair-store-" + Guid.NewGuid().ToString("N"));
        const string segmentId = "seg@test";
        byte[] content = "patch-data"u8.ToArray();

        try
        {
            var store = new RepairPatchStore(dir, 1024 * 1024);
            await store.EnsureCatalogLoadedAsync(CancellationToken.None);

            store.CommitPatch(segmentId, content, Header(content.Length));

            Assert.True(store.IsRepaired(segmentId, content.Length));
            Assert.True(store.TryGet(segmentId, out var response));
            response!.Stream!.Dispose();

            var replacement = "patch-data-v2"u8.ToArray();
            store.CommitPatch(segmentId, replacement, Header(replacement.Length));
            Assert.True(store.IsRepaired(segmentId, replacement.Length));
            Assert.True(store.TryGet(segmentId, out var replaced));
            using (replaced!.Stream)
            {
                using var copy = new MemoryStream();
                replaced.Stream!.CopyTo(copy);
                Assert.Equal(replacement, copy.ToArray());
            }

            var reloaded = new RepairPatchStore(dir, 1024 * 1024);
            await reloaded.EnsureCatalogLoadedAsync(CancellationToken.None);
            Assert.True(reloaded.IsRepaired(segmentId, replacement.Length));
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Eviction_RemovesOldestWhenOverCap()
    {
        var dir = Path.Join(Path.GetTempPath(), "nzbdav-repair-evict-" + Guid.NewGuid().ToString("N"));
        try
        {
            var maxBytes = 50;
            var store = new RepairPatchStore(dir, maxBytes);
            await store.EnsureCatalogLoadedAsync(CancellationToken.None);

            store.CommitPatch("a@test", new byte[30], Header(30));
            store.CommitPatch("b@test", new byte[30], Header(30));

            Assert.False(store.Contains("a@test"));
            Assert.True(store.Contains("b@test"));
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task PartialKnownFailure_PublishesNothing()
    {
        var dir = NewTempDir("catalog-io");
        try
        {
            var blob = WriteBlob(dir, "partial-io@test", "blob-bytes"u8.ToArray());
            var store = new RepairPatchStore(
                dir,
                1024 * 1024,
                ct => YieldOneThenThrow(blob, new IOException("catalog scan failed"), ct));

            await Assert.ThrowsAsync<IOException>(
                () => store.EnsureCatalogLoadedAsync(CancellationToken.None));

            AssertUnpublished(store);
        }
        finally
        {
            DeleteDir(dir);
        }
    }

    [Fact]
    public async Task PartialUnexpectedFailure_PublishesNothing()
    {
        var dir = NewTempDir("catalog-unexpected");
        try
        {
            var blob = WriteBlob(dir, "partial-unexpected@test", "blob-bytes"u8.ToArray());
            var failure = new InvalidOperationException("catalog iterator poisoned");
            var store = new RepairPatchStore(
                dir,
                1024 * 1024,
                ct => YieldOneThenThrow(blob, failure, ct));

            var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
                () => store.EnsureCatalogLoadedAsync(CancellationToken.None));
            Assert.Same(failure, thrown);
            AssertUnpublished(store);
        }
        finally
        {
            DeleteDir(dir);
        }
    }

    [Fact]
    public async Task Cancellation_DoesNotPoisonRetry()
    {
        var dir = NewTempDir("catalog-cancel");
        var scanEntered = NewTcs();
        var scanExited = NewTcs();
        using var releaseScan = new ManualResetEventSlim(false);
        Func<CancellationToken, IEnumerable<string>> enumerate = ct =>
            YieldOneThenWait(
                WriteBlob(dir, "cancel-retry@test", "blob-bytes"u8.ToArray()),
                scanEntered,
                releaseScan,
                scanExited,
                ct);

        try
        {
            var store = new RepairPatchStore(dir, 1024 * 1024, ct => enumerate(ct));
            using var cts = new CancellationTokenSource();
            var load = store.EnsureCatalogLoadedAsync(cts.Token);
            await scanEntered.Task.WaitAsync(Timeout);
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => load);
            await scanExited.Task.WaitAsync(Timeout);
            AssertUnpublished(store);

            enumerate = ct => Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories);
            await store.EnsureCatalogLoadedAsync(CancellationToken.None);

            Assert.True(store.IsCatalogReady);
            Assert.Equal(1, store.EntryCount);
            Assert.Equal("blob-bytes"u8.ToArray().Length, store.CurrentBytes);
        }
        finally
        {
            releaseScan.Set();
            DeleteDir(dir);
        }
    }

    [Fact]
    public async Task OutOfMemory_PublishesNothing()
    {
        var dir = NewTempDir("catalog-oom");
        try
        {
            var blob = WriteBlob(dir, "partial-oom@test", "blob-bytes"u8.ToArray());
            var oom = new OutOfMemoryException("scripted");
            var store = new RepairPatchStore(
                dir,
                1024 * 1024,
                ct => YieldOneThenThrow(blob, oom, ct));

            var thrown = await Assert.ThrowsAsync<OutOfMemoryException>(
                () => store.EnsureCatalogLoadedAsync(CancellationToken.None));
            Assert.Same(oom, thrown);
            AssertUnpublished(store);
        }
        finally
        {
            DeleteDir(dir);
        }
    }

    [Fact]
    public async Task ConcurrentEnsure_RunsOneScan()
    {
        var dir = NewTempDir("catalog-concurrent");
        var scanEntered = NewTcs();
        var allowScan = NewTcs();
        var starts = 0;
        var active = 0;
        var maxActive = 0;
        var maxLock = new object();
        var content = "concurrent-blob"u8.ToArray();
        var blob = WriteBlob(dir, "concurrent@test", content);

        try
        {
            var store = new RepairPatchStore(dir, 1024 * 1024, ct =>
            {
                Interlocked.Increment(ref starts);
                var now = Interlocked.Increment(ref active);
                lock (maxLock)
                {
                    if (now > maxActive)
                        maxActive = now;
                }

                try
                {
                    scanEntered.TrySetResult();
                    allowScan.Task.Wait(ct);
                    return new[] { blob };
                }
                finally
                {
                    Interlocked.Decrement(ref active);
                }
            });

            var first = store.EnsureCatalogLoadedAsync(CancellationToken.None);
            await scanEntered.Task.WaitAsync(Timeout);
            var second = store.EnsureCatalogLoadedAsync(CancellationToken.None);

            Assert.Equal(1, Volatile.Read(ref starts));
            Assert.Equal(1, maxActive);
            Assert.False(store.IsCatalogReady);
            Assert.False(first.IsCompleted);
            Assert.False(second.IsCompleted);

            allowScan.TrySetResult();
            await Task.WhenAll(first, second).WaitAsync(Timeout);

            Assert.Equal(1, Volatile.Read(ref starts));
            Assert.Equal(1, maxActive);
            Assert.True(store.IsCatalogReady);
            Assert.Equal(1, store.EntryCount);
            Assert.Equal(content.Length, store.CurrentBytes);
        }
        finally
        {
            allowScan.TrySetResult();
            DeleteDir(dir);
        }
    }

    [Fact]
    public async Task ConcurrentCommit_NewHashSurvivesPublication()
    {
        var dir = NewTempDir("catalog-live-unique");
        var captured = NewTcs();
        var allowPublish = NewTcs();
        var live = "live-unique"u8.ToArray();

        try
        {
            var store = new RepairPatchStore(dir, 1024 * 1024, ct =>
                WaitThenComplete(captured, allowPublish, ct));

            var load = store.EnsureCatalogLoadedAsync(CancellationToken.None);
            await captured.Task.WaitAsync(Timeout);
            Assert.False(store.IsCatalogReady);

            store.CommitPatch("live-unique@test", live, Header(live.Length));
            allowPublish.TrySetResult();
            await load.WaitAsync(Timeout);

            Assert.True(store.IsCatalogReady);
            Assert.True(store.Contains("live-unique@test"));
            Assert.Equal(1, store.EntryCount);
            Assert.Equal(live.Length, store.CurrentBytes);
        }
        finally
        {
            allowPublish.TrySetResult();
            DeleteDir(dir);
        }
    }

    [Fact]
    public async Task ConcurrentCommit_SameHashLiveSizeWins()
    {
        var dir = NewTempDir("catalog-live-same");
        var captured = NewTcs();
        var allowPublish = NewTcs();
        var scanned = new byte[10];
        var live = new byte[20];
        var blob = WriteBlob(dir, "same-hash@test", scanned);

        try
        {
            var store = new RepairPatchStore(dir, 1024 * 1024, ct =>
                YieldThenWait(blob, captured, allowPublish, ct));

            var load = store.EnsureCatalogLoadedAsync(CancellationToken.None);
            await captured.Task.WaitAsync(Timeout);
            Assert.False(store.IsCatalogReady);

            store.CommitPatch("same-hash@test", live, Header(live.Length));
            allowPublish.TrySetResult();
            await load.WaitAsync(Timeout);

            Assert.True(store.IsCatalogReady);
            Assert.True(store.Contains("same-hash@test"));
            Assert.True(store.IsRepaired("same-hash@test", live.Length));
            Assert.Equal(1, store.EntryCount);
            Assert.Equal(live.Length, store.CurrentBytes);
        }
        finally
        {
            allowPublish.TrySetResult();
            DeleteDir(dir);
        }
    }

    [Fact]
    public async Task CatalogScan_PreservesLiveTempFile()
    {
        var dir = NewTempDir("live-tmp");
        try
        {
            var liveTmp = Path.Join(dir, "live.tmp");
            Directory.CreateDirectory(dir);
            File.WriteAllText(liveTmp, "staging");
            var store = new RepairPatchStore(dir, 1024 * 1024, _ => [liveTmp]);

            await store.EnsureCatalogLoadedAsync(CancellationToken.None);

            Assert.True(File.Exists(liveTmp));
            Assert.True(store.IsCatalogReady);
            Assert.Equal(0, store.EntryCount);
        }
        finally
        {
            DeleteDir(dir);
        }
    }

    [Fact]
    public async Task CatalogScan_DeletesStaleTempFile()
    {
        var dir = NewTempDir("stale-tmp");
        try
        {
            var staleTmp = Path.Join(dir, "stale.tmp");
            Directory.CreateDirectory(dir);
            File.WriteAllText(staleTmp, "orphan");
            File.SetLastWriteTimeUtc(staleTmp, DateTime.UtcNow - TimeSpan.FromHours(2));
            var store = new RepairPatchStore(dir, 1024 * 1024, _ => [staleTmp]);

            await store.EnsureCatalogLoadedAsync(CancellationToken.None);

            Assert.False(File.Exists(staleTmp));
            Assert.True(store.IsCatalogReady);
            Assert.Equal(0, store.EntryCount);
        }
        finally
        {
            DeleteDir(dir);
        }
    }

    private static void AssertUnpublished(RepairPatchStore store)
    {
        Assert.False(store.IsCatalogReady);
        Assert.Equal(0, store.EntryCount);
        Assert.Equal(0, store.CurrentBytes);
    }

    private static string NewTempDir(string prefix) =>
        Path.Join(Path.GetTempPath(), $"nzbdav-repair-{prefix}-" + Guid.NewGuid().ToString("N"));

    private static void DeleteDir(string dir)
    {
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
    }

    private static TaskCompletionSource NewTcs() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static string WriteBlob(string dir, string segmentId, byte[] content)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(segmentId)));
        var path = Path.Join(dir, hash[..2], hash);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
        return path;
    }

    private static IEnumerable<string> YieldOneThenThrow(
        string file,
        Exception failure,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        yield return file;
        throw failure;
    }

    private static IEnumerable<string> YieldOneThenWait(
        string file,
        TaskCompletionSource scanEntered,
        ManualResetEventSlim releaseScan,
        TaskCompletionSource scanExited,
        CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            yield return file;
            scanEntered.TrySetResult();
            releaseScan.Wait(ct);
        }
        finally
        {
            scanExited.TrySetResult();
        }
    }

    private static IEnumerable<string> YieldThenWait(
        string file,
        TaskCompletionSource captured,
        TaskCompletionSource allowPublish,
        CancellationToken ct)
    {
        yield return file;
        captured.TrySetResult();
        allowPublish.Task.Wait(ct);
    }

    private static IEnumerable<string> WaitThenComplete(
        TaskCompletionSource captured,
        TaskCompletionSource allowPublish,
        CancellationToken ct)
    {
        captured.TrySetResult();
        allowPublish.Task.Wait(ct);
        yield break;
    }
}
