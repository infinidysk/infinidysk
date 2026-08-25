using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Queue;

namespace NzbWebDAV.Tests.Queue;

public sealed class HistoryItemAddedPayloadTests
{
    [Fact]
    public void SymlinkStrategy_ReportsCompletedSymlinksPath()
    {
        var config = new ConfigManager();
        config.UpdateValues(
        [
            new() { ConfigName = ConfigKeys.RcloneMountDir, ConfigValue = "/mnt/nzbdav" },
        ]);

        var payload = HistoryItemAddedPayload.FromHistoryItem(
            HistoryItem("movies"),
            new DavItem { Name = "Movie" },
            config);

        Assert.Equal(
            Path.Join("/mnt/nzbdav", DavItem.SymlinkFolder.Name, "movies", "Movie"),
            payload.DownloadPath);
    }

    [Fact]
    public void StrmStrategy_ReportsCompletedDownloadsDir()
    {
        var config = new ConfigManager();
        config.UpdateValues(
        [
            new() { ConfigName = ConfigKeys.ApiImportStrategy, ConfigValue = "strm" },
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
