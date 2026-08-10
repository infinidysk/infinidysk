using System.Text.Json.Serialization;

namespace NzbWebDAV.Api.SabControllers.GetWarnings;

public class GetWarningsResponse
{
    [JsonPropertyName("warnings")]
    public List<WarningItem> Warnings { get; set; } = new();

    public class WarningItem
    {
        [JsonPropertyName("type")]
        public required string Type { get; init; }

        [JsonPropertyName("text")]
        public required string Text { get; init; }

        [JsonPropertyName("time")]
        public required long Time { get; init; }
    }
}
