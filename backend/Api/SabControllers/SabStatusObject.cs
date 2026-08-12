using System.Text.Json.Serialization;

namespace NzbWebDAV.Api.SabControllers;

public sealed class SabStatusObject
{
    [JsonPropertyName("completedir")]
    public required string CompleteDir { get; init; }

    [JsonPropertyName("paused")]
    public bool Paused { get; init; }

    [JsonPropertyName("speedlimit")]
    public string SpeedLimit { get; init; } = "0";

    [JsonPropertyName("speedlimit_abs")]
    public string SpeedLimitAbs { get; init; } = "0";
}
