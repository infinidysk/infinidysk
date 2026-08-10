using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Interceptors;
using NzbWebDAV.Database.MigrationHelpers;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;

namespace NzbWebDAV.Tests.Services;

public sealed class HealthCheckQueueItemsQueryTests : IAsyncLifetime
{
    private readonly string _databasePath =
        Path.Join(Path.GetTempPath(), $"nzbdav-health-queue-{Guid.NewGuid():N}.sqlite");
    private DavDatabaseContext _context = null!;
    private DavDatabaseClient _dbClient = null!;

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
        _dbClient = new DavDatabaseClient(_context);
    }

    public async Task DisposeAsync()
    {
        await _context.DisposeAsync();
        try { File.Delete(_databasePath); } catch { /* best effort */ }
    }

    [Fact]
    public async Task Query_ExcludesHistoryLinkedNonUrgent_AndIncludesUrgentAndUnlinked()
    {
        var historyId = Guid.NewGuid();
        var scheduledAt = DateTimeOffset.UtcNow.AddHours(1);

        var historyLinkedNonUrgent = NewUsenetFile("history-linked-non-urgent.mkv", historyId, scheduledAt);
        var historyLinkedUrgent = NewUsenetFile("history-linked-urgent.mkv", historyId, DateTimeOffset.UnixEpoch);
        var unlinkedUrgent = NewUsenetFile("unlinked-urgent.mkv", null, DateTimeOffset.UnixEpoch);
        var unlinkedScheduled = NewUsenetFile("unlinked-scheduled.mkv", null, scheduledAt);

        _context.Items.AddRange(
            historyLinkedNonUrgent,
            historyLinkedUrgent,
            unlinkedUrgent,
            unlinkedScheduled);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var ids = await HealthCheckService.GetHealthCheckQueueItemsQuery(_dbClient)
            .Select(x => x.Id)
            .ToListAsync();

        Assert.DoesNotContain(historyLinkedNonUrgent.Id, ids);
        Assert.Contains(historyLinkedUrgent.Id, ids);
        Assert.Contains(unlinkedUrgent.Id, ids);
        Assert.Contains(unlinkedScheduled.Id, ids);
    }

    [Fact]
    public async Task OrderedQuery_PrioritizesUrgentThenUncheckedThenScheduledItems()
    {
        var historyId = Guid.NewGuid();
        var scheduledAt = DateTimeOffset.UtcNow.AddHours(-2);

        var historyLinkedUrgent = NewUsenetFile("urgent-first.mkv", historyId, DateTimeOffset.UnixEpoch);
        var uncheckedItem = NewUsenetFile("unchecked-second.mkv", null, nextHealthCheck: null);
        var unlinkedScheduled = NewUsenetFile("scheduled-third.mkv", null, scheduledAt);

        _context.Items.AddRange(historyLinkedUrgent, uncheckedItem, unlinkedScheduled);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var orderedIds = await HealthCheckService.GetHealthCheckQueueItems(_dbClient)
            .Select(x => x.Id)
            .ToListAsync();

        Assert.Equal(
            [historyLinkedUrgent.Id, uncheckedItem.Id, unlinkedScheduled.Id],
            orderedIds);
    }

    private static DavItem NewUsenetFile(
        string name,
        Guid? historyItemId,
        DateTimeOffset? nextHealthCheck)
    {
        var item = DavItem.New(
            Guid.NewGuid(),
            DavItem.ContentFolder,
            name,
            fileSize: 100,
            DavItem.ItemType.UsenetFile,
            DavItem.ItemSubType.NzbFile,
            releaseDate: DateTimeOffset.UtcNow.AddDays(-1),
            lastHealthCheck: null,
            historyItemId,
            fileBlobId: null);
        item.NextHealthCheck = nextHealthCheck;
        return item;
    }
}
