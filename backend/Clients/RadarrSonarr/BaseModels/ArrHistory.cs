using System.Text.Json;
using System.Text.Json.Serialization;

namespace NzbWebDAV.Clients.RadarrSonarr.BaseModels;

public class ArrHistory
{
    [JsonPropertyName("records")]
    public List<ArrHistoryRecord> Records { get; set; } = [];

    [JsonPropertyName("totalRecords")]
    public int TotalRecords { get; set; }
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
    [JsonConverter(typeof(ArrEventTypeJsonConverter))]
    public int EventType { get; set; }

    [JsonPropertyName("sourceTitle")]
    public string? SourceTitle { get; set; }

    [JsonPropertyName("data")]
    public ArrHistoryData Data { get; set; } = new();
}

public sealed class ArrHistoryData
{
    [JsonPropertyName("fileId")]
    public string? FileId { get; set; }

    [JsonPropertyName("importedPath")]
    public string? ImportedPath { get; set; }
}

public sealed class ArrEventTypeJsonConverter : JsonConverter<int>
{
    public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.Number when reader.TryGetInt32(out var value) => value,
            JsonTokenType.String => ParseEventType(reader.GetString()),
            _ => 0,
        };

    public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options) =>
        writer.WriteNumberValue(value);

    private static int ParseEventType(string? value) =>
        value switch
        {
            null => 0,
            _ when int.TryParse(value, out var numericValue) => numericValue,
            _ when value.Equals("grabbed", StringComparison.OrdinalIgnoreCase) => 1,
            _ when value.Equals("downloadFolderImported", StringComparison.OrdinalIgnoreCase) => 3,
            _ => 0,
        };
}
