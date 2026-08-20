using System.Text.Json.Serialization;

namespace NzbWebDAV.Clients.RadarrSonarr.BaseModels;

public class ArrHistory
{
    [JsonPropertyName("records")]
    public List<ArrHistoryRecord> Records { get; set; } = [];
}

public class ArrHistoryRecord
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("date")]
    public DateTimeOffset Date { get; set; }

    [JsonPropertyName("downloadId")]
    public string? DownloadId { get; set; }

    [JsonPropertyName("eventType")]
    public int EventType { get; set; }

    [JsonPropertyName("sourceTitle")]
    public string? SourceTitle { get; set; }
}
