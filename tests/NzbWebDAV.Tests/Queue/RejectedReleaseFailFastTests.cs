using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Interceptors;
using NzbWebDAV.Database.MigrationHelpers;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Queue;

namespace NzbWebDAV.Tests.Queue;

public sealed class RejectedReleaseFailFastTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 27, 12, 0, 0, TimeSpan.Zero);

    private readonly string _databasePath =
        Path.Join(Path.GetTempPath(), $"nzbdav-rejected-release-{Guid.NewGuid():N}.sqlite");
    private DavDatabaseContext _context = null!;
    private DavDatabaseClient _dbClient = null!;

    public async Task InitializeAsync()
    {
        _context = new DavDatabaseContext(CreateOptions());
        await _context.Database.MigrateAsync();
        _dbClient = new DavDatabaseClient(_context);
    }

    public async Task DisposeAsync()
    {
        await _context.DisposeAsync();
        try { File.Delete(_databasePath); } catch (IOException) { /* best effort */ }
    }

    private DbContextOptions<DavDatabaseContext> CreateOptions() =>
        new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite($"Data Source={_databasePath}")
            .AddInterceptors(new SqliteForeignKeyEnabler())
            .ReplaceService<
                IMigrationsSqlGenerator,
                SqliteMigrationsSqlGenerator<SqliteMigrationsSqlGenerator>>()
            .Options;

    private async Task ReopenContextAsync()
    {
        await _context.DisposeAsync();
        _context = new DavDatabaseContext(CreateOptions());
        await _context.Database.MigrateAsync();
        _dbClient = new DavDatabaseClient(_context);
    }

    private async Task SeedRejectionAsync(
        HealthCheckResult.RepairAction action,
        DateTimeOffset createdAt,
        string? nzbFileName,
        string? jobName)
    {
        _context.HealthCheckResults.Add(new HealthCheckResult
        {
            Id = Guid.NewGuid(),
            CreatedAt = createdAt,
            DavItemId = Guid.NewGuid(),
            Path = "/content/release.mkv",
            NzbFileName = nzbFileName,
            JobName = jobName,
            Result = HealthCheckResult.HealthResult.Unhealthy,
            RepairStatus = action,
            Message = "Repair removed and blocklisted the release.",
        });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }

    private static QueueItem NewQueueItem(
        string fileName = "release.nzb",
        string jobName = "release") =>
        new()
        {
            Id = Guid.NewGuid(),
            CreatedAt = Now.UtcDateTime,
            FileName = fileName,
            JobName = jobName,
            Category = "tv",
        };

    private Task AssertThrowsAsync(QueueItem queueItem)
    {
        return Assert.ThrowsAsync<NonRetryableDownloadException>(
            () => QueueItemProcessor.ThrowIfRecentlyRejectedAsync(
                _dbClient, queueItem, Now, CancellationToken.None));
    }

    private Task AssertDoesNotThrowAsync(QueueItem queueItem) =>
        QueueItemProcessor.ThrowIfRecentlyRejectedAsync(
            _dbClient, queueItem, Now, CancellationToken.None);

    [Fact]
    public async Task RecentRepairedWithExactNzbFileName_Throws()
    {
        await SeedRejectionAsync(
            HealthCheckResult.RepairAction.Repaired,
            Now.AddDays(-1),
            "release.nzb",
            "release");

        var exception = await Assert.ThrowsAsync<NonRetryableDownloadException>(
            () => QueueItemProcessor.ThrowIfRecentlyRejectedAsync(
                _dbClient, NewQueueItem(), Now, CancellationToken.None));

        Assert.Contains("removed and blocklisted", exception.Message);
        Assert.Contains("14 days", exception.Message);
    }

    [Fact]
    public async Task RecentRepaired_StillThrowsAfterContextReopen()
    {
        await SeedRejectionAsync(
            HealthCheckResult.RepairAction.Repaired,
            Now.AddDays(-1),
            "release.nzb",
            "release");
        await ReopenContextAsync();

        await AssertThrowsAsync(NewQueueItem());
    }

    [Fact]
    public async Task RowExactlyAtCutoff_Throws()
    {
        await SeedRejectionAsync(
            HealthCheckResult.RepairAction.Repaired,
            Now - QueueItemProcessor.RejectedReleaseRecheckWindow,
            "release.nzb",
            "release");

        await AssertThrowsAsync(NewQueueItem());
    }

    [Fact]
    public async Task RowOneSecondOlderThanCutoff_DoesNotThrow()
    {
        await SeedRejectionAsync(
            HealthCheckResult.RepairAction.Repaired,
            Now - QueueItemProcessor.RejectedReleaseRecheckWindow - TimeSpan.FromSeconds(1),
            "release.nzb",
            "release");

        await AssertDoesNotThrowAsync(NewQueueItem());
    }

    [Fact]
    public async Task RecentDeleted_DoesNotThrow()
    {
        await SeedRejectionAsync(
            HealthCheckResult.RepairAction.Deleted,
            Now.AddDays(-1),
            "release.nzb",
            "release");

        await AssertDoesNotThrowAsync(NewQueueItem());
    }

    [Fact]
    public async Task RecentRepairedViaPar2_DoesNotThrow()
    {
        await SeedRejectionAsync(
            HealthCheckResult.RepairAction.RepairedViaPar2,
            Now.AddDays(-1),
            "release.nzb",
            "release");

        await AssertDoesNotThrowAsync(NewQueueItem());
    }

    [Fact]
    public async Task DifferentNzbFileNameWithSameJobName_DoesNotThrow()
    {
        await SeedRejectionAsync(
            HealthCheckResult.RepairAction.Repaired,
            Now.AddDays(-1),
            "other-release.nzb",
            "release");

        await AssertDoesNotThrowAsync(NewQueueItem());
    }

    [Fact]
    public async Task NullNzbFileNameWithMatchingJobName_Throws()
    {
        await SeedRejectionAsync(
            HealthCheckResult.RepairAction.Repaired,
            Now.AddDays(-1),
            nzbFileName: null,
            jobName: "release");

        await AssertThrowsAsync(NewQueueItem());
    }

    [Fact]
    public async Task BothPersistedIdentitiesNull_DoesNotThrow()
    {
        await SeedRejectionAsync(
            HealthCheckResult.RepairAction.Repaired,
            Now.AddDays(-1),
            nzbFileName: null,
            jobName: null);

        await AssertDoesNotThrowAsync(NewQueueItem());
    }

    [Fact]
    public async Task NullQueueFileName_DoesNotMatchNullPersistedNzbFileName()
    {
        await SeedRejectionAsync(
            HealthCheckResult.RepairAction.Repaired,
            Now.AddDays(-1),
            nzbFileName: null,
            jobName: "other-release");
        var queueItem = NewQueueItem(jobName: "queue-release");
        queueItem.FileName = null!;

        await AssertDoesNotThrowAsync(queueItem);
    }

    [Fact]
    public async Task DifferentReleaseIdentity_DoesNotThrow()
    {
        await SeedRejectionAsync(
            HealthCheckResult.RepairAction.Repaired,
            Now.AddDays(-1),
            "other-release.nzb",
            "other-release");

        await AssertDoesNotThrowAsync(NewQueueItem());
    }
}
