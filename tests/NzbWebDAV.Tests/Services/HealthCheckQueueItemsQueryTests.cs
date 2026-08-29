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
    public async Task Query_ExcludesHistoryLinkedNonUrgent_AndIncludesUrgentForcedAndUnlinked()
    {
        var historyId = Guid.NewGuid();
        var scheduledAt = DateTimeOffset.UtcNow.AddHours(1);

        var historyLinkedNonUrgent = NewUsenetFile("history-linked-non-urgent.mkv", historyId, scheduledAt);
        var historyLinkedUnchecked = NewUsenetFile("history-linked-unchecked.mkv", historyId, null);
        var historyLinkedUrgent = NewUsenetFile("history-linked-urgent.mkv", historyId, DateTimeOffset.UnixEpoch);
        var historyLinkedForced = NewUsenetFile("history-linked-forced.mkv", historyId, HealthCheckService.ForcedRecheckSentinel);
        var unlinkedUrgent = NewUsenetFile("unlinked-urgent.mkv", null, DateTimeOffset.UnixEpoch);
        var unlinkedScheduled = NewUsenetFile("unlinked-scheduled.mkv", null, scheduledAt);

        _context.Items.AddRange(
            historyLinkedNonUrgent,
            historyLinkedUnchecked,
            historyLinkedUrgent,
            historyLinkedForced,
            unlinkedUrgent,
            unlinkedScheduled);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var ids = await HealthCheckService.GetHealthCheckQueueItemsQuery(_dbClient)
            .Select(x => x.Id)
            .ToListAsync();

        Assert.DoesNotContain(historyLinkedNonUrgent.Id, ids);
        Assert.DoesNotContain(historyLinkedUnchecked.Id, ids);
        Assert.Contains(historyLinkedUrgent.Id, ids);
        Assert.Contains(historyLinkedForced.Id, ids);
        Assert.Contains(unlinkedUrgent.Id, ids);
        Assert.Contains(unlinkedScheduled.Id, ids);
    }

    [Fact]
    public async Task Query_IncludesHistoryLinkedPendingRepairs()
    {
        var historyId = Guid.NewGuid();
        var deferredUntil = DateTimeOffset.UtcNow.AddHours(3);
        var pending = NewUsenetFile("history-linked-pending-repair.mkv", historyId, deferredUntil);
        pending.HealthRepairPending = true;
        _context.Items.Add(pending);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var ids = await HealthCheckService.GetHealthCheckQueueItemsQuery(_dbClient)
            .Select(x => x.Id)
            .ToListAsync();

        Assert.Contains(pending.Id, ids);
    }

    [Fact]
    public async Task CallerSideFilter_SkipsNonMediaFiles_ButKeepsRepairWork()
    {
        var scheduledAt = DateTimeOffset.UtcNow.AddHours(-1);

        var videoFile = NewUsenetFile("movie.mkv", null, scheduledAt);
        var audioFile = NewUsenetFile("track.flac", null, scheduledAt);
        var archiveFile = NewUsenetFile("archive.rar", null, scheduledAt);
        var imageFile = NewUsenetFile("cover.jpg", null, scheduledAt);
        var subtitleFile = NewUsenetFile("subs.srt", null, scheduledAt);
        var nfoFile = NewUsenetFile("info.nfo", null, scheduledAt);
        var urgentImage = NewUsenetFile("urgent-screenshot.jpg", null, DateTimeOffset.UnixEpoch);
        var pendingImage = NewUsenetFile(
            "pending-screenshot.jpg",
            null,
            DateTimeOffset.UtcNow.AddHours(1));
        pendingImage.HealthRepairPending = true;

        _context.Items.AddRange(
            videoFile,
            audioFile,
            archiveFile,
            imageFile,
            subtitleFile,
            nfoFile,
            urgentImage,
            pendingImage);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var currentDateTime = DateTimeOffset.UtcNow;

        // Mirror the production selector when both checks and repairs are admitted.
        var filtered = new List<DavItem>();
        await foreach (var item in HealthCheckService.GetHealthCheckQueueItems(_dbClient)
            .Where(x =>
                x.NextHealthCheck == null ||
                x.NextHealthCheck < currentDateTime ||
                x.HealthRepairPending)
            .AsAsyncEnumerable())
        {
            if (item.NextHealthCheck == DateTimeOffset.UnixEpoch ||
                item.HealthRepairPending ||
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
        Assert.Contains(pendingImage.Id, filtered.Select(x => x.Id));
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
        var forcedMedia = NewUsenetFile("forced-recheck.mkv", Guid.NewGuid(), HealthCheckService.ForcedRecheckSentinel);
        var forcedImage = NewUsenetFile("forced-cover.jpg", null, HealthCheckService.ForcedRecheckSentinel);
        var pendingMedia = NewUsenetFile("pending-repair.mkv", null, nextHealthCheck: null);
        pendingMedia.HealthRepairPending = true;

        _context.Items.AddRange(
            videoFile, audioFile, archiveFile, imageFile, subtitleFile, nfoFile, scheduledMedia,
            forcedMedia, forcedImage, pendingMedia);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        // Mirror GetHealthCheckQueueController uncheckedCount: never-checked and operator-forced
        // files that HealthCheckService will actually process.
        var uncheckedCount = (await HealthCheckService.GetHealthCheckQueueItemsQuery(_dbClient)
            .Where(x => !x.HealthRepairPending &&
                (x.NextHealthCheck == null ||
                 x.NextHealthCheck == HealthCheckService.ForcedRecheckSentinel))
            .Select(x => x.Name)
            .ToListAsync())
            .Count(FilenameUtil.IsHealthCheckCandidate);

        Assert.Equal(4, uncheckedCount);
    }

    [Fact]
    public async Task OrderedQuery_PrioritizesRepairsThenUncheckedForcedAndScheduledItems()
    {
        var historyId = Guid.NewGuid();
        var scheduledAt = DateTimeOffset.UtcNow.AddHours(-2);

        var historyLinkedUrgent = NewUsenetFile("urgent-first.mkv", historyId, DateTimeOffset.UnixEpoch);
        var pendingRepair = NewUsenetFile(
            "pending-second.mkv",
            historyId,
            DateTimeOffset.UtcNow.AddHours(2));
        pendingRepair.HealthRepairPending = true;
        var uncheckedItem = NewUsenetFile("unchecked-third.mkv", null, nextHealthCheck: null);
        var historyLinkedForced = NewUsenetFile("forced-fourth.mkv", historyId, HealthCheckService.ForcedRecheckSentinel);
        var unlinkedScheduled = NewUsenetFile("scheduled-fifth.mkv", null, scheduledAt);

        _context.Items.AddRange(
            historyLinkedUrgent,
            pendingRepair,
            uncheckedItem,
            historyLinkedForced,
            unlinkedScheduled);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var orderedIds = await HealthCheckService.GetHealthCheckQueueItems(_dbClient)
            .Select(x => x.Id)
            .ToListAsync();

        Assert.Equal(
            [
                historyLinkedUrgent.Id,
                pendingRepair.Id,
                uncheckedItem.Id,
                historyLinkedForced.Id,
                unlinkedScheduled.Id,
            ],
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
