using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Queue.PostProcessors;
using NzbWebDAV.Services;

namespace NzbWebDAV.Tests.Services;

public sealed class HistoryCleanupServiceTests : IDisposable
{
    private readonly string _databasePath =
        Path.Join(Path.GetTempPath(), $"nzbdav-history-cleanup-{Guid.NewGuid():N}.sqlite");
    private readonly string _strmDirectory =
        Path.Join(Path.GetTempPath(), $"nzbdav-history-cleanup-strm-{Guid.NewGuid():N}");
    private readonly DavDatabaseContext _context;
    private readonly ConfigManager _config;

    public HistoryCleanupServiceTests()
    {
        _context = new DavDatabaseContext(new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite($"Data Source={_databasePath}")
            .Options);
        _context.Database.EnsureCreated();
        Directory.CreateDirectory(_strmDirectory);

        _config = new ConfigManager();
        _config.UpdateValues(
        [
            new() { ConfigName = ConfigKeys.ApiCompletedDownloadsDir, ConfigValue = _strmDirectory },
            new() { ConfigName = ConfigKeys.GeneralBaseUrl, ConfigValue = "http://localhost:3000" },
            new() { ConfigName = ConfigKeys.ApiStrmKey, ConfigValue = "test-strm-key" },
        ]);
    }

    public void Dispose()
    {
        _context.Dispose();
        try { File.Delete(_databasePath); } catch (IOException) { /* best effort */ }
        try { Directory.Delete(_strmDirectory, recursive: true); } catch (IOException) { /* best effort */ }
    }

    [Fact]
    public async Task ProcessNextItemAsync_DeleteMountedFilesRemovesOwnedStrmSidecars()
    {
        var historyItemId = Guid.NewGuid();
        var firstVideo = NewVideo(historyItemId, "tv/Show/S01E01.mkv");
        var secondVideo = NewVideo(historyItemId, "tv/Show/S01E02.mkv");
        _context.Items.AddRange(firstVideo, secondVideo);
        _context.HistoryCleanupItems.Add(new HistoryCleanupItem
        {
            Id = historyItemId,
            DeleteMountedFiles = true,
        });
        await _context.SaveChangesAsync();

        await CreateStrmFilesPostProcessor.WriteStrmFileAsync(_config, firstVideo, forceRewrite: false);
        await CreateStrmFilesPostProcessor.WriteStrmFileAsync(_config, secondVideo, forceRewrite: false);
        var firstStrmPath = CreateStrmFilesPostProcessor.GetStrmFilePath(_config, firstVideo);
        var secondStrmPath = CreateStrmFilesPostProcessor.GetStrmFilePath(_config, secondVideo);

        var processed = await HistoryCleanupService.ProcessNextItemAsync(_context, _config);

        Assert.True(processed);
        Assert.False(File.Exists(firstStrmPath));
        Assert.False(File.Exists(secondStrmPath));
        Assert.False(await _context.Items.AsNoTracking().AnyAsync(x => x.HistoryItemId == historyItemId));
        Assert.False(await _context.HistoryCleanupItems.AsNoTracking().AnyAsync(x => x.Id == historyItemId));
    }

    [Fact]
    public async Task ProcessNextItemAsync_KeepMountedFilesPreservesStrmSidecars()
    {
        var historyItemId = Guid.NewGuid();
        var video = NewVideo(historyItemId, "movies/Movie/movie.mkv");
        _context.Items.Add(video);
        _context.HistoryCleanupItems.Add(new HistoryCleanupItem
        {
            Id = historyItemId,
            DeleteMountedFiles = false,
        });
        await _context.SaveChangesAsync();

        await CreateStrmFilesPostProcessor.WriteStrmFileAsync(_config, video, forceRewrite: false);
        var strmPath = CreateStrmFilesPostProcessor.GetStrmFilePath(_config, video);

        var processed = await HistoryCleanupService.ProcessNextItemAsync(_context, _config);

        Assert.True(processed);
        Assert.True(File.Exists(strmPath));
        var retainedItem = await _context.Items.AsNoTracking().SingleAsync(x => x.Id == video.Id);
        Assert.Null(retainedItem.HistoryItemId);
        Assert.False(await _context.HistoryCleanupItems.AsNoTracking().AnyAsync(x => x.Id == historyItemId));
    }

    private static DavItem NewVideo(Guid historyItemId, string relativePath)
    {
        var id = Guid.NewGuid();
        var name = Path.GetFileName(relativePath);
        return new DavItem
        {
            Id = id,
            IdPrefix = id.ToString("N")[..DavItem.IdPrefixLength],
            Name = name,
            Type = DavItem.ItemType.UsenetFile,
            SubType = DavItem.ItemSubType.NzbFile,
            Path = $"/content/{relativePath}",
            HistoryItemId = historyItemId,
        };
    }
}
