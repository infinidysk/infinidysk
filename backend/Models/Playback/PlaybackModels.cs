
using System.Text.Json.Serialization;

namespace NzbWebDAV.Models.Playback;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PlaybackSourceType
{
    Plex,
    Emby,
    Jellyfin,
    InfiniDysk,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PlaybackState
{
    Unknown,
    Playing,
    Paused,
    Buffering,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PlaybackDeliveryMethod
{
    Unknown,
    DirectPlay,
    DirectStream,
    Transcode,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PlaybackFreshness
{
    Fresh,
    Stale,
}

public readonly record struct PlaybackSessionKey(Guid SourceInstanceId, string NativeSessionId);

public sealed record PlaybackObservation
{
    public required string NativeSessionId { get; init; }
    public string? UserName { get; init; }
    public string? ClientName { get; init; }
    public string? DeviceName { get; init; }
    public string? ItemId { get; init; }
    public string? Title { get; init; }
    public string? MediaType { get; init; }
    public string? SeriesName { get; init; }
    public int? SeasonNumber { get; init; }
    public int? EpisodeNumber { get; init; }
    public PlaybackState State { get; init; }
    public long? PositionMs { get; init; }
    public long? DurationMs { get; init; }
    public PlaybackDeliveryMethod DeliveryMethod { get; init; }
    public string? MediaSourceId { get; init; }
    public string? MediaSourcePath { get; init; }
}

public sealed record MappedPlaybackObservation(PlaybackObservation Observation, Guid DavItemId);

public sealed record AuthoritativePlaybackSession
{
    public required PlaybackSessionKey Key { get; init; }
    public required string SourceInstanceName { get; init; }
    public required PlaybackSourceType SourceType { get; init; }
    public required string NativeSessionId { get; init; }
    public string? UserName { get; init; }
    public string? ClientName { get; init; }
    public string? DeviceName { get; init; }
    public string? ItemId { get; init; }
    public string? Title { get; init; }
    public string? MediaType { get; init; }
    public string? SeriesName { get; init; }
    public int? SeasonNumber { get; init; }
    public int? EpisodeNumber { get; init; }
    public PlaybackState State { get; init; }
    public long? PositionMs { get; init; }
    public long? DurationMs { get; init; }
    public PlaybackDeliveryMethod DeliveryMethod { get; init; }
    public string? MediaSourceId { get; init; }
    public string? MediaSourcePath { get; init; }
    public Guid DavItemId { get; init; }
    public DateTimeOffset LastConfirmedAt { get; init; }
    public PlaybackFreshness Freshness { get; init; }
}

public sealed record PlaybackAuthoritySnapshot
{
    public Guid SourceInstanceId { get; init; }
    public required string SourceInstanceName { get; init; }
    public PlaybackSourceType SourceType { get; init; }
    public bool Available { get; init; }
    public bool IsStale { get; init; }
    public DateTimeOffset? LastSuccessfulPollAt { get; init; }
    public DateTimeOffset? LastFailureAt { get; init; }
    public string? LastErrorKind { get; init; }
}
