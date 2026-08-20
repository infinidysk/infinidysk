using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Models;
using NzbWebDAV.Services.Repair;
using NzbWebDAV.Tests.Database;

namespace NzbWebDAV.Tests.Services.Repair;

[Collection(nameof(ConfigPathCollection))]
public sealed class DavNzbFileCorruptionRecordTests : IAsyncLifetime
{
    private readonly string _configRoot =
        Path.Join(Path.GetTempPath(), $"nzbdav-corrupt-record-{Guid.NewGuid():N}");
    private string? _previousConfigPath;
    private ConfigManager _config = null!;

    public Task InitializeAsync()
    {
        _previousConfigPath = Environment.GetEnvironmentVariable("CONFIG_PATH");
        Directory.CreateDirectory(_configRoot);
        Environment.SetEnvironmentVariable("CONFIG_PATH", _configRoot);
        DavDatabaseContext.ResetOptionsForTests();
        _config = new ConfigManager();
        _config.UpdateValues(
        [
            new ConfigItem { ConfigName = ConfigKeys.RepairEnable, ConfigValue = "true" },
        ]);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        Environment.SetEnvironmentVariable("CONFIG_PATH", _previousConfigPath);
        DavDatabaseContext.ResetOptionsForTests();
        try { Directory.Delete(_configRoot, recursive: true); } catch (IOException) { /* best effort */ }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Consumer_ResolvesAbsoluteIndexFromSegmentId()
    {
        var segments = new[] { "head@test", "mid@test", "tail@test", "last@test" };
        var (item, _) = await AddFileAsync(segments, missing: [1], containerClass: 2);
        // A post-seek stream sliced at "tail@test" would see relative index 0.
        var service = NewService();

        await service.ProcessCorruptionEventForTestsAsync(item.Path, segments[2], CancellationToken.None);

        var blob = await ReadCurrentBlobAsync(item.Id);
        Assert.Equal([2], blob.CorruptSegmentIndices!);
        Assert.Equal([1], blob.MissingSegmentIndices!);
        Assert.Equal((byte)2, blob.ContainerClass);
    }

    [Fact]
    public async Task Consumer_MergesCorruptIndicesWithoutDroppingExistingFields()
    {
        var segments = NewSegmentIds(4);
        var (item, _) = await AddFileAsync(segments, missing: [0, 3], containerClass: 1, criticalHead: 56);
        var service = NewService();

        await service.ProcessCorruptionEventForTestsAsync(item.Path, segments[2], CancellationToken.None);
        await service.ProcessCorruptionEventForTestsAsync(item.Path, segments[0], CancellationToken.None);

        var blob = await ReadCurrentBlobAsync(item.Id);
        Assert.Equal([0, 2], blob.CorruptSegmentIndices!);
        Assert.Equal([0, 3], blob.MissingSegmentIndices!);
        Assert.Equal((byte)1, blob.ContainerClass);
        Assert.Equal(56, blob.CriticalHeadEndExclusive);
    }

    [Fact]
    public async Task ConcurrentMutations_LoseNoBlobFields()
    {
        var segments = NewSegmentIds(4);
        var (item, _) = await AddFileAsync(segments, missing: null, containerClass: 3);
        var instanceA = ReloadTracked(item.Id);
        var instanceB = ReloadTracked(item.Id);

        await Task.WhenAll(
            DavNzbFileBlobUpdater.MutateAsync(instanceA, current =>
            {
                current.MissingSegmentIndices = [1];
                return current;
            }),
            DavNzbFileBlobUpdater.MutateAsync(instanceB, current =>
            {
                current.CorruptSegmentIndices = [2];
                return current;
            }));

        var blobA = await BlobStore.ReadBlob<DavNzbFile>(instanceA.FileBlobId!.Value);
        var blobB = await BlobStore.ReadBlob<DavNzbFile>(instanceB.FileBlobId!.Value);
        Assert.Contains(
            new[] { blobA!, blobB! },
            blob => blob.MissingSegmentIndices is [1]
                    && blob.CorruptSegmentIndices is [2]
                    && blob.ContainerClass == 3
                    && blob.SegmentIds.SequenceEqual(segments));
    }

    [Fact]
    public async Task Consumer_EnqueuesPar2OnlyWhenPar2Enabled()
    {
        var segments = NewSegmentIds(3);
        var (item, _) = await AddFileAsync(segments);

        _config.UpdateValues(
        [
            new ConfigItem { ConfigName = ConfigKeys.RepairEnable, ConfigValue = "true" },
            new ConfigItem { ConfigName = ConfigKeys.RepairPar2Enabled, ConfigValue = "false" },
        ]);
        var trackingOnly = new RecordingEnqueuePar2RepairService(_config, Path.Join(_configRoot, "patches-off"));
        await trackingOnly.ProcessCorruptionEventForTestsAsync(item.Path, segments[1], CancellationToken.None);
        Assert.Empty(trackingOnly.Enqueued);
        var trackingBlob = await ReadCurrentBlobAsync(item.Id);
        Assert.Equal([1], trackingBlob.CorruptSegmentIndices!);

        _config.UpdateValues(
        [
            new ConfigItem { ConfigName = ConfigKeys.RepairEnable, ConfigValue = "true" },
            new ConfigItem { ConfigName = ConfigKeys.RepairPar2Enabled, ConfigValue = "true" },
        ]);
        var withPar2 = new RecordingEnqueuePar2RepairService(_config, Path.Join(_configRoot, "patches-on"));
        await withPar2.ProcessCorruptionEventForTestsAsync(item.Path, segments[1], CancellationToken.None);
        Assert.Equal([segments[1]], Assert.Single(withPar2.Enqueued));
    }

    [Fact]
    public async Task HealthReplaceMutation_PreservesCorruptRecord()
    {
        var segments = NewSegmentIds(3);
        var (item, _) = await AddFileAsync(segments);
        await DavNzbFileBlobUpdater.MutateAsync(item, current =>
        {
            current.CorruptSegmentIndices = [2];
            return current;
        });

        await DavNzbFileBlobUpdater.MutateAsync(item, current =>
        {
            current.MissingSegmentIndices = [0];
            return current;
        });

        var blob = await BlobStore.ReadBlob<DavNzbFile>(item.FileBlobId!.Value);
        Assert.Equal([0], blob!.MissingSegmentIndices!);
        Assert.Equal([2], blob.CorruptSegmentIndices!);
    }

    private Par2RepairService NewService() =>
        new(_config, null!, new RepairPatchStore(Path.Join(_configRoot, "patches"), 1024 * 1024));

    private async Task<(DavItem Item, Guid BlobId)> AddFileAsync(
        string[] segmentIds,
        int[]? missing = null,
        byte? containerClass = null,
        long? criticalHead = null)
    {
        await using var context = new DavDatabaseContext();
        await context.Database.MigrateAsync();

        var itemId = Guid.NewGuid();
        var blobId = Guid.NewGuid();
        var sizes = Enumerable.Repeat(100L, segmentIds.Length).ToArray();
        var ranges = new LongRange[sizes.Length];
        long offset = 0;
        for (var i = 0; i < sizes.Length; i++)
        {
            ranges[i] = LongRange.FromStartAndSize(offset, sizes[i]);
            offset += sizes[i];
        }

        await BlobStore.WriteBlob(blobId, new DavNzbFile
        {
            Id = itemId,
            SegmentIds = segmentIds,
            SegmentByteRanges = ranges,
            MissingSegmentIndices = missing,
            ContainerClass = containerClass,
            CriticalHeadEndExclusive = criticalHead,
        });

        var item = DavItem.New(
            itemId,
            DavItem.ContentFolder,
            $"movie-{itemId:N}.mkv",
            fileSize: offset,
            DavItem.ItemType.UsenetFile,
            DavItem.ItemSubType.NzbFile,
            releaseDate: DateTimeOffset.UtcNow.AddDays(-1),
            lastHealthCheck: null,
            historyItemId: null,
            fileBlobId: blobId);
        context.Items.Add(item);
        await context.SaveChangesAsync();
        return (item, blobId);
    }

    private static async Task<DavNzbFile> ReadCurrentBlobAsync(Guid itemId)
    {
        await using var context = new DavDatabaseContext();
        var item = await context.Items.AsNoTracking().SingleAsync(x => x.Id == itemId);
        var blob = await BlobStore.ReadBlob<DavNzbFile>(item.FileBlobId!.Value);
        return blob!;
    }

    private static DavItem ReloadTracked(Guid itemId)
    {
        using var context = new DavDatabaseContext();
        return context.Items.AsNoTracking().Single(x => x.Id == itemId);
    }

    private static string[] NewSegmentIds(int count) =>
        Enumerable.Range(0, count).Select(i => $"seg{i}-{Guid.NewGuid():N}@test").ToArray();

    private sealed class RecordingEnqueuePar2RepairService(ConfigManager config, string patchDir)
        : Par2RepairService(config, null!, new RepairPatchStore(patchDir, 1024 * 1024))
    {
        public List<string[]> Enqueued { get; } = [];

        public override Task EnqueueAsync(
            DavItem davItem,
            IReadOnlyList<string> missingSegmentIds,
            CancellationToken ct = default)
        {
            Enqueued.Add(missingSegmentIds.ToArray());
            return Task.CompletedTask;
        }
    }
}
