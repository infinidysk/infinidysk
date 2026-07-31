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
}
