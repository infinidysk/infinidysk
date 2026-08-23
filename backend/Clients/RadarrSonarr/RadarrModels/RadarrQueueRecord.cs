using System.Text.Json.Serialization;
using NzbWebDAV.Clients.RadarrSonarr.BaseModels;

namespace NzbWebDAV.Clients.RadarrSonarr.RadarrModels;

public class RadarrQueueRecord : ArrQueueRecord
{
    [JsonPropertyName("movieId")]
    public int MovieId { get; set; }

    public override string? GetMediaIdentity() => MovieId > 0 ? $"movie:{MovieId}" : null;
}
