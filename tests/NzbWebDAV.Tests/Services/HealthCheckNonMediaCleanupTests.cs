using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Interceptors;
using NzbWebDAV.Database.MigrationHelpers;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;

namespace NzbWebDAV.Tests.Services;

public sealed class HealthCheckNonMediaCleanupTests : IAsyncLifetime
{
    private readonly string _databasePath =
        Path.Join(Path.GetTempPath(), $"nzbdav-health-cleanup-{Guid.NewGuid():N}.sqlite");
    private DavDatabaseContext _context = null!;

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
    }

    public async Task DisposeAsync()
    {
        await _context.DisposeAsync();
        try { File.Delete(_databasePath); } catch (IOException) { /* best effort */ }
    }

    [Fact]
    public async Task Cleanup_ClearsScheduledNonMedia_AndPreservesMediaUrgentAndUncheckedFiles()
    {
        var scheduledAt = DateTimeOffset.UtcNow.AddHours(1);
        var media = NewUsenetFile("movie.mkv", scheduledAt);
        var subtitle = NewUsenetFile("movie.srt", scheduledAt);
        var nfo = NewUsenetFile("movie.nfo", scheduledAt);
        var urgentImage = NewUsenetFile("cover.jpg", DateTimeOffset.UnixEpoch);
        urgentImage.LastHealthCheck = scheduledAt;
        var uncheckedMedia = NewUsenetFile("unseen.mkv", null);

        _context.Items.AddRange(media, subtitle, nfo, urgentImage, uncheckedMedia);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await HealthCheckService.ClearNonMediaHealthCheckEntries(_context, cancellation.Token);

        var items = await _context.Items.ToDictionaryAsync(x => x.Id);

        AssertScheduled(items[media.Id].NextHealthCheck, scheduledAt);
        AssertScheduled(items[media.Id].LastHealthCheck, scheduledAt);
        Assert.Null(items[subtitle.Id].NextHealthCheck);
        Assert.Null(items[subtitle.Id].LastHealthCheck);
        Assert.Null(items[nfo.Id].NextHealthCheck);
        Assert.Null(items[nfo.Id].LastHealthCheck);
        Assert.Equal(DateTimeOffset.UnixEpoch, items[urgentImage.Id].NextHealthCheck);
        AssertScheduled(items[urgentImage.Id].LastHealthCheck, scheduledAt);
        Assert.Null(items[uncheckedMedia.Id].NextHealthCheck);
        Assert.Null(items[uncheckedMedia.Id].LastHealthCheck);
    }

    [Fact]
    public async Task Cleanup_AdvancesPastScheduledMediaAcrossBatches()
    {
        var scheduledAt = DateTimeOffset.UtcNow.AddHours(1);
        var items = Enumerable.Range(0, 1205)
            .Select(index => NewUsenetFile(
                index % 5 == 0 ? $"metadata-{index}.nfo" : $"movie-{index}.mkv",
                scheduledAt))
            .ToList();

        _context.Items.AddRange(items);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await HealthCheckService.ClearNonMediaHealthCheckEntries(_context, cancellation.Token);

        var persistedItems = await _context.Items.ToDictionaryAsync(x => x.Id);
        foreach (var item in items)
        {
            var persisted = persistedItems[item.Id];
            if (item.Name.EndsWith(".nfo", StringComparison.Ordinal))
            {
                Assert.Null(persisted.NextHealthCheck);
                Assert.Null(persisted.LastHealthCheck);
            }
            else
            {
                AssertScheduled(persisted.NextHealthCheck, scheduledAt);
                AssertScheduled(persisted.LastHealthCheck, scheduledAt);
            }
        }
    }

    private static DavItem NewUsenetFile(string name, DateTimeOffset? nextHealthCheck)
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
            historyItemId: null,
            fileBlobId: null);
        item.NextHealthCheck = nextHealthCheck;
        item.LastHealthCheck = nextHealthCheck is { } && nextHealthCheck != DateTimeOffset.UnixEpoch
            ? nextHealthCheck
            : null;
        return item;
    }

    private static void AssertScheduled(DateTimeOffset? actual, DateTimeOffset expected)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected.ToUnixTimeSeconds(), actual.Value.ToUnixTimeSeconds());
    }
}
