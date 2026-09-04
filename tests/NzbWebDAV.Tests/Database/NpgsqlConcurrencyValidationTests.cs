using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;
using NzbWebDAV.Tests.TestUtils;

namespace NzbWebDAV.Tests.Database;

// Opt-in validation, not part of the standard suite: requires a live Postgres on
// 127.0.0.1:15432 (e.g. `docker run -d -e POSTGRES_PASSWORD=test -e POSTGRES_USER=test
// -e POSTGRES_DB=nzbdav_test -p 15432:5432 postgres:16`) and RUN_NPGSQL_CONCURRENCY_TESTS=1
// (see OptInPostgresFactAttribute). Exists because SQLite does NOT reproduce the
// NpgsqlOperationInProgressException this guards against — the concurrency-safety bug in
// GetDirectoryChildrenEnumerableAsync only surfaces against real Npgsql, so a SQLite-only
// suite can't catch a regression here. Verified: fails with the exact production stack
// trace against the pre-fix code, passes against the fixed code.
//
// Uses [OptInPostgresFactAttribute], not this project's usual [SkippableFact] +
// Skip.IfNot() pattern: this class's Postgres connection happens in
// IAsyncLifetime.InitializeAsync(), which xUnit runs before the test body -- a body-level
// Skip.IfNot() would already be too late to stop that connection attempt when Postgres
// isn't available. FactAttribute.Skip is evaluated at discovery time, before
// InitializeAsync ever runs, which is what actually prevents the connection attempt.
public sealed class NpgsqlConcurrencyValidationTests : IAsyncLifetime
{
    private const string ConnectionString =
        "Host=127.0.0.1;Port=15432;Database=nzbdav_test;Username=test;Password=test";

    private DavDatabaseContext _context = null!;
    private DavDatabaseClient _client = null!;

    public async Task InitializeAsync()
    {
        // test-harness-only: DavItem.New() uses DateTime.Now (Kind=Local); relax Npgsql's
        // default strict-UTC requirement rather than reworking test fixtures.
        AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);
        var options = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseNpgsql(ConnectionString)
            .Options;
        _context = new DavDatabaseContext(options);
        await _context.Database.EnsureDeletedAsync();
        await _context.Database.EnsureCreatedAsync();
        _client = new DavDatabaseClient(_context);
    }

    [OptInPostgresFact]
    public async Task GetDirectoryChildrenEnumerableAsync_AgainstRealNpgsql_SurvivesConcurrentQuery()
    {
        var directory = DavItem.New(
            Guid.NewGuid(), DavItem.Root, "shows", null,
            DavItem.ItemType.Directory, DavItem.ItemSubType.Directory,
            null, null, null, null);
        var firstFile = DavItem.New(
            Guid.NewGuid(), directory, "episode1.mkv", 100,
            DavItem.ItemType.UsenetFile, DavItem.ItemSubType.NzbFile,
            null, null, null, null);
        var secondFile = DavItem.New(
            Guid.NewGuid(), directory, "episode2.mkv", 100,
            DavItem.ItemType.UsenetFile, DavItem.ItemSubType.NzbFile,
            null, null, null, null);

        _context.Items.AddRange(directory, firstFile, secondFile);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var seen = new List<DavItem>();
        await foreach (var child in _client.GetDirectoryChildrenEnumerableAsync(directory.Id))
        {
            seen.Add(child);
            var lookup = await _client.GetDirectoryChildAsync(directory.Id, child.Name);
            Assert.NotNull(lookup);
        }

        Assert.Equal(2, seen.Count);
    }

    [OptInPostgresFact]
    public async Task GetCompletedSymlinkCategoryChildrenEnumerableAsync_AgainstRealNpgsql_SurvivesConcurrentQuery()
    {
        var directory = DavItem.New(
            Guid.NewGuid(), DavItem.Root, "release-1", null,
            DavItem.ItemType.Directory, DavItem.ItemSubType.Directory,
            null, null, null, null);
        _context.Items.Add(directory);
        await _context.SaveChangesAsync();

        _context.HistoryItems.Add(new HistoryItem
        {
            Id = Guid.NewGuid(),
            CreatedAt = DateTime.Now,
            FileName = "release-1",
            JobName = "release-1",
            Category = "movies",
            DownloadStatus = HistoryItem.DownloadStatusOption.Completed,
            DownloadDirId = directory.Id,
        });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var seen = new List<DavItem>();
        await foreach (var child in _client.GetCompletedSymlinkCategoryChildrenEnumerableAsync("movies"))
        {
            seen.Add(child);
            var lookup = await _client.GetDirectoryChildAsync(DavItem.Root.Id, "release-1");
            // Not asserting non-null here: DavItem.Root.Id isn't this item's real parent in
            // this fixture. The point is only that issuing a second query mid-enumeration
            // does not throw NpgsqlOperationInProgressException.
            _ = lookup;
        }

        Assert.Single(seen);
    }

    // The three sites below (GetHealthCheckQueueController's two loops, and
    // HealthCheckService's background scan) are NOT fixed — as of this commit their
    // callers happen to do no DB work inside the streaming loop body, so they don't
    // trip the hazard today. This test proves the underlying query methods they use
    // are exactly as susceptible as GetDirectoryChildrenEnumerableAsync was: if a
    // future change adds any per-item DB call inside either controller loop or the
    // HealthCheckService scan loop, it will reproduce this exact exception in
    // production, and this test documents why.
    [OptInPostgresFact]
    public async Task HealthCheckQueueItems_StreamedWithInterleavedQuery_ThrowsOnRealNpgsql()
    {
        var file = DavItem.New(
            Guid.NewGuid(), DavItem.Root, "movie.mkv", 100,
            DavItem.ItemType.UsenetFile, DavItem.ItemSubType.NzbFile,
            null, null, null, null);
        _context.Items.Add(file);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        await Assert.ThrowsAsync<Npgsql.NpgsqlOperationInProgressException>(async () =>
        {
            await foreach (var item in HealthCheckService.GetHealthCheckQueueItems(_client)
                .AsAsyncEnumerable().ConfigureAwait(false))
            {
                // Simulates a future contributor adding a per-item DB lookup inside one
                // of the three currently-safe-by-accident streaming loops.
                await _client.GetFileById(item.Id.ToString());
            }
        });
    }

    public async Task DisposeAsync()
    {
        await _context.Database.EnsureDeletedAsync();
        await _context.DisposeAsync();
    }
}
