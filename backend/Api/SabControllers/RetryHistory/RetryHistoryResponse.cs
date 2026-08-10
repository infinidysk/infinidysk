using System.Text.Json.Serialization;

namespace NzbWebDAV.Api.SabControllers.RetryHistory;

public class RetryHistoryResponse : SabBaseResponse
{
    [JsonPropertyName("nzo_id")]
    public string? NzoId { get; set; }

    [JsonPropertyName("nzo_ids")]
    public List<string>? NzoIds { get; set; }

    [JsonPropertyName("failed")]
    public List<RetryHistoryFailedItem>? Failed { get; set; }
}

public class RetryHistoryFailedItem
{
    [JsonPropertyName("nzo_id")]
    public string NzoId { get; set; } = null!;

    [JsonPropertyName("error")]
    public string Error { get; set; } = null!;
}
