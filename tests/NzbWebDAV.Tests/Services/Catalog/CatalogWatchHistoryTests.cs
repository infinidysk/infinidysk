using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Interceptors;
using NzbWebDAV.Database.MigrationHelpers;
using NzbWebDAV.Services.Catalog;

namespace NzbWebDAV.Tests.Services.Catalog;

/// <summary>
/// Watch-history behavior: one resumable row per title/episode, appointment with the
/// partial unique index across re-watches, page bounds, and age-based retention.
/// </summary>
public sealed class CatalogWatchHistoryTests : IAsyncLifetime
{
    private readonly string _databasePath =
        Path.Join(Path.GetTempPath(), $"watch-history-{Guid.NewGuid():N}.sqlite");
    private readonly MutableTimeProvider _clock = new(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));
    private DbContextOptions<DavDatabaseContext> _options = null!;
    private DavDatabaseContext _context = null!;
    private CatalogWatchHistoryService _service = null!;

    public async Task InitializeAsync()
    {
        _options = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite($"Data Source={_databasePath}")
            .AddInterceptors(new SqliteForeignKeyEnabler())
            .ReplaceService<IMigrationsSqlGenerator, SqliteMigrationsSqlGenerator<SqliteMigrationsSqlGenerator>>()
            .Options;
        _context = new DavDatabaseContext(_options);
        await _context.Database.MigrateAsync();
        _service = new CatalogWatchHistoryService(() => new DavDatabaseContext(_options), _clock);
    }

    public async Task DisposeAsync()
    {
        await _context.DisposeAsync();
        File.Delete(_databasePath);
    }

    [Fact]
    public async Task Reports_UpsertToOneResumableRowAndKeepTheFirstStart()
    {
        var startedAt = _clock.GetUtcNow().ToUnixTimeSeconds();

        await _service.ReportProgressAsync(Movie(10378, position: 0, duration: 600), CancellationToken.None);
        _clock.Advance(TimeSpan.FromSeconds(30));
        await _service.ReportProgressAsync(Movie(10378, position: 15, duration: 600), CancellationToken.None);
        _clock.Advance(TimeSpan.FromSeconds(30));
        await _service.ReportProgressAsync(Movie(10378, position: 30, duration: 600), CancellationToken.None);

        var row = Assert.Single(await _service.ListAsync(1, 20, CancellationToken.None));
        Assert.Equal("Big Buck Bunny", row.Title);
        Assert.Equal(30, row.PositionSeconds);
        Assert.Equal(600, row.DurationSeconds);
        Assert.Equal(startedAt, row.StartedAtEpoch);
        Assert.Equal(_clock.GetUtcNow().ToUnixTimeSeconds(), row.UpdatedAtEpoch);
        Assert.False(row.Finished);
    }

    [Fact]
    public async Task Episodes_DedupePerEpisodeAndKeepSeparateRows()
    {
        await _service.ReportProgressAsync(Series(1930, season: 1, episode: 1, position: 60), CancellationToken.None);
        await _service.ReportProgressAsync(Series(1930, season: 1, episode: 2, position: 10), CancellationToken.None);
        await _service.ReportProgressAsync(Series(1930, season: 1, episode: 1, position: 120), CancellationToken.None);

        var rows = await _service.ListAsync(1, 20, CancellationToken.None);

        Assert.Equal(2, rows.Count);
        Assert.Equal([1, 2], rows.Select(row => row.Episode));
        Assert.Equal(120, rows.Single(row => row.Episode == 1).PositionSeconds);
    }

    [Fact]
    public async Task FinishedReport_ThenReplay_LeavesOneLiveRow()
    {
        await _service.ReportProgressAsync(Movie(10378, position: 300, duration: 600), CancellationToken.None);
        _clock.Advance(TimeSpan.FromSeconds(60));
        await _service.ReportProgressAsync(
            Movie(10378, position: 600, duration: 600, finished: true), CancellationToken.None);

        var finished = Assert.Single(await _service.ListAsync(1, 20, CancellationToken.None));
        Assert.True(finished.Finished);
        Assert.Equal(600, finished.PositionSeconds);

        _clock.Advance(TimeSpan.FromSeconds(60));
        await _service.ReportProgressAsync(Movie(10378, position: 5, duration: 600), CancellationToken.None);

        // The superseded finished row leaves with the new watch, so Continue Watching
        // never shows a stale "Watched" card next to the live one.
        var row = Assert.Single(await _service.ListAsync(1, 20, CancellationToken.None));
        Assert.Equal(5, row.PositionSeconds);
        Assert.False(row.Finished);
    }

    [Fact]
    public async Task List_PutsFinishedRowsBehindResumeCandidates()
    {
        await _service.ReportProgressAsync(Movie(1, position: 600, duration: 600, finished: true), CancellationToken.None);
        _clock.Advance(TimeSpan.FromSeconds(60));
        await _service.ReportProgressAsync(Movie(2, position: 5, duration: 600), CancellationToken.None);

        var rows = await _service.ListAsync(1, 20, CancellationToken.None);

        Assert.Equal(new[] { 2, 1 }, rows.Select(row => row.TmdbId));
        Assert.False(rows[0].Finished);
        Assert.True(rows[1].Finished);
    }

    [Fact]
    public async Task Remove_DeletesOnlyTheRequestedRow()
    {
        await _service.ReportProgressAsync(Movie(1, position: 10, duration: 600), CancellationToken.None);
        _clock.Advance(TimeSpan.FromSeconds(1));
        await _service.ReportProgressAsync(Movie(2, position: 10, duration: 600), CancellationToken.None);
        var target = (await _service.ListAsync(1, 20, CancellationToken.None)).Single(row => row.TmdbId == 2);

        Assert.True(await _service.RemoveAsync(1, target.Id, CancellationToken.None));

        var remaining = Assert.Single(await _service.ListAsync(1, 20, CancellationToken.None));
        Assert.Equal(1, remaining.TmdbId);
        Assert.False(await _service.RemoveAsync(1, target.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Profiles_KeepIndependentResumeRows()
    {
        await _service.ReportProgressAsync(
            Movie(10378, position: 10, duration: 600) with { ProfileId = 1 }, CancellationToken.None);
        await _service.ReportProgressAsync(
            Movie(10378, position: 200, duration: 600) with { ProfileId = 2 }, CancellationToken.None);

        // The same movie reported by two profiles is two rows: the resume index is
        // profile-scoped, so one profile's progress never overwrites the other's.
        var profileOne = Assert.Single(await _service.ListAsync(1, 20, CancellationToken.None));
        var profileTwo = Assert.Single(await _service.ListAsync(2, 20, CancellationToken.None));
        Assert.Equal(10, profileOne.PositionSeconds);
        Assert.Equal(200, profileTwo.PositionSeconds);

        Assert.False(await _service.RemoveAsync(2, profileOne.Id, CancellationToken.None));
        Assert.True(await _service.RemoveAsync(2, profileTwo.Id, CancellationToken.None));
        Assert.Single(await _service.ListAsync(1, 20, CancellationToken.None));
        Assert.Empty(await _service.ListAsync(2, 20, CancellationToken.None));
    }

    [Fact]
    public async Task List_ReturnsTheNewestRowsAndClampsThePageSize()
    {
        for (var id = 1; id <= 45; id++)
        {
            _clock.Advance(TimeSpan.FromSeconds(1));
            await _service.ReportProgressAsync(Movie(id, position: 10, duration: 600), CancellationToken.None);
        }

        var page = await _service.ListAsync(1, 40, CancellationToken.None);
        Assert.Equal(40, page.Count);
        Assert.Equal(45, page[0].TmdbId);
        Assert.Equal(6, page[^1].TmdbId);

        Assert.Single(await _service.ListAsync(1, 0, CancellationToken.None));
    }

    [Fact]
    public async Task Reports_PruneRowsOutsideTheRetentionWindow()
    {
        await _service.ReportProgressAsync(Movie(1, position: 10, duration: 600), CancellationToken.None);
        _clock.Advance(TimeSpan.FromDays(CatalogWatchHistoryService.RetentionDays + 1));
        await _service.ReportProgressAsync(Movie(2, position: 10, duration: 600), CancellationToken.None);

        var rows = await _service.ListAsync(1, 20, CancellationToken.None);

        Assert.Equal(2, Assert.Single(rows).TmdbId);
    }

    private static ReportProgressRequest Movie(
        int tmdbId, double? position, double? duration, bool finished = false)
        => new(
            "movie",
            tmdbId,
            null,
            null,
            position,
            duration,
            finished,
            "Big Buck Bunny",
            null,
            "https://image.tmdb.org/t/p/w500/poster.jpg",
            "https://image.tmdb.org/t/p/w1280/backdrop.jpg",
            ProfileId: 1);

    private static ReportProgressRequest Series(int tmdbId, int season, int episode, double position)
        => new(
            "series",
            tmdbId,
            season,
            episode,
            position,
            1500,
            false,
            "The Beverly Hillbillies",
            $"S{season}E{episode} · The Clampetts Strike Oil",
            null,
            null,
            ProfileId: 1);

    private sealed class MutableTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }
}
