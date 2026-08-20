using System.Text.Json.Serialization;

namespace NzbWebDAV.Logging;

public sealed class LogEntry
{
    [JsonPropertyName("seq")] public required long Sequence { get; init; }
    [JsonPropertyName("ts")] public required long TimestampUnixMs { get; init; }
    [JsonPropertyName("level")] public required string Level { get; init; }
    [JsonPropertyName("msg")] public required string Message { get; init; }
    [JsonPropertyName("source")] public string? Source { get; init; }
    [JsonPropertyName("exception")] public string? Exception { get; init; }
    [JsonPropertyName("traceId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TraceId { get; init; }
}
