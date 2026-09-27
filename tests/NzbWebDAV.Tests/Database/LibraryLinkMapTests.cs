using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Interceptors;
using NzbWebDAV.Database.MigrationHelpers;
using NzbWebDAV.Database.Models;
using NzbWebDAV.MediaLibrary;

namespace NzbWebDAV.Tests.Database;

public sealed class LibraryLinkMapTests : IAsyncLifetime
{
    private readonly string _databasePath =
        Path.Join(Path.GetTempPath(), $"nzbdav-librarymap-tests-{Guid.NewGuid():N}.sqlite");
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
        File.Delete(_databasePath);
    }

    [Fact]
    public async Task InsertAndRead_RoundTripsAllFields()
    {
        var id = Guid.NewGuid();
        _context.LinkMaps.Add(new LibraryLinkMap
        {
            Id = Guid.NewGuid(),
            DavItemId = id,
            LinkPath = "movies/film.mkv",
            TargetText = $"/mnt/nzbdav/.ids/{id}.mkv",
            MappingType = LibraryMappingType.Internal,
            Status = LibraryLinkStatus.Valid,
            LastSeenUtc = DateTime.UtcNow,
        });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var row = await _context.LinkMaps.SingleAsync(x => x.LinkPath == "movies/film.mkv");
        Assert.Equal(id, row.DavItemId);
        Assert.Equal(LibraryMappingType.Internal, row.MappingType);
        Assert.Equal(LibraryLinkStatus.Valid, row.Status);
    }

    [Fact]
    public async Task DuplicateLinkPath_ViolatesUniqueIndex()
    {
        _context.LinkMaps.Add(new LibraryLinkMap
        {
            Id = Guid.NewGuid(),
            LinkPath = "movies/dup.mkv",
            TargetText = "/old-nas/dup.mkv",
            MappingType = LibraryMappingType.External,
            Status = LibraryLinkStatus.Valid,
            LastSeenUtc = DateTime.UtcNow,
        });
        await _context.SaveChangesAsync();
        _context.LinkMaps.Add(new LibraryLinkMap
        {
            Id = Guid.NewGuid(),
            LinkPath = "movies/dup.mkv",
            TargetText = "/old-nas/other.mkv",
            MappingType = LibraryMappingType.External,
            Status = LibraryLinkStatus.Valid,
            LastSeenUtc = DateTime.UtcNow,
        });
        await Assert.ThrowsAsync<DbUpdateException>(() => _context.SaveChangesAsync());
    }
}
