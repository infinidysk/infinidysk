using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Interceptors;
using NzbWebDAV.Database.MigrationHelpers;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;
using NzbWebDAV.Tests.Database;

namespace NzbWebDAV.Tests.Services;

[Collection(nameof(ConfigPathCollection))]
public sealed class NzbBlobCleanupServiceTests : IAsyncLifetime
{
    private readonly string _configRoot =
        Path.Join(Path.GetTempPath(), $"nzbdav-nzb-blob-cleanup-cfg-{Guid.NewGuid():N}");
    private readonly string _databasePath =
        Path.Join(Path.GetTempPath(), $"nzbdav-nzb-blob-cleanup-{Guid.NewGuid():N}.sqlite");
    private string? _previousConfigPath;
    private DavDatabaseContext _context = null!;

    public async Task InitializeAsync()
    {
        _previousConfigPath = Environment.GetEnvironmentVariable("CONFIG_PATH");
        Directory.CreateDirectory(_configRoot);
        Environment.SetEnvironmentVariable("CONFIG_PATH", _configRoot);

        var options = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite($"Data Source={_databasePath}")
            .AddInterceptors(new SqliteForeignKeyEnabler())
            .ReplaceService<
                IMigrationsSqlGenerator,
                SqliteMigrationsSqlGenerator<SqliteMigrationsSqlGenerator>>()
            .Options;
        _context = new DavDatabaseContext(options);
        await _context.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await _context.DisposeAsync();
        Environment.SetEnvironmentVariable("CONFIG_PATH", _previousConfigPath);
        try { File.Delete(_databasePath); } catch { /* best effort */ }
        try { Directory.Delete(_configRoot, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task QueueItemDelete_EnqueuesCleanup_AndRemovesBlobAndNzbName()
    {
        var blobId = Guid.NewGuid();
        await using (Stream stream = new MemoryStream("nzb-content"u8.ToArray()))
            await BlobStore.WriteBlob(blobId, stream);

        _context.QueueItems.Add(new QueueItem
        {
            Id = blobId,
            CreatedAt = DateTime.UtcNow,
            FileName = "show.nzb",
            JobName = "show",
            NzbFileSize = 10,
            TotalSegmentBytes = 10,
            Category = "tv",
            Priority = QueueItem.PriorityOption.Normal,
            PostProcessing = QueueItem.PostProcessingOption.None,
        });
        _context.NzbNames.Add(new NzbName { Id = blobId, FileName = "show.nzb" });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var queueItem = await _context.QueueItems.SingleAsync(x => x.Id == blobId);
        _context.QueueItems.Remove(queueItem);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        Assert.NotNull(await _context.NzbBlobCleanupItems.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == blobId));

        var processed = await NzbBlobCleanupService.ProcessNextCleanupItemAsync(_context, CancellationToken.None);
        Assert.True(processed);

        Assert.Null(BlobStore.ReadBlob(blobId));
        Assert.Null(await _context.NzbNames.AsNoTracking().SingleOrDefaultAsync(x => x.Id == blobId));
        Assert.Empty(await _context.NzbBlobCleanupItems.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Cleanup_SkipsBlobStillReferencedByDavItem()
    {
        var blobId = Guid.NewGuid();
        await using (Stream stream = new MemoryStream("nzb-content"u8.ToArray()))
            await BlobStore.WriteBlob(blobId, stream);

        _context.NzbNames.Add(new NzbName { Id = blobId, FileName = "mounted.nzb" });
        var davItem = DavItem.New(
            Guid.NewGuid(), DavItem.Root, "mounted.mkv", 100,
            DavItem.ItemType.UsenetFile, DavItem.ItemSubType.NzbFile,
            null, null, null, null, blobId);
        _context.Items.Add(davItem);
        _context.NzbBlobCleanupItems.Add(new NzbBlobCleanupItem { Id = blobId });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var processed = await NzbBlobCleanupService.ProcessNextCleanupItemAsync(_context, CancellationToken.None);
        Assert.True(processed);

        Assert.NotNull(BlobStore.ReadBlob(blobId));
        Assert.NotNull(await _context.NzbNames.AsNoTracking().SingleOrDefaultAsync(x => x.Id == blobId));
        Assert.Empty(await _context.NzbBlobCleanupItems.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Cleanup_RemovesOrphanNzbNameWithBlob()
    {
        var blobId = Guid.NewGuid();
        await using (Stream stream = new MemoryStream("nzb-content"u8.ToArray()))
            await BlobStore.WriteBlob(blobId, stream);

        _context.NzbNames.Add(new NzbName { Id = blobId, FileName = "orphan.nzb" });
        _context.NzbBlobCleanupItems.Add(new NzbBlobCleanupItem { Id = blobId });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        await NzbBlobCleanupService.ProcessNextCleanupItemAsync(_context, CancellationToken.None);

        Assert.Null(BlobStore.ReadBlob(blobId));
        Assert.Null(await _context.NzbNames.AsNoTracking().SingleOrDefaultAsync(x => x.Id == blobId));
    }
}
