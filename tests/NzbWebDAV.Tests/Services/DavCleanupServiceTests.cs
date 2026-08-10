using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Interceptors;
using NzbWebDAV.Database.MigrationHelpers;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;

namespace NzbWebDAV.Tests.Services;

public sealed class DavCleanupServiceTests : IAsyncLifetime
{
    private readonly string _databasePath =
        Path.Join(Path.GetTempPath(), $"nzbdav-dav-cleanup-{Guid.NewGuid():N}.sqlite");
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
        await _context.Database.ExecuteSqlRawAsync("DELETE FROM DavCleanupItems");
    }

    public async Task DisposeAsync()
    {
        await _context.DisposeAsync();
        try { File.Delete(_databasePath); } catch (IOException) { /* best effort */ }
    }

    [Fact]
    public async Task ProcessNextItemAsync_CleansLowercaseQueueIdAndMixedCaseParentIds()
    {
        var parentId = Guid.Parse("abcdefab-cdef-4abc-8def-abcdefabcdef");
        var lowercaseParentId = parentId.ToString().ToLowerInvariant();
        var rawChildId = Guid.NewGuid();
        var efChildId = Guid.NewGuid();

        await InsertCleanupItemAsync(lowercaseParentId);
        await InsertRawChildAsync(rawChildId, lowercaseParentId);

        var deletedParent = new DavItem
        {
            Id = parentId,
            Name = "deleted",
            Type = DavItem.ItemType.Directory,
            SubType = DavItem.ItemSubType.Directory,
            Path = "/deleted",
        };
        _context.Items.Add(DavItem.New(
            efChildId,
            deletedParent,
            "ef-child.mkv",
            1,
            DavItem.ItemType.UsenetFile,
            DavItem.ItemSubType.NzbFile,
            null,
            null,
            null,
            null));
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var storedParentIds = await _context.Database
            .SqlQueryRaw<string>(
                """
                SELECT ParentId AS Value
                FROM DavItems
                WHERE Path IN ('/deleted/raw-child.mkv', '/deleted/ef-child.mkv')
                """)
            .ToListAsync();
        Assert.Contains(lowercaseParentId, storedParentIds);
        Assert.Contains(parentId.ToString().ToUpperInvariant(), storedParentIds);

        var processed = await DavCleanupService.ProcessNextItemAsync(
            _context,
            CancellationToken.None);

        Assert.True(processed);
        Assert.False(await _context.Items.AsNoTracking()
            .AnyAsync(x => x.Id == rawChildId || x.Id == efChildId));
        Assert.Equal(0, await CountCleanupItemsAsync());
        Assert.False(await DavCleanupService.ProcessNextItemAsync(
            _context,
            CancellationToken.None));
    }

    [Fact]
    public async Task ProcessNextItemAsync_DequeuesItemWhenChildrenAreAlreadyGone()
    {
        var lowercaseId = "12345678-abcd-4abc-8def-abcdefabcdef";
        await InsertCleanupItemAsync(lowercaseId);

        var processed = await DavCleanupService.ProcessNextItemAsync(
            _context,
            CancellationToken.None);

        Assert.True(processed);
        Assert.Equal(0, await CountCleanupItemsAsync());
        Assert.False(await DavCleanupService.ProcessNextItemAsync(
            _context,
            CancellationToken.None));
    }

    private Task InsertCleanupItemAsync(string id) =>
        _context.Database.ExecuteSqlRawAsync(
            "INSERT INTO DavCleanupItems (Id) VALUES (@id)",
            new SqliteParameter("@id", id));

    private Task InsertRawChildAsync(Guid childId, string parentId) =>
        _context.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO DavItems
                (Id, IdPrefix, CreatedAt, ParentId, Name, FileSize, Type, SubType, Path)
            VALUES
                (@id, @idPrefix, CURRENT_TIMESTAMP, @parentId, @name, @fileSize, @type, @subType, @path)
            """,
            new SqliteParameter("@id", childId.ToString().ToUpperInvariant()),
            new SqliteParameter("@idPrefix", childId.ToString("N")[..DavItem.IdPrefixLength]),
            new SqliteParameter("@parentId", parentId),
            new SqliteParameter("@name", "raw-child.mkv"),
            new SqliteParameter("@fileSize", 1),
            new SqliteParameter("@type", (int)DavItem.ItemType.UsenetFile),
            new SqliteParameter("@subType", (int)DavItem.ItemSubType.NzbFile),
            new SqliteParameter("@path", "/deleted/raw-child.mkv"));

    private Task<int> CountCleanupItemsAsync() =>
        _context.Database
            .SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM DavCleanupItems")
            .SingleAsync();
}
