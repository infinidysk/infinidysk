using Microsoft.EntityFrameworkCore;
using Npgsql;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Tests.TestUtils;

namespace NzbWebDAV.Tests.Database;

// Opt-in validation, not part of the standard suite: requires a live Postgres on
// 127.0.0.1:15432 (e.g. `docker run -d -e POSTGRES_PASSWORD=test -e POSTGRES_USER=test
// -e POSTGRES_DB=nzbdav_test -p 15432:5432 postgres:16`), RUN_NPGSQL_CONCURRENCY_TESTS=1,
// and DATABASE_PROVIDER=postgres (see OptInPostgresFactAttribute for why the latter is
// required -- it's not just a gate, DavDatabaseContext's model shape depends on it).
// Exists because SQLite does NOT reproduce the
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
//
// Isolation matches PostgresMigrationTests: a unique schema per test-class instance
// (rather than EnsureDeleted/EnsureCreated against a single hard-coded database), so a
// developer's own "nzbdav_test" database is never dropped and concurrent test runs
// cannot collide. Uses real migrations via PostgresDavDatabaseContext + MigrateAsync,
// not EnsureCreatedAsync -- this also runs entities through the same wall-clock
// DateTime value converters the running application uses, so no process-wide
// Npgsql legacy-timestamp compatibility switch is needed here.
public sealed class NpgsqlConcurrencyValidationTests : IAsyncLifetime
{
    private const string BaseConnectionString =
        "Host=127.0.0.1;Port=15432;Database=nzbdav_test;Username=test;Password=test";

    private readonly string _schema = $"nzbdav_test_{Guid.NewGuid():N}";
    private DavDatabaseContext _context = null!;
    private DavDatabaseClient _client = null!;

    public async Task InitializeAsync()
    {
        await using var adminConnection = new NpgsqlConnection(BaseConnectionString);
        await adminConnection.OpenAsync();
        await using (var create = adminConnection.CreateCommand())
        {
            create.CommandText = $"CREATE SCHEMA \"{_schema}\"";
            await create.ExecuteNonQueryAsync();
        }

        var scopedConnectionString = new NpgsqlConnectionStringBuilder(BaseConnectionString)
        {
            SearchPath = _schema
        }.ConnectionString;
        var options = new DbContextOptionsBuilder<PostgresDavDatabaseContext>()
            .UseNpgsql(scopedConnectionString)
            .Options;
        _context = new PostgresDavDatabaseContext(options);
        await _context.Database.MigrateAsync().WaitAsync(TimeSpan.FromSeconds(30));
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
            // directory's parent is DavItem.Root, so this lookup should succeed; the
            // point is that issuing it mid-enumeration must not throw
            // NpgsqlOperationInProgressException.
            var lookup = await _client.GetDirectoryChildAsync(DavItem.Root.Id, "release-1");
            Assert.NotNull(lookup);
        }

        Assert.Single(seen);
    }

    public async Task DisposeAsync()
    {
        await using (var adminConnection = new NpgsqlConnection(BaseConnectionString))
        {
            await adminConnection.OpenAsync();
            await using var drop = adminConnection.CreateCommand();
            drop.CommandText = $"DROP SCHEMA IF EXISTS \"{_schema}\" CASCADE";
            await drop.ExecuteNonQueryAsync();
        }

        await _context.DisposeAsync();
    }
}
