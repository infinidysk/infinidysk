using NzbWebDAV.Services.Repair;
using UsenetSharp.Models;

namespace NzbWebDAV.Tests.Services.Repair;

public sealed class RepairPatchStoreTests
{
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
            await store.CatalogLoadTask;

            store.CommitPatch(segmentId, content, Header(content.Length));

            Assert.True(store.IsRepaired(segmentId, content.Length));
            Assert.True(store.TryGet(segmentId, out var response));
            response!.Stream!.Dispose();

            var reloaded = new RepairPatchStore(dir, 1024 * 1024);
            await reloaded.CatalogLoadTask;
            Assert.True(reloaded.IsRepaired(segmentId, content.Length));
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
            await store.CatalogLoadTask;

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
}
