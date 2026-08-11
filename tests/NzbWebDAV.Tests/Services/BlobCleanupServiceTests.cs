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
public sealed class BlobCleanupServiceTests : IAsyncLifetime
{
    private readonly string _configRoot =
        Path.Join(Path.GetTempPath(), $"nzbdav-blob-cleanup-cfg-{Guid.NewGuid():N}");
    private readonly string _databasePath =
        Path.Join(Path.GetTempPath(), $"nzbdav-blob-cleanup-{Guid.NewGuid():N}.sqlite");
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
        try { File.Delete(_databasePath); } catch (IOException) { /* best effort */ }
        try { Directory.Delete(_configRoot, recursive: true); } catch (IOException) { /* best effort */ }
    }

    [Fact]
    public async Task UnreferencedBlob_IsDeletedAndDequeued()
    {
        var blobId = Guid.NewGuid();
        await using (Stream stream = new MemoryStream("payload"u8.ToArray()))
            await BlobStore.WriteBlob(blobId, stream);
        _context.BlobCleanupItems.Add(new BlobCleanupItem { Id = blobId });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var processed = await BlobCleanupService.ProcessNextCleanupItemAsync(_context, CancellationToken.None);

        Assert.True(processed);
        Assert.Null(BlobStore.ReadBlob(blobId));
        Assert.Empty(await _context.BlobCleanupItems.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task ReferencedBlob_IsKeptAndDequeued()
    {
        // A queued cleanup id whose blob was re-attached to a live DavItem
        // (e.g. a restore/reimport) must not lose the live payload.
        var blobId = Guid.NewGuid();
        await using (Stream stream = new MemoryStream("payload"u8.ToArray()))
            await BlobStore.WriteBlob(blobId, stream);
        var davItem = DavItem.New(
            Guid.NewGuid(), DavItem.ContentFolder, $"live-{blobId:N}.mkv", 100,
            DavItem.ItemType.UsenetFile, DavItem.ItemSubType.NzbFile,
            releaseDate: null, lastHealthCheck: null, historyItemId: null,
            fileBlobId: blobId);
        _context.Items.Add(davItem);
        _context.BlobCleanupItems.Add(new BlobCleanupItem { Id = blobId });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var processed = await BlobCleanupService.ProcessNextCleanupItemAsync(_context, CancellationToken.None);

        Assert.True(processed);
        Assert.NotNull(BlobStore.ReadBlob(blobId));
        Assert.Empty(await _context.BlobCleanupItems.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task EmptyQueue_ReportsNothingToDo()
    {
        var processed = await BlobCleanupService.ProcessNextCleanupItemAsync(_context, CancellationToken.None);

        Assert.False(processed);
    }
}
