using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Queue.PostProcessors;

namespace NzbWebDAV.Tests.Queue;

public class CreateStrmFilesPostProcessorTests : IDisposable
{
    private readonly string _dbPath;
    private readonly string _strmDir;
    private readonly DavDatabaseContext _context;
    private readonly DavDatabaseClient _dbClient;
    private readonly ConfigManager _config;
    private readonly Guid _historyItemId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    public CreateStrmFilesPostProcessorTests()
    {
        _dbPath = Path.Join(Path.GetTempPath(), $"strm-test-{Guid.NewGuid():N}.sqlite");
        _strmDir = Path.Join(Path.GetTempPath(), $"strm-out-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_strmDir);

        var options = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .Options;
        _context = new DavDatabaseContext(options);
        _context.Database.EnsureCreated();
        _dbClient = new DavDatabaseClient(_context);

        _config = new ConfigManager();
        _config.UpdateValues(
        [
            new() { ConfigName = ConfigKeys.ApiCompletedDownloadsDir, ConfigValue = _strmDir },
            new() { ConfigName = ConfigKeys.ApiImportStrategy, ConfigValue = "strm" },
            new() { ConfigName = "general.base-url", ConfigValue = "http://localhost:3000" },
            new() { ConfigName = ConfigKeys.ApiStrmKey, ConfigValue = "test-strm-key" },
        ]);
    }

    public void Dispose()
    {
        _context.Dispose();
        try { File.Delete(_dbPath); } catch (IOException) { /* ignore */ }
        try { Directory.Delete(_strmDir, recursive: true); } catch (IOException) { /* ignore */ }
    }

    [Fact]
    public void GetPathRelativeToContentRoot_PreservesNestedSeasonFolders()
    {
        var relative = CreateStrmFilesPostProcessor.GetPathRelativeToContentRoot(
            "/content/tv/Show/Season 01/ep.mkv");
        Assert.Equal(Path.Join("tv", "Show", "Season 01", "ep.mkv"), relative);
    }

    [Fact]
    public async Task CreateStrmFilesAsync_CreatesForPersistedVideosNotInAddedState()
    {
        var category = SeedDirectory(DavItem.ContentFolder, "tv");
        var job = SeedDirectory(category, "Show", _historyItemId);
        var season = SeedDirectory(job, "Season 01", _historyItemId);
        for (var i = 1; i <= 8; i++)
            SeedVideo(season, $"S01E{i:00}.mkv", _historyItemId);

        await _context.SaveChangesAsync();
        // Clear tracker so items are Unchanged, not Added.
        _context.ChangeTracker.Clear();

        var processor = new CreateStrmFilesPostProcessor(_config, _dbClient, _historyItemId);
        await processor.CreateStrmFilesAsync();

        var strmFiles = Directory.GetFiles(_strmDir, "*.strm", SearchOption.AllDirectories);
        Assert.Equal(8, strmFiles.Length);
    }

    [Fact]
    public async Task CreateStrmFilesAsync_SkipsExistingStrmNamedItems()
    {
        var category = SeedDirectory(DavItem.ContentFolder, "tv");
        var job = SeedDirectory(category, "Show", _historyItemId);
        SeedVideo(job, "episode.mkv", _historyItemId);
        SeedVideo(job, "already.strm", _historyItemId);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var processor = new CreateStrmFilesPostProcessor(_config, _dbClient, _historyItemId);
        await processor.CreateStrmFilesAsync();

        var strmFiles = Directory.GetFiles(_strmDir, "*.strm", SearchOption.AllDirectories);
        Assert.Single(strmFiles);
        Assert.DoesNotContain(strmFiles, f => f.EndsWith("already.strm.strm", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CreateStrmFilesAsync_IsIdempotentWhenContentUnchanged()
    {
        var category = SeedDirectory(DavItem.ContentFolder, "movies");
        var job = SeedDirectory(category, "Movie", _historyItemId);
        SeedVideo(job, "movie.mkv", _historyItemId);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var processor = new CreateStrmFilesPostProcessor(_config, _dbClient, _historyItemId);
        await processor.CreateStrmFilesAsync();
        var path = Directory.GetFiles(_strmDir, "*.strm", SearchOption.AllDirectories).Single();
        var mtime1 = File.GetLastWriteTimeUtc(path);
        await Task.Delay(50);
        await processor.CreateStrmFilesAsync();
        var mtime2 = File.GetLastWriteTimeUtc(path);

        Assert.Equal(mtime1, mtime2);
    }

    [Fact]
    public async Task CreateStrmFilesAsync_RollsBackEarlierFilesWhenLaterWriteFails()
    {
        var category = SeedDirectory(DavItem.ContentFolder, "tv");
        var job = SeedDirectory(category, "Show", _historyItemId);
        var second = SeedVideo(job, "second.mkv", _historyItemId);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
        // Added items are collected before persisted rows, so this write always
        // succeeds first and must be rolled back when the later destination fails.
        var first = SeedVideo(job, "first.mkv", _historyItemId);

        var secondPath = CreateStrmFilesPostProcessor.GetStrmFilePath(_config, second);
        Directory.CreateDirectory(secondPath);

        var processor = new CreateStrmFilesPostProcessor(_config, _dbClient, _historyItemId);
        await Assert.ThrowsAnyAsync<Exception>(() => processor.CreateStrmFilesAsync());

        Assert.False(File.Exists(CreateStrmFilesPostProcessor.GetStrmFilePath(_config, first)));
        Assert.True(Directory.Exists(secondPath));
    }

    [Fact]
    public async Task DeleteStrmFile_UsesPersistedPathAfterCompletedDirectoryChanges()
    {
        var davItem = new DavItem
        {
            Id = Guid.NewGuid(),
            Name = "episode.mkv",
            Type = DavItem.ItemType.UsenetFile,
            Path = "/content/tv/Show/episode.mkv",
        };
        await CreateStrmFilesPostProcessor.WriteStrmFileAsync(_config, davItem, forceRewrite: false);
        var originalPath = davItem.GeneratedStrmPath;
        Assert.True(File.Exists(originalPath));

        var movedDir = Path.Join(Path.GetTempPath(), $"strm-moved-{Guid.NewGuid():N}");
        Directory.CreateDirectory(movedDir);
        _config.UpdateValues(
        [
            new() { ConfigName = ConfigKeys.ApiCompletedDownloadsDir, ConfigValue = movedDir },
        ]);

        CreateStrmFilesPostProcessor.DeleteStrmFile(davItem);

        Assert.False(File.Exists(originalPath));
        try { Directory.Delete(movedDir, recursive: true); } catch (IOException) { /* best effort */ }
    }

    [Fact]
    public async Task DeleteStrmFile_RemovesOwnedSidecarAndEmptyDirectories()
    {
        var davItem = new DavItem
        {
            Id = Guid.NewGuid(),
            Name = "episode.mkv",
            Type = DavItem.ItemType.UsenetFile,
            Path = "/content/tv/Show/Season 01/episode.mkv",
        };
        var strmPath = CreateStrmFilesPostProcessor.GetStrmFilePath(_config, davItem);
        await CreateStrmFilesPostProcessor.WriteStrmFileAsync(_config, davItem, forceRewrite: false);

        CreateStrmFilesPostProcessor.DeleteStrmFile(davItem);

        Assert.False(File.Exists(strmPath));
        Assert.False(Directory.Exists(Path.GetDirectoryName(strmPath)));
    }

    [Fact]
    public async Task DeleteStrmFile_PreservesUnrelatedOrMissingSidecars()
    {
        var davItem = new DavItem
        {
            Id = Guid.NewGuid(),
            Name = "movie.mkv",
            Type = DavItem.ItemType.UsenetFile,
            Path = "/content/movies/Movie/movie.mkv",
        };
        var strmPath = CreateStrmFilesPostProcessor.GetStrmFilePath(_config, davItem);
        Directory.CreateDirectory(Path.GetDirectoryName(strmPath)!);
        var otherItem = new DavItem
        {
            Id = Guid.NewGuid(),
            Name = "other.mkv",
            Type = DavItem.ItemType.UsenetFile,
            Path = "/content/movies/Other/other.mkv",
        };
        await File.WriteAllTextAsync(
            strmPath,
            CreateStrmFilesPostProcessor.GetStrmTargetUrl(_config, otherItem));

        CreateStrmFilesPostProcessor.DeleteStrmFile(davItem);
        CreateStrmFilesPostProcessor.DeleteStrmFile(davItem);

        Assert.True(File.Exists(strmPath));
    }

    [Fact]
    public async Task DeleteStrmFile_PreservesSidecarOutsideCompletedDownloadsDirectory()
    {
        var davItem = new DavItem
        {
            Id = Guid.NewGuid(),
            Name = $"escape-{Guid.NewGuid():N}.mkv",
            Type = DavItem.ItemType.UsenetFile,
            Path = $"/content/../escape-{Guid.NewGuid():N}.mkv",
        };
        var strmPath = Path.GetFullPath(CreateStrmFilesPostProcessor.GetStrmFilePath(_config, davItem));
        try
        {
            await File.WriteAllTextAsync(
                strmPath,
                CreateStrmFilesPostProcessor.GetStrmTargetUrl(_config, davItem));

            CreateStrmFilesPostProcessor.DeleteStrmFile(davItem);

            Assert.True(File.Exists(strmPath));
        }
        finally
        {
            try { File.Delete(strmPath); } catch (IOException) { /* best effort */ }
        }
    }

    [Fact]
    public async Task DeleteStrmFile_PreservesSidecarBehindSymlinkedDirectory()
    {
        var davItem = new DavItem
        {
            Id = Guid.NewGuid(),
            Name = "episode.mkv",
            Type = DavItem.ItemType.UsenetFile,
            Path = "/content/tv/Show/episode.mkv",
        };
        var outsideDirectory = Path.Join(Path.GetTempPath(), $"strm-outside-{Guid.NewGuid():N}");
        var symlinkPath = Path.Join(_strmDir, "tv");
        var strmPath = CreateStrmFilesPostProcessor.GetStrmFilePath(_config, davItem);
        try
        {
            Directory.CreateDirectory(Path.Join(outsideDirectory, "Show"));
            Directory.CreateSymbolicLink(symlinkPath, outsideDirectory);
            await File.WriteAllTextAsync(
                strmPath,
                CreateStrmFilesPostProcessor.GetStrmTargetUrl(_config, davItem));

            CreateStrmFilesPostProcessor.DeleteStrmFile(davItem);

            Assert.True(File.Exists(strmPath));
        }
        finally
        {
            try { Directory.Delete(outsideDirectory, recursive: true); } catch (IOException) { /* best effort */ }
        }
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

    private DavItem SeedVideo(DavItem parent, string name, Guid historyItemId)
    {
        var item = DavItem.New(
            Guid.NewGuid(), parent, name, 100,
            DavItem.ItemType.UsenetFile, DavItem.ItemSubType.NzbFile,
            null, null, historyItemId, Guid.NewGuid());
        _context.Items.Add(item);
        return item;
    }
}
