using System.Text.Json.Serialization;

namespace NzbWebDAV.Api.Controllers.RequeueActionNeededHealthChecks;

public class RequeueActionNeededHealthChecksResponse : BaseApiResponse
{
    [JsonPropertyName("requeuedCount")]
    public required int RequeuedCount { get; init; }
}
