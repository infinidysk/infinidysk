using System.Text.Json.Serialization;

namespace NzbWebDAV.Clients.RadarrSonarr.BaseModels;

public class ArrQueueRecord
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("protocol")]
    public string? Protocol { get; set; }

    [JsonPropertyName("downloadClient")]
    public string? DownloadClient { get; set; }

    [JsonPropertyName("indexer")]
    public string? Indexer { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("trackedDownloadStatus")]
    public string? TrackedDownloadStatus { get; set; }

    [JsonPropertyName("trackedDownloadState")]
    public string? TrackedDownloadState { get; set; }

    [JsonPropertyName("statusMessages")]
    public List<ArrQueueStatusMessage> StatusMessages { get; set; } = [];

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("sizeleft")]
    public long Sizeleft { get; set; }

    [JsonPropertyName("downloadId")]
    public string? DownloadId { get; set; }

    public bool IsAwaitingImport =>
        string.Equals(Status, "completed", StringComparison.OrdinalIgnoreCase)
        || string.Equals(TrackedDownloadState, "importPending", StringComparison.OrdinalIgnoreCase)
        || string.Equals(TrackedDownloadState, "importing", StringComparison.OrdinalIgnoreCase);

    public bool HasStatusMessage(string message)
    {
        return GetMatchingStatusMessages([message]).Count > 0;
    }

    /// <summary>
    /// Returns the original Arr status text, rather than the configured substring
    /// that matched it, so callers can give operators the actionable import reason.
    /// </summary>
    public IReadOnlyList<string> GetMatchingStatusMessages(IEnumerable<string> messages)
    {
        var configuredMessages = messages
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToArray();
        if (configuredMessages.Length == 0) return [];

        return StatusMessages
            .SelectMany(x => x.Messages)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Where(status => configuredMessages.Any(message => status.Contains(message, StringComparison.Ordinal)))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    public virtual string? GetMediaIdentity() => null;
}
