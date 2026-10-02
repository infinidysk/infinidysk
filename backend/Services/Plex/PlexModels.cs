namespace NzbWebDAV.Services.Plex;

public sealed record PlexPathMapping(string PlexPath, string? DavPath, string? LocalPath = null);

public sealed record PlexServer
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Url { get; init; } = "";
    public string Token { get; init; } = "";
    public bool Enabled { get; init; } = true;
    public string? AccountId { get; init; }
    public IReadOnlyList<PlexPathMapping> PathMappings { get; init; } = [];
    public override string ToString() => $"PlexServer {{ Id = {Id}, credentials = [redacted] }}";
}

public sealed record PlexLibrary(string Id, string Title, string Type);
public sealed record PlexUser(string Id, string Name);
public sealed record PlexHomeUser(string Id, string Name, bool Protected, bool Admin);
public sealed record PlexAccount(string Id, string Name, string Token)
{
    public override string ToString() => "PlexAccount { credentials = [redacted] }";
}
public sealed record PlexSource(string ServerId, string? LibraryId, string Kind, string Id, string Key, string Title, string Type);
public sealed record PlexMediaItem(string RatingKey, string Type, string Title, string? ShowRatingKey,
    int? Season, int? Episode, string? File, long ViewOffset, long Duration, long? ViewedAt, string? UserId = null)
{
    public string? WatchStateUserId { get; init; }
    public string? MappingStatus { get; init; }
    public string? MappingReason { get; init; }
}
public sealed record PlexSession(string Id, string UserId, string State, string? File, PlexMediaItem Item);
public sealed record PlexIdentity(string MachineIdentifier, string Version);
public sealed record PlexConnection(string Uri, bool Local, bool Relay);
public sealed record PlexDiscoveredServer(string Id, string Name, string Token, IReadOnlyList<PlexConnection> Connections, string? AccountId = null)
{
    public override string ToString() => $"PlexDiscoveredServer {{ Id = {Id}, credentials = [redacted] }}";
}
public sealed record PlexPin(int Id, string Code, int ExpiresIn, string? AuthToken)
{
    public override string ToString() => "PlexPin { credentials = [redacted] }";
}
public sealed record PlexLoginStart(string Handle, string Url, DateTimeOffset ExpiresAt)
{
    public override string ToString() => "PlexLoginStart { login details = [redacted] }";
}
public sealed record PlexLoginStatus(string State, DateTimeOffset? ExpiresAt);
public sealed record PlexServerHandle(string Handle, string Id, string Name, IReadOnlyList<PlexConnection> Connections);

public sealed class PlexRequestException(string message) : Exception(message);
