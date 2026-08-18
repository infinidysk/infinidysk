using System.Text.Json.Serialization;

namespace NzbWebDAV.Api.Controllers.ResetHealthCheckQueue;

public class ResetHealthCheckQueueResponse : BaseApiResponse
{
    [JsonPropertyName("resetCount")]
    public required int ResetCount { get; init; }
}
