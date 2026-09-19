namespace NzbWebDAV.Models.Playback;

public enum TransportCorrelationScope
{
    None,
    Session,
    File,
}

public sealed record CurrentActivitySnapshot
{
    public required IReadOnlyList<CurrentPlaybackActivity> Playback { get; init; }
    public required IReadOnlyList<CurrentTransportActivity> Reads { get; init; }
    public required IReadOnlyList<PlaybackAuthoritySnapshot> Authorities { get; init; }
}

public sealed record CurrentPlaybackActivity
{
    public required AuthoritativePlaybackSession Session { get; init; }
    public required IReadOnlyList<Guid> TransportReadIds { get; init; }
    public bool HasSharedFileTransport { get; init; }
}

public sealed record CurrentTransportActivity
{
    public Guid Id { get; init; }
    public Guid? DavItemId { get; init; }
    public required string FileName { get; init; }
    public required string Path { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset LastActivityAt { get; init; }
    public long BytesRead { get; init; }
    public long BytesFetched { get; init; }
    public long SourceOffset { get; init; }
    public long? FileSize { get; init; }
    public string? ClientIp { get; init; }
    public string? ClientUserAgent { get; init; }
    public string? PlayerSession { get; init; }
    public TransportCorrelationScope CorrelationScope { get; init; }
    public int MatchingPlaybackSessionCount { get; init; }
    public bool Shared { get; init; }
    public required IReadOnlyList<CurrentProviderContribution> Providers { get; init; }
}

public sealed record CurrentProviderContribution
{
    public required string Host { get; init; }
    public string? Nickname { get; init; }
    public long Segments { get; init; }
}
