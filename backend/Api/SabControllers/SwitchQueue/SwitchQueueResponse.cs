using System.Text.Json.Serialization;

namespace NzbWebDAV.Api.SabControllers.SwitchQueue;

public sealed class SwitchQueueResponse
{
    [JsonPropertyName("result")]
    public ResultObject Result { get; init; } = new();

    public sealed class ResultObject
    {
        [JsonPropertyName("position")]
        public int Position { get; init; }

        [JsonPropertyName("priority")]
        public int Priority { get; init; }
    }
}
