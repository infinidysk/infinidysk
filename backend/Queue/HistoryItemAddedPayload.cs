using System.Text.Json.Serialization;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Queue;

/// <summary>
/// Websocket payload for <c>HistoryItemAdded</c>. JSON names match SAB history slots.
/// </summary>
public sealed class HistoryItemAddedPayload
{
    public const string DownloadFolderMissingFailMessage = "Download folder no longer exists.";

    [JsonPropertyName("nzo_id")] public string NzoId { get; init; } = null!;
    [JsonPropertyName("nzb_name")] public string NzbName { get; init; } = null!;
    [JsonPropertyName("name")] public string JobName { get; init; } = null!;
    [JsonPropertyName("category")] public string Category { get; init; } = null!;
    [JsonPropertyName("status")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public HistoryItem.DownloadStatusOption Status { get; init; }
    [JsonPropertyName("bytes")] public long SizeInBytes { get; init; }
    [JsonPropertyName("storage")] public string? DownloadPath { get; init; }
    [JsonPropertyName("download_time")] public int DownloadTimeSeconds { get; init; }
    [JsonPropertyName("completed")] public long Completed { get; init; }
    [JsonPropertyName("fail_message")] public string FailMessage { get; init; } = null!;
    [JsonPropertyName("nzb_blob_id")] public string? NzbBlobId { get; init; }
    [JsonPropertyName("indexer")] public string? Indexer { get; init; }
    [JsonPropertyName("providers")] public List<QueueItemAddedPayload.ProviderUsage>? Providers { get; init; }

    public static HistoryItemAddedPayload FromHistoryItem(
        HistoryItem historyItem,
        DavItem? downloadFolder,
        ConfigManager configManager,
        IReadOnlyDictionary<string, long>? providerUsage = null,
        IReadOnlyDictionary<string, (string Host, string? Nickname)>? displayByMetricsKey = null)
    {
        // A Completed slot with an empty `storage` is a state SABnzbd never produces; Sonarr/Radarr
        // warn "Download doesn't contain intermediate path" forever instead of handling it.
        var downloadFolderMissing = IsDownloadFolderMissing(historyItem, downloadFolder);
        return new HistoryItemAddedPayload
        {
            NzoId = historyItem.Id.ToString(),
            NzbName = historyItem.FileName,
            JobName = historyItem.JobName,
            Category = historyItem.Category,
            Status = downloadFolderMissing
                ? HistoryItem.DownloadStatusOption.Failed
                : historyItem.DownloadStatus,
            SizeInBytes = historyItem.TotalSegmentBytes,
            DownloadPath = GetDownloadPath(historyItem, downloadFolder, configManager),
            DownloadTimeSeconds = historyItem.DownloadTimeSeconds,
            Completed = new DateTimeOffset(historyItem.CreatedAt).ToUnixTimeSeconds(),
            FailMessage = downloadFolderMissing
                ? DownloadFolderMissingFailMessage
                : historyItem.FailMessage ?? "",
            NzbBlobId = historyItem.NzbBlobId?.ToString(),
            Indexer = historyItem.IndexerName,
            Providers = QueueItemAddedPayload.MapProviders(providerUsage, displayByMetricsKey),
        };
    }

    public static bool IsDownloadFolderMissing(HistoryItem historyItem, DavItem? downloadFolder) =>
        historyItem.DownloadStatus == HistoryItem.DownloadStatusOption.Completed && downloadFolder == null;

    private static string? GetDownloadPath(
        HistoryItem historyItem,
        DavItem? downloadFolder,
        ConfigManager configManager)
    {
        if (downloadFolder == null) return null;
        var importStrategy = configManager.GetImportStrategy();
        if (importStrategy == "strm")
        {
            return Path.Join(
                configManager.GetStrmCompletedDownloadDir(),
                historyItem.Category,
                downloadFolder.Name);
        }

        if (importStrategy == "symlinks")
        {
            return Path.Join(
                configManager.GetSymlinkCompletedDownloadDir(),
                historyItem.Category,
                downloadFolder.Name);
        }

        throw new InvalidOperationException("Unknown import strategy");
    }
}
