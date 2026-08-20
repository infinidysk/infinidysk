using System.Text.Json.Serialization;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Queue;

namespace NzbWebDAV.Api.SabControllers.GetHistory;

public class GetHistoryResponse : SabBaseResponse
{
    [JsonPropertyName("history")]
    public HistoryObject History { get; set; } = null!;

    public class HistoryObject
    {
        [JsonPropertyName("slots")]
        public List<HistorySlot> Slots { get; set; } = null!;

        [JsonPropertyName("noofslots")]
        public int TotalCount { get; set; }
    }

    public class HistorySlot
    {
        [JsonPropertyName("nzo_id")]
        public string NzoId { get; set; } = null!;

        [JsonPropertyName("nzb_name")]
        public string NzbName { get; set; } = null!;

        [JsonPropertyName("name")]
        public string JobName { get; set; } = null!;

        [JsonPropertyName("category")]
        public string Category { get; set; } = null!;

        [JsonPropertyName("status")]
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public HistoryItem.DownloadStatusOption Status { get; set; }

        [JsonPropertyName("bytes")]
        public long SizeInBytes { get; set; }

        [JsonPropertyName("storage")]
        public string? DownloadPath { get; set; }

        [JsonPropertyName("download_time")]
        public int DownloadTimeSeconds { get; set; }

        [JsonPropertyName("completed")]
        public long Completed { get; set; }

        [JsonPropertyName("fail_message")]
        public string FailMessage { get; set; } = null!;

        [JsonPropertyName("nzb_blob_id")]
        public string? NzbBlobId { get; set; }

        [JsonPropertyName("indexer")]
        public string? Indexer { get; set; }

        [JsonPropertyName("providers")]
        public List<ProviderUsage>? Providers { get; set; }

        public static HistorySlot FromHistoryItem
        (
            HistoryItem historyItem,
            DavItem? downloadFolder,
            ConfigManager configManager,
            IReadOnlyDictionary<string, long>? providerUsage = null,
            IReadOnlyDictionary<string, (string Host, string? Nickname)>? displayByMetricsKey = null
        )
        {
            var payload = HistoryItemAddedPayload.FromHistoryItem(
                historyItem, downloadFolder, configManager, providerUsage, displayByMetricsKey);
            return new HistorySlot
            {
                NzoId = payload.NzoId,
                NzbName = payload.NzbName,
                JobName = payload.JobName,
                Category = payload.Category,
                Status = payload.Status,
                SizeInBytes = payload.SizeInBytes,
                DownloadPath = payload.DownloadPath,
                DownloadTimeSeconds = payload.DownloadTimeSeconds,
                Completed = payload.Completed,
                FailMessage = payload.FailMessage,
                NzbBlobId = payload.NzbBlobId,
                Indexer = payload.Indexer,
                Providers = payload.Providers?
                    .Select(p => new ProviderUsage
                    {
                        Host = p.Host,
                        Nickname = p.Nickname,
                        Segments = p.Segments,
                    })
                    .ToList(),
            };
        }

        public class ProviderUsage
        {
            [JsonPropertyName("host")] public required string Host { get; init; }
            [JsonPropertyName("nickname")] public string? Nickname { get; init; }
            [JsonPropertyName("segments")] public required long Segments { get; init; }
        }
    }
}
