using System.Text.Json;
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

    /// <summary>
    /// Used to grace-period an orphaned (no matching series/movie) record before it is
    /// eligible for removal, so a series/movie re-added moments after deletion still has
    /// a chance to reclaim its own in-flight download. Nullable defensively: an absent or
    /// unparseable value should not fail the whole queue response, and is treated as
    /// "old enough" by callers.
    /// </summary>
    [JsonPropertyName("added")]
    [JsonConverter(typeof(TolerantDateTimeJsonConverter))]
    public DateTime? Added { get; set; }

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

/// <summary>
/// Maps an unparseable or unexpected <c>added</c> value to null instead of throwing. A plain
/// <see cref="DateTime"/>? already tolerates a missing or null field, but System.Text.Json still
/// throws on a malformed date string — and a single bad record would fail the whole queue response,
/// which is the blindness this PR exists to remove.
/// </summary>
public sealed class TolerantDateTimeJsonConverter : JsonConverter<DateTime?>
{
    public override DateTime? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.String when reader.TryGetDateTime(out var value) => value,
            _ => null,
        };

    public override void Write(Utf8JsonWriter writer, DateTime? value, JsonSerializerOptions options)
    {
        if (value is { } dt) writer.WriteStringValue(dt);
        else writer.WriteNullValue();
    }
}
