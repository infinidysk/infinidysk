using System.Text.Json.Serialization;

namespace NzbWebDAV.Api.Controllers.JellyfinWebhook;

/// <summary>
/// A permissive subset of Jellyfin's webhook-plugin payload (verified against a real
/// "Playback Progress" / Item Type "Episodes" / Send All Properties capture). Every
/// field is nullable and unrecognized fields are ignored, so an unexpected payload
/// shape (different plugin version/template) degrades to a no-op instead of a crash.
/// </summary>
public class JellyfinWebhookPayload
{
    [JsonPropertyName("NotificationType")]
    public string? NotificationType { get; set; }

    [JsonPropertyName("ItemType")]
    public string? ItemType { get; set; }

    [JsonPropertyName("ItemId")]
    public string? ItemId { get; set; }

    [JsonPropertyName("SeriesName")]
    public string? SeriesName { get; set; }

    [JsonPropertyName("SeasonNumber")]
    public int? SeasonNumber { get; set; }

    [JsonPropertyName("EpisodeNumber")]
    public int? EpisodeNumber { get; set; }

    [JsonPropertyName("PlaybackPositionTicks")]
    public long? PlaybackPositionTicks { get; set; }

    [JsonPropertyName("RunTimeTicks")]
    public long? RunTimeTicks { get; set; }
}
