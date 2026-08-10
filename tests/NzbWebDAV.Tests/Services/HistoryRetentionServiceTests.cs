using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Interceptors;
using NzbWebDAV.Database.MigrationHelpers;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;

namespace NzbWebDAV.Tests.Services;

public sealed class HistoryRetentionServiceTests : IAsyncLifetime
{
    private readonly string _databasePath =
        Path.Join(Path.GetTempPath(), $"nzbdav-history-retention-{Guid.NewGuid():N}.sqlite");
    private DavDatabaseContext _context = null!;
    private DavDatabaseClient _client = null!;

    public async Task InitializeAsync()
    {
        var options = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite($"Data Source={_databasePath}")
            .AddInterceptors(new SqliteForeignKeyEnabler())
            .ReplaceService<
                IMigrationsSqlGenerator,
                SqliteMigrationsSqlGenerator<SqliteMigrationsSqlGenerator>>()
            .Options;
        _context = new DavDatabaseContext(options);
        await _context.Database.MigrateAsync();
        _client = new DavDatabaseClient(_context);
    }

    public async Task DisposeAsync()
    {
        await _context.DisposeAsync();
        try { File.Delete(_databasePath); } catch (IOException) { /* best effort */ }
    }

    [Fact]
    public async Task SweepAsync_RemovesOldHistory_PreservesDavItems()
    {
        var oldHistoryId = Guid.NewGuid();
        var recentHistoryId = Guid.NewGuid();
        var davItemId = Guid.NewGuid();

        _context.HistoryItems.AddRange(
            new HistoryItem
            {
                Id = oldHistoryId,
                CreatedAt = DateTime.UtcNow.AddDays(-120),
                FileName = "old.nzb",
                JobName = "old-job",
                Category = "movies",
                DownloadStatus = HistoryItem.DownloadStatusOption.Completed,
                TotalSegmentBytes = 100,
                DownloadTimeSeconds = 1,
            },
            new HistoryItem
            {
                Id = recentHistoryId,
                CreatedAt = DateTime.UtcNow.AddDays(-10),
                FileName = "recent.nzb",
                JobName = "recent-job",
                Category = "movies",
                DownloadStatus = HistoryItem.DownloadStatusOption.Completed,
                TotalSegmentBytes = 100,
                DownloadTimeSeconds = 1,
            });

        var davItem = DavItem.New(
            davItemId, DavItem.Root, "old.mkv", 100,
            DavItem.ItemType.UsenetFile, DavItem.ItemSubType.NzbFile,
            null, null, oldHistoryId, null);
        _context.Items.Add(davItem);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var removed = await HistoryRetentionService.SweepAsync(_client, retentionDays: 90, CancellationToken.None);

        Assert.Equal(1, removed);
        Assert.Null(await _context.HistoryItems.AsNoTracking().FirstOrDefaultAsync(x => x.Id == oldHistoryId));
        Assert.NotNull(await _context.HistoryItems.AsNoTracking().FirstOrDefaultAsync(x => x.Id == recentHistoryId));

        var cleanup = await _context.HistoryCleanupItems.AsNoTracking()
            .SingleAsync(x => x.Id == oldHistoryId);
        Assert.False(cleanup.DeleteMountedFiles);

        // Simulate HistoryCleanupService processing deleteFiles=false.
        await _context.Items
            .Where(x => x.HistoryItemId == oldHistoryId)
            .ExecuteUpdateAsync(x => x.SetProperty(p => p.HistoryItemId, (Guid?)null));
        await _context.HistoryCleanupItems
            .Where(x => x.Id == oldHistoryId)
            .ExecuteDeleteAsync();
        _context.ChangeTracker.Clear();

        var preserved = await _context.Items.AsNoTracking().SingleAsync(x => x.Id == davItemId);
        Assert.Null(preserved.HistoryItemId);
        Assert.Equal("old.mkv", preserved.Name);
    }

    [Fact]
    public async Task SweepAsync_WithZeroRetention_DeletesNothing()
    {
        _context.HistoryItems.Add(new HistoryItem
        {
            Id = Guid.NewGuid(),
            CreatedAt = DateTime.UtcNow.AddDays(-400),
            FileName = "ancient.nzb",
            JobName = "ancient",
            Category = "movies",
            DownloadStatus = HistoryItem.DownloadStatusOption.Completed,
            TotalSegmentBytes = 1,
            DownloadTimeSeconds = 1,
        });
        await _context.SaveChangesAsync();

        var removed = await HistoryRetentionService.SweepAsync(_client, retentionDays: 0, CancellationToken.None);

        Assert.Equal(0, removed);
        Assert.Equal(1, await _context.HistoryItems.CountAsync());
    }
}
