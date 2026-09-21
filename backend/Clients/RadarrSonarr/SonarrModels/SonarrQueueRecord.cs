using System.Text.Json.Serialization;
using NzbWebDAV.Clients.RadarrSonarr.BaseModels;

namespace NzbWebDAV.Clients.RadarrSonarr.SonarrModels;

public class SonarrQueueRecord : ArrQueueRecord
{
    // Nullable: Sonarr sends "seriesId": null (and "episodeId": null) for a record it
    // can't match to a series — most commonly a still-downloading release whose series
    // was deleted from Sonarr. Non-nullable ints here would throw deserializing that
    // single record and take the whole /queue response down with it, silently blinding
    // ArrMonitoringService to every other record on the page.
    [JsonPropertyName("seriesId")]
    public int? SeriesId { get; set; }

    [JsonPropertyName("episodeId")]
    public int? EpisodeId { get; set; }

    // Nullable for the same reason: Sonarr's QueueResource declares this as `int?` too.
    [JsonPropertyName("seasonNumber")]
    public int? SeasonNumber { get; set; }

    public override string? GetMediaIdentity() => EpisodeId is > 0 ? $"episode:{EpisodeId}" : null;
}
