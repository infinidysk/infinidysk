using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Queue.PostProcessors;
using NzbWebDAV.WebDav;

namespace NzbWebDAV.Tests.Queue;

public sealed class CreateSymlinkFilesPostProcessorTests : IDisposable
{
    private readonly string _dbPath;
    private readonly string _outputDirectory;
    private readonly DavDatabaseContext _context;
    private readonly DavDatabaseClient _dbClient;
    private readonly ConfigManager _config;
    private readonly Guid _historyItemId = Guid.NewGuid();

    public CreateSymlinkFilesPostProcessorTests()
    {
        _dbPath = Path.Join(Path.GetTempPath(), $"symlink-test-{Guid.NewGuid():N}.sqlite");
        _outputDirectory = Path.Join(Path.GetTempPath(), $"symlink-out-{Guid.NewGuid():N}");
        var options = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .Options;
        _context = new DavDatabaseContext(options);
        _context.Database.EnsureCreated();
        _dbClient = new DavDatabaseClient(_context);
        _config = new ConfigManager();
        _config.UpdateValues(
        [
            new() { ConfigName = ConfigKeys.ApiSymlinkOutputDir, ConfigValue = _outputDirectory },
            new() { ConfigName = ConfigKeys.RcloneMountDir, ConfigValue = "/mnt/nzbdav" },
        ]);
    }

    [Fact]
    public async Task CreateSymlinkFilesAsync_CreatesNestedCanonicalTargetAndIsIdempotent()
    {
        var category = SeedDirectory(DavItem.ContentFolder, "tv");
        var show = SeedDirectory(category, "Show", _historyItemId);
        var season = SeedDirectory(show, "Season 01", _historyItemId);
        var item = SeedVideo(season, "episode.mkv");
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var processor = new CreateSymlinkFilesPostProcessor(_config, _dbClient, _historyItemId);
        await processor.CreateSymlinkFilesAsync();
        await processor.CreateSymlinkFilesAsync();
        await _context.SaveChangesAsync();

        var path = CreateSymlinkFilesPostProcessor.GetSymlinkFilePath(_outputDirectory, item);
        var persisted = await _context.Items.SingleAsync(x => x.Id == item.Id);
        Assert.Equal(
            DatabaseStoreSymlinkFile.GetTargetPath(item.Id, "/mnt/nzbdav"),
            new FileInfo(path).LinkTarget);
        Assert.Equal(Path.GetFullPath(_outputDirectory), persisted.GeneratedSymlinkOutputRoot);
        Assert.Equal(Path.GetFullPath(path), persisted.GeneratedSymlinkPath);
        Assert.Equal(DatabaseStoreSymlinkFile.GetTargetPath(item.Id, "/mnt/nzbdav"), persisted.GeneratedSymlinkTarget);
    }

    [Fact]
    public void DeleteSymlinkFile_PreservesForeignSymlink()
    {
        var item = NewVideo("/content/movies/Movie/movie.mkv");
        var path = CreateSymlinkFilesPostProcessor.GetSymlinkFilePath(_outputDirectory, item);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.CreateSymbolicLink(path, "/tmp/foreign-target");

        item.GeneratedSymlinkOutputRoot = _outputDirectory;
        item.GeneratedSymlinkPath = path;
        item.GeneratedSymlinkTarget = DatabaseStoreSymlinkFile.GetTargetPath(item.Id, "/mnt/nzbdav");
        CreateSymlinkFilesPostProcessor.DeleteSymlinkFile(item);

        Assert.Equal("/tmp/foreign-target", new FileInfo(path).LinkTarget);
    }

    [Fact]
    public void DeleteSymlinkFile_RemovesOwnedSymlinkAndEmptyDirectories()
    {
        var item = NewVideo("/content/movies/Movie/movie.mkv");
        var path = CreateSymlinkFilesPostProcessor.GetSymlinkFilePath(_outputDirectory, item);
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        File.CreateSymbolicLink(
            path,
            DatabaseStoreSymlinkFile.GetTargetPath(item.Id, "/mnt/nzbdav"));
        item.GeneratedSymlinkOutputRoot = _outputDirectory;
        item.GeneratedSymlinkPath = path;
        item.GeneratedSymlinkTarget = DatabaseStoreSymlinkFile.GetTargetPath(item.Id, "/mnt/nzbdav");

        CreateSymlinkFilesPostProcessor.DeleteSymlinkFile(item);

        Assert.Null(new FileInfo(path).LinkTarget);
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public void GetSymlinkFilePath_PreservesNestedContentLayout()
    {
        var item = new DavItem { Path = "/content/tv/Show/Season 01/episode.mkv" };

        Assert.Equal(
            Path.Join(_outputDirectory, "tv", "Show", "Season 01", "episode.mkv"),
            CreateSymlinkFilesPostProcessor.GetSymlinkFilePath(_outputDirectory, item));
    }

    public void Dispose()
    {
        _context.Dispose();
        try { File.Delete(_dbPath); } catch (IOException) { /* best effort */ }
        try { Directory.Delete(_outputDirectory, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private DavItem SeedDirectory(DavItem parent, string name, Guid? historyItemId = null)
    {
        var item = DavItem.New(
            Guid.NewGuid(), parent, name, null,
            DavItem.ItemType.Directory, DavItem.ItemSubType.Directory,
            null, null, historyItemId, null);
        _context.Items.Add(item);
        return item;
    }

    private DavItem SeedVideo(DavItem parent, string name)
    {
        var item = DavItem.New(
            Guid.NewGuid(), parent, name, 100,
            DavItem.ItemType.UsenetFile, DavItem.ItemSubType.NzbFile,
            null, null, _historyItemId, Guid.NewGuid());
        _context.Items.Add(item);
        return item;
    }

    private DavItem NewVideo(string path)
    {
        var name = Path.GetFileName(path);
        var parent = new DavItem { Path = Path.GetDirectoryName(path)!, Name = "Movie" };
        return DavItem.New(
            Guid.NewGuid(), parent, name, 100,
            DavItem.ItemType.UsenetFile, DavItem.ItemSubType.NzbFile,
            null, null, _historyItemId, Guid.NewGuid());
    }
}
