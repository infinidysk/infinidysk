using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Queue.PostProcessors;
using NzbWebDAV.Tasks;
using NzbWebDAV.Websocket;

namespace NzbWebDAV.Tests.Tasks;

[Collection(nameof(BaseTaskCollection))]
public class RecreateStrmFilesTaskTests : IAsyncLifetime
{
    private readonly string _dbPath = Path.Join(Path.GetTempPath(), $"recreate-strm-{Guid.NewGuid():N}.sqlite");
    private readonly string _strmDir = Path.Join(Path.GetTempPath(), $"recreate-strm-out-{Guid.NewGuid():N}");
    private DavDatabaseContext _context = null!;
    private DavDatabaseClient _dbClient = null!;
    private ConfigManager _config = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_strmDir);
        var options = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .Options;
        _context = new DavDatabaseContext(options);
        await _context.Database.EnsureCreatedAsync();
        _dbClient = new DavDatabaseClient(_context);

        _config = new ConfigManager();
        _config.UpdateValues(
        [
            new() { ConfigName = ConfigKeys.ApiCompletedDownloadsDir, ConfigValue = _strmDir },
            new() { ConfigName = ConfigKeys.ApiImportStrategy, ConfigValue = "strm" },
            new() { ConfigName = "general.base-url", ConfigValue = "http://localhost:3000" },
            new() { ConfigName = ConfigKeys.ApiStrmKey, ConfigValue = "test-strm-key" },
        ]);

        await BaseTask.ResetRunningTaskForTestsAsync();
    }

    public async Task DisposeAsync()
    {
        await BaseTask.ResetRunningTaskForTestsAsync();
        await _context.DisposeAsync();
        try { File.Delete(_dbPath); } catch { /* ignore */ }
        try { Directory.Delete(_strmDir, recursive: true); } catch { /* ignore */ }
    }

    [Fact]
    public async Task CreatesMissingStrmForMountedVideos()
    {
        var category = SeedDirectory(DavItem.ContentFolder, "tv");
        var job = SeedDirectory(category, "Show");
        SeedVideo(job, "ep01.mkv");
        SeedVideo(job, "ep02.mkv");
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var executed = await new RecreateStrmFilesTask(
            _config, _dbClient, new WebsocketManager(), rewriteAll: false).Execute();

        Assert.True(executed);
        var strmFiles = Directory.GetFiles(_strmDir, "*.strm", SearchOption.AllDirectories);
        Assert.Equal(2, strmFiles.Length);
    }

    [Fact]
    public async Task SkipsIdenticalExistingContent()
    {
        var category = SeedDirectory(DavItem.ContentFolder, "movies");
        var job = SeedDirectory(category, "Movie");
        var video = SeedVideo(job, "movie.mkv");
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        await CreateStrmFilesPostProcessor.WriteStrmFileAsync(_config, video, forceRewrite: false);
        var path = Directory.GetFiles(_strmDir, "*.strm", SearchOption.AllDirectories).Single();
        var mtime1 = File.GetLastWriteTimeUtc(path);

        await Task.Delay(50);
        await new RecreateStrmFilesTask(_config, _dbClient, new WebsocketManager()).Execute();
        var mtime2 = File.GetLastWriteTimeUtc(path);

        Assert.Equal(mtime1, mtime2);
    }

    [Fact]
    public async Task RewriteAllForcesRewrite()
    {
        var category = SeedDirectory(DavItem.ContentFolder, "movies");
        var job = SeedDirectory(category, "Movie");
        var video = SeedVideo(job, "movie.mkv");
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        await CreateStrmFilesPostProcessor.WriteStrmFileAsync(_config, video, forceRewrite: false);
        var path = Directory.GetFiles(_strmDir, "*.strm", SearchOption.AllDirectories).Single();
        var mtime1 = File.GetLastWriteTimeUtc(path);

        await Task.Delay(50);
        await new RecreateStrmFilesTask(_config, _dbClient, new WebsocketManager(), rewriteAll: true).Execute();
        var mtime2 = File.GetLastWriteTimeUtc(path);

        Assert.True(mtime2 > mtime1);
    }

    [Fact]
    public async Task DoesNotDoubleWrapExistingStrmNamedItems()
    {
        var category = SeedDirectory(DavItem.ContentFolder, "tv");
        var job = SeedDirectory(category, "Show");
        SeedVideo(job, "ep.mkv");
        SeedVideo(job, "sidecar.strm");
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        await new RecreateStrmFilesTask(_config, _dbClient, new WebsocketManager()).Execute();

        var strmFiles = Directory.GetFiles(_strmDir, "*.strm", SearchOption.AllDirectories);
        Assert.Single(strmFiles);
        Assert.DoesNotContain(strmFiles, f => f.EndsWith("sidecar.strm.strm", StringComparison.OrdinalIgnoreCase));
    }

    private DavItem SeedDirectory(DavItem parent, string name)
    {
        var item = DavItem.New(
            Guid.NewGuid(), parent, name, null,
            DavItem.ItemType.Directory, DavItem.ItemSubType.Directory,
            null, null, null, null);
        _context.Items.Add(item);
        return item;
    }

    private DavItem SeedVideo(DavItem parent, string name)
    {
        var item = DavItem.New(
            Guid.NewGuid(), parent, name, 100,
            DavItem.ItemType.UsenetFile, DavItem.ItemSubType.NzbFile,
            null, null, null, Guid.NewGuid());
        _context.Items.Add(item);
        return item;
    }
}
