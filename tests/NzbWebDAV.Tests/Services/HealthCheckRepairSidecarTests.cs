using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Interceptors;
using NzbWebDAV.Database.MigrationHelpers;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;

namespace NzbWebDAV.Tests.Services;

public sealed class HealthCheckRepairSidecarTests : IAsyncLifetime
{
    private readonly string _databasePath =
        Path.Join(Path.GetTempPath(), $"nzbdav-health-sidecar-{Guid.NewGuid():N}.sqlite");
    private readonly string _rootDir =
        Path.Join(Path.GetTempPath(), $"nzbdav-health-sidecar-{Guid.NewGuid():N}");
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
        Directory.CreateDirectory(_rootDir);
    }

    public async Task DisposeAsync()
    {
        await _context.DisposeAsync();
        try { File.Delete(_databasePath); } catch (IOException) { /* best effort */ }
        try { Directory.Delete(_rootDir, recursive: true); } catch (IOException) { /* best effort */ }
    }

    [Fact]
    public async Task RepairRemoval_DeletesOwnedStrmSidecarWithTheItem()
    {
        var item = NewUsenetFile("movie.mkv");
        var strmPath = Path.Join(_rootDir, "completed-downloads", "movies", "Some.Release", "movie.mkv.strm");
        var strmTarget = $"http://localhost/view/.ids/{item.Id}.mkv";
        item.GeneratedStrmOutputRoot = Path.GetFullPath(Path.Join(_rootDir, "completed-downloads"));
        item.GeneratedStrmPath = strmPath;
        item.GeneratedStrmTarget = strmTarget;
        _context.Items.Add(item);
        await _context.SaveChangesAsync();

        Directory.CreateDirectory(Path.GetDirectoryName(strmPath)!);
        await File.WriteAllTextAsync(strmPath, strmTarget);

        HealthCheckService.RemoveDavItemWithGeneratedSidecars(_dbClient, item);
        await _context.SaveChangesAsync();

        Assert.False(File.Exists(strmPath));
        Assert.False(Directory.Exists(Path.Join(_rootDir, "completed-downloads", "movies")));
        Assert.False(await _context.Items.AnyAsync(x => x.Id == item.Id));
    }

    [Fact]
    public async Task RepairRemoval_PreservesSidecarWhoseOnDiskTargetChanged()
    {
        var item = NewUsenetFile("movie.mkv");
        var strmPath = Path.Join(_rootDir, "completed-downloads", "movie.mkv.strm");
        item.GeneratedStrmOutputRoot = Path.GetFullPath(Path.Join(_rootDir, "completed-downloads"));
        item.GeneratedStrmPath = strmPath;
        item.GeneratedStrmTarget = $"http://localhost/view/.ids/{item.Id}.mkv";
        _context.Items.Add(item);
        await _context.SaveChangesAsync();

        Directory.CreateDirectory(Path.GetDirectoryName(strmPath)!);
        await File.WriteAllTextAsync(strmPath, $"http://localhost/view/.ids/{Guid.NewGuid()}.mkv");

        HealthCheckService.RemoveDavItemWithGeneratedSidecars(_dbClient, item);
        await _context.SaveChangesAsync();

        Assert.True(File.Exists(strmPath));
        Assert.False(await _context.Items.AnyAsync(x => x.Id == item.Id));
    }

    [Fact]
    public async Task RepairRemoval_WithoutGeneratedSidecars_StillRemovesItem()
    {
        var item = NewUsenetFile("movie.mkv");
        _context.Items.Add(item);
        await _context.SaveChangesAsync();

        HealthCheckService.RemoveDavItemWithGeneratedSidecars(_dbClient, item);
        await _context.SaveChangesAsync();

        Assert.False(await _context.Items.AnyAsync(x => x.Id == item.Id));
    }

    private static DavItem NewUsenetFile(string name) =>
        DavItem.New(
            Guid.NewGuid(),
            DavItem.ContentFolder,
            name,
            fileSize: 100,
            DavItem.ItemType.UsenetFile,
            DavItem.ItemSubType.NzbFile,
            releaseDate: null,
            lastHealthCheck: null,
            historyItemId: null,
            fileBlobId: null);
}
