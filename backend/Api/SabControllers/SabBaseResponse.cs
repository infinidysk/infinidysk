using System.Text.Json.Serialization;

namespace NzbWebDAV.Api.SabControllers;

public class SabBaseResponse
{
    public bool Status { get; set; } = true;
    public string? Error { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Problem { get; set; }
}
