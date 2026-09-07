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
            NewHistoryItem("movies"),
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
            NewHistoryItem("tv"),
            new DavItem { Name = "Show" },
            config);

        Assert.Equal(Path.Join("/mnt/jellyfin", "tv", "Show"), payload.DownloadPath);
    }

    [Fact]
    public void CompletedWithoutDownloadFolder_ReportsFailedWithReason()
    {
        var payload = HistoryItemAddedPayload.FromHistoryItem(
            NewHistoryItem("movies", HistoryItem.DownloadStatusOption.Completed),
            downloadFolder: null,
            new ConfigManager());

        Assert.Equal(HistoryItem.DownloadStatusOption.Failed, payload.Status);
        Assert.Equal(HistoryItemAddedPayload.DownloadFolderMissingFailMessage, payload.FailMessage);
        Assert.Null(payload.DownloadPath);
    }

    [Fact]
    public void CompletedWithDownloadFolder_StaysCompleted()
    {
        var payload = HistoryItemAddedPayload.FromHistoryItem(
            NewHistoryItem("movies", HistoryItem.DownloadStatusOption.Completed),
            new DavItem { Name = "Movie" },
            new ConfigManager());

        Assert.Equal(HistoryItem.DownloadStatusOption.Completed, payload.Status);
        Assert.Equal("", payload.FailMessage);
    }

    [Fact]
    public void FailedWithoutDownloadFolder_KeepsOriginalFailMessage()
    {
        var historyItem = NewHistoryItem("movies", HistoryItem.DownloadStatusOption.Failed);
        historyItem.FailMessage = "Missing articles.";

        var payload = HistoryItemAddedPayload.FromHistoryItem(historyItem, downloadFolder: null, new ConfigManager());

        Assert.Equal(HistoryItem.DownloadStatusOption.Failed, payload.Status);
        Assert.Equal("Missing articles.", payload.FailMessage);
    }

    private static HistoryItem NewHistoryItem(
        string category,
        HistoryItem.DownloadStatusOption status = HistoryItem.DownloadStatusOption.Completed) => new()
    {
        Id = Guid.NewGuid(),
        FileName = "release.nzb",
        JobName = "release",
        Category = category,
        DownloadStatus = status,
    };
}
