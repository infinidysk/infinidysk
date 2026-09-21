using System.Text.Json.Serialization;
using NzbWebDAV.Clients.RadarrSonarr.BaseModels;

namespace NzbWebDAV.Clients.RadarrSonarr.RadarrModels;

public class RadarrQueueRecord : ArrQueueRecord
{
    // Nullable for the same reason as SonarrQueueRecord.SeriesId: Radarr sends
    // "movieId": null for a record it can't match to a movie (e.g. the movie was
    // deleted while its release was still downloading), and a non-nullable int would
    // throw deserializing that record and take the whole /queue response down with it.
    [JsonPropertyName("movieId")]
    public int? MovieId { get; set; }

    public override string? GetMediaIdentity() => MovieId is > 0 ? $"movie:{MovieId}" : null;
}
