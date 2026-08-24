using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Queue;

namespace NzbWebDAV.Tests.Queue;

public sealed class HistoryItemAddedPayloadTests
{
    [Fact]
    public void DualOutput_ReportsOnlyPrimarySymlinkPath()
    {
        var config = new ConfigManager();
        config.UpdateValues(
        [
            new() { ConfigName = ConfigKeys.ApiSymlinkOutputDir, ConfigValue = "/mnt/plex" },
            new() { ConfigName = ConfigKeys.ApiStrmOutputEnabled, ConfigValue = "true" },
            new() { ConfigName = ConfigKeys.ApiCompletedDownloadsDir, ConfigValue = "/mnt/jellyfin" },
        ]);

        var payload = HistoryItemAddedPayload.FromHistoryItem(
            HistoryItem("movies"),
            new DavItem { Name = "Movie" },
            config);

        Assert.Equal(Path.Join("/mnt/plex", "movies", "Movie"), payload.DownloadPath);
    }

    [Fact]
    public void DualOutput_ReportsOnlyPrimaryStrmPath()
    {
        var config = new ConfigManager();
        config.UpdateValues(
        [
            new() { ConfigName = ConfigKeys.ApiImportStrategy, ConfigValue = "strm" },
            new() { ConfigName = ConfigKeys.ApiSymlinkOutputEnabled, ConfigValue = "true" },
            new() { ConfigName = ConfigKeys.ApiSymlinkOutputDir, ConfigValue = "/mnt/plex" },
            new() { ConfigName = ConfigKeys.ApiCompletedDownloadsDir, ConfigValue = "/mnt/jellyfin" },
        ]);

        var payload = HistoryItemAddedPayload.FromHistoryItem(
            HistoryItem("tv"),
            new DavItem { Name = "Show" },
            config);

        Assert.Equal(Path.Join("/mnt/jellyfin", "tv", "Show"), payload.DownloadPath);
    }

    private static HistoryItem HistoryItem(string category) => new()
    {
        Id = Guid.NewGuid(),
        FileName = "release.nzb",
        JobName = "release",
        Category = category,
    };
}
