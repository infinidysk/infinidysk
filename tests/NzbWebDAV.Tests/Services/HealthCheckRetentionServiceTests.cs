using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Interceptors;
using NzbWebDAV.Database.MigrationHelpers;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;

namespace NzbWebDAV.Tests.Services;

public sealed class HealthCheckRetentionServiceTests : IAsyncLifetime
{
    private readonly string _databasePath =
        Path.Join(Path.GetTempPath(), $"nzbdav-health-retention-{Guid.NewGuid():N}.sqlite");
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
    public async Task SweepAsync_DeletesOnlyRowsOlderThanRetention()
    {
        var oldResult = NewResult(DateTimeOffset.UtcNow.AddDays(-40));
        var recentResult = NewResult(DateTimeOffset.UtcNow.AddDays(-5));
        _context.HealthCheckResults.AddRange(oldResult, recentResult);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var deleted = await HealthCheckRetentionService.SweepAsync(_context, retentionDays: 30, CancellationToken.None);

        Assert.Equal(1, deleted);
        var remaining = await _context.HealthCheckResults.AsNoTracking().Select(x => x.Id).ToListAsync();
        Assert.Equal([recentResult.Id], remaining);
    }

    [Fact]
    public async Task SweepAsync_WithZeroRetention_DeletesNothing()
    {
        _context.HealthCheckResults.Add(NewResult(DateTimeOffset.UtcNow.AddDays(-400)));
        await _context.SaveChangesAsync();

        var deleted = await HealthCheckRetentionService.SweepAsync(_context, retentionDays: 0, CancellationToken.None);

        Assert.Equal(0, deleted);
        Assert.Equal(1, await _context.HealthCheckResults.CountAsync());
    }

    [Fact]
    public async Task Reset_ClearsResultsAndStats()
    {
        _context.HealthCheckResults.Add(NewResult(DateTimeOffset.UtcNow));
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        // Stats are populated by INSERT triggers when results are saved.
        Assert.True(await _context.HealthCheckStats.CountAsync() > 0);

        var deletedResults = await _context.HealthCheckResults.ExecuteDeleteAsync();
        var deletedStats = await _context.HealthCheckStats.ExecuteDeleteAsync();

        Assert.Equal(1, deletedResults);
        Assert.True(deletedStats >= 0);
        Assert.Equal(0, await _context.HealthCheckResults.CountAsync());
        Assert.Equal(0, await _context.HealthCheckStats.CountAsync());
    }

    private static HealthCheckResult NewResult(DateTimeOffset createdAt) => new()
    {
        Id = Guid.NewGuid(),
        CreatedAt = createdAt,
        DavItemId = Guid.NewGuid(),
        Path = "/content/example.mkv",
        Result = HealthCheckResult.HealthResult.Healthy,
        RepairStatus = HealthCheckResult.RepairAction.None,
        Message = null,
    };
}
