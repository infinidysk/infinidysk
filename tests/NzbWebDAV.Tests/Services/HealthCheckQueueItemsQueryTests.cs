using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Interceptors;
using NzbWebDAV.Database.MigrationHelpers;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;
using NzbWebDAV.Utils;

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
        try { File.Delete(_databasePath); } catch (IOException) { /* best effort */ }
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
    public async Task CallerSideFilter_SkipsNonMediaFiles_ButKeepsUrgent()
    {
        var scheduledAt = DateTimeOffset.UtcNow.AddHours(-1);

        var videoFile = NewUsenetFile("movie.mkv", null, scheduledAt);
        var audioFile = NewUsenetFile("track.flac", null, scheduledAt);
        var archiveFile = NewUsenetFile("archive.rar", null, scheduledAt);
        var imageFile = NewUsenetFile("cover.jpg", null, scheduledAt);
        var subtitleFile = NewUsenetFile("subs.srt", null, scheduledAt);
        var nfoFile = NewUsenetFile("info.nfo", null, scheduledAt);
        var urgentImage = NewUsenetFile("urgent-screenshot.jpg", null, DateTimeOffset.UnixEpoch);

        _context.Items.AddRange(videoFile, audioFile, archiveFile, imageFile, subtitleFile, nfoFile, urgentImage);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var currentDateTime = DateTimeOffset.UtcNow;

        // Mirror the streaming filter used in HealthCheckService.ExecuteAsync
        var filtered = new List<DavItem>();
        await foreach (var item in HealthCheckService.GetHealthCheckQueueItems(_dbClient)
            .Where(x => x.NextHealthCheck == null || x.NextHealthCheck < currentDateTime)
            .AsAsyncEnumerable())
        {
            if (item.NextHealthCheck == DateTimeOffset.UnixEpoch ||
                FilenameUtil.IsHealthCheckCandidate(item.Name))
            {
                filtered.Add(item);
            }
        }

        Assert.Contains(videoFile.Id, filtered.Select(x => x.Id));
        Assert.Contains(audioFile.Id, filtered.Select(x => x.Id));
        Assert.Contains(archiveFile.Id, filtered.Select(x => x.Id));
        Assert.DoesNotContain(imageFile.Id, filtered.Select(x => x.Id));
        Assert.DoesNotContain(subtitleFile.Id, filtered.Select(x => x.Id));
        Assert.DoesNotContain(nfoFile.Id, filtered.Select(x => x.Id));
        Assert.Contains(urgentImage.Id, filtered.Select(x => x.Id));
    }

    [Fact]
    public async Task UncheckedCount_ExcludesNonMediaFiles()
    {
        var videoFile = NewUsenetFile("movie.mkv", null, nextHealthCheck: null);
        var audioFile = NewUsenetFile("track.flac", null, nextHealthCheck: null);
        var archiveFile = NewUsenetFile("archive.rar", null, nextHealthCheck: null);
        var imageFile = NewUsenetFile("cover.jpg", null, nextHealthCheck: null);
        var subtitleFile = NewUsenetFile("subs.srt", null, nextHealthCheck: null);
        var nfoFile = NewUsenetFile("info.nfo", null, nextHealthCheck: null);
        var scheduledMedia = NewUsenetFile("already-checked.mkv", null, DateTimeOffset.UtcNow.AddHours(1));

        _context.Items.AddRange(
            videoFile, audioFile, archiveFile, imageFile, subtitleFile, nfoFile, scheduledMedia);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        // Mirror GetHealthCheckQueueController uncheckedCount: never-checked files that
        // HealthCheckService will actually process.
        var uncheckedCount = (await HealthCheckService.GetHealthCheckQueueItemsQuery(_dbClient)
            .Where(x => x.NextHealthCheck == null)
            .Select(x => x.Name)
            .ToListAsync())
            .Count(FilenameUtil.IsHealthCheckCandidate);

        Assert.Equal(3, uncheckedCount);
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
