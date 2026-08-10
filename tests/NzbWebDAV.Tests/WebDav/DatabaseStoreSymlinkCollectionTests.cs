using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using NWebDav.Server;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Interceptors;
using NzbWebDAV.Database.MigrationHelpers;
using NzbWebDAV.Database.Models;
using NzbWebDAV.WebDav;
using NzbWebDAV.Websocket;

namespace NzbWebDAV.Tests.WebDav;

public sealed class DatabaseStoreSymlinkCollectionTests : IAsyncLifetime
{
    private readonly string _databasePath = Path.Join(Path.GetTempPath(), $"nzbdav-symlink-{Guid.NewGuid():N}.sqlite");
    private DavDatabaseContext _context = null!;
    private DavDatabaseClient _client = null!;
    private ConfigManager _config = null!;
    private WebsocketManager _websocket = null!;

    public async Task InitializeAsync()
    {
        var options = new DbContextOptionsBuilder<DavDatabaseContext>().UseSqlite($"Data Source={_databasePath}")
            .AddInterceptors(new SqliteForeignKeyEnabler())
            .ReplaceService<IMigrationsSqlGenerator, SqliteMigrationsSqlGenerator<SqliteMigrationsSqlGenerator>>().Options;
        _context = new DavDatabaseContext(options);
        await _context.Database.MigrateAsync();
        _client = new DavDatabaseClient(_context);
        _config = new ConfigManager();
        _config.UpdateValues([new ConfigItem { ConfigName = ConfigKeys.WebdavEnforceReadonly, ConfigValue = "false" }]);
        _websocket = new WebsocketManager();
    }

    [Fact]
    public async Task DeleteItemAsync_FromSymlinkRoot_ReturnsForbidden()
    {
        var status = await new DatabaseStoreSymlinkCollection(DavItem.SymlinkFolder, _client, _config, _websocket)
            .DeleteItemAsync("movies", CancellationToken.None);
        Assert.Equal(DavStatusCode.Forbidden, status);
    }

    [Fact]
    public async Task DeleteItemAsync_FileDelete_DoesNotRemoveHistory()
    {
        var category = NewDir(Guid.NewGuid(), DavItem.ContentFolder, "movies");
        var release = NewDir(Guid.NewGuid(), category, "Show.S01E01");
        var historyId = Guid.NewGuid();
        _context.Items.AddRange(category, release);
        _context.HistoryItems.Add(CreateHistory(historyId, "episode.nzb", category.Name, release.Id));
        await _context.SaveChangesAsync();
        var status = await new DatabaseStoreSymlinkCollection(release, _client, _config, _websocket)
            .DeleteItemAsync("episode.mkv", CancellationToken.None);
        Assert.Equal(DavStatusCode.NoContent, status);
        Assert.NotNull(await _context.HistoryItems.AsNoTracking().FirstOrDefaultAsync(x => x.Id == historyId));
    }

    [Fact]
    public async Task DeleteItemAsync_ReleaseDirectory_PrunesHistoryAndPreservesDavItems()
    {
        var category = NewDir(Guid.NewGuid(), DavItem.ContentFolder, "movies");
        var release = NewDir(Guid.NewGuid(), category, "Show.S01E01");
        var historyId = Guid.NewGuid();
        _context.Items.AddRange(category, release);
        _context.HistoryItems.Add(CreateHistory(historyId, "episode.nzb", category.Name, release.Id));
        await _context.SaveChangesAsync();
        var status = await new DatabaseStoreSymlinkCollection(category, _client, _config, _websocket)
            .DeleteItemAsync(release.Name, CancellationToken.None);
        Assert.Equal(DavStatusCode.NoContent, status);
        Assert.Null(await _context.HistoryItems.AsNoTracking().FirstOrDefaultAsync(x => x.Id == historyId));
        Assert.NotNull(await _context.Items.AsNoTracking().FirstOrDefaultAsync(x => x.Id == release.Id));
    }

    [Fact]
    public async Task DeleteItemAsync_WhenReadonlyWebdavEnabled_ReturnsForbiddenForReleaseDirectory()
    {
        _config.UpdateValues([new ConfigItem { ConfigName = ConfigKeys.WebdavEnforceReadonly, ConfigValue = "true" }]);
        var category = NewDir(Guid.NewGuid(), DavItem.ContentFolder, "tv");
        var release = NewDir(Guid.NewGuid(), category, "Show.S01E01");
        var historyId = Guid.NewGuid();
        _context.Items.AddRange(category, release);
        _context.HistoryItems.Add(CreateHistory(historyId, "episode.nzb", category.Name, release.Id));
        await _context.SaveChangesAsync();
        var status = await new DatabaseStoreSymlinkCollection(category, _client, _config, _websocket)
            .DeleteItemAsync(release.Name, CancellationToken.None);
        Assert.Equal(DavStatusCode.Forbidden, status);
    }

    private static DavItem NewDir(Guid id, DavItem parent, string name) =>
        DavItem.New(id, parent, name, null, DavItem.ItemType.Directory, DavItem.ItemSubType.Directory, null, null, null, null);

    private static HistoryItem CreateHistory(Guid id, string fileName, string category, Guid downloadDirId) => new()
    {
        Id = id,
        CreatedAt = DateTime.UtcNow,
        FileName = fileName,
        JobName = Path.GetFileNameWithoutExtension(fileName),
        Category = category,
        DownloadStatus = HistoryItem.DownloadStatusOption.Completed,
        DownloadDirId = downloadDirId,
        TotalSegmentBytes = 100,
        DownloadTimeSeconds = 1,
    };

    public async Task DisposeAsync() { await _context.DisposeAsync(); try { File.Delete(_databasePath); } catch (IOException) { } }
}
