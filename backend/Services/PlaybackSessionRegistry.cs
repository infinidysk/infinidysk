using NzbWebDAV.Config;
using NzbWebDAV.Models.Playback;

namespace NzbWebDAV.Services;

/// <summary>
/// In-memory authoritative playback state. Membership is driven by actual
/// player/media-server state, never by WebDAV byte flow.
/// </summary>
public sealed class PlaybackSessionRegistry
{
    internal static readonly Guid NativeExploreInstanceId =
        Guid.Parse("9f23ad65-cdaa-40fe-a5e4-c18fd77a1327");

    private readonly object _gate = new();
    private readonly Dictionary<PlaybackSessionKey, AuthoritativePlaybackSession> _sessions = new();
    private readonly Dictionary<Guid, PlaybackAuthoritySnapshot> _authorities = new();

    public PlaybackRegistrySnapshot CaptureSnapshot()
    {
        List<AuthoritativePlaybackSession> sessions;
        List<PlaybackAuthoritySnapshot> authorities;
        lock (_gate)
        {
            sessions = _sessions.Values.ToList();
            authorities = _authorities.Values.ToList();
        }

        return new PlaybackRegistrySnapshot(
            sessions
                .OrderBy(session => session.SourceInstanceName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(session => session.NativeSessionId, StringComparer.Ordinal)
                .ToList(),
            authorities
                .OrderBy(authority => authority.SourceInstanceName, StringComparer.OrdinalIgnoreCase)
                .ToList());
    }

    public IReadOnlyList<AuthoritativePlaybackSession> Snapshot()
    {
        List<AuthoritativePlaybackSession> sessions;
        lock (_gate)
            sessions = _sessions.Values.ToList();

        return sessions
            .OrderBy(session => session.SourceInstanceName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(session => session.NativeSessionId, StringComparer.Ordinal)
            .ToList();
    }

    public IReadOnlyList<PlaybackAuthoritySnapshot> AuthoritySnapshot()
    {
        List<PlaybackAuthoritySnapshot> authorities;
        lock (_gate)
            authorities = _authorities.Values.ToList();

        return authorities
            .OrderBy(authority => authority.SourceInstanceName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public void ReconcileExternal(
        MediaServerInstance instance,
        IReadOnlyList<MappedPlaybackObservation> mappedSessions,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(mappedSessions);

        var sourceType = ToPlaybackSourceType(instance.Type);
        var desired = new Dictionary<PlaybackSessionKey, AuthoritativePlaybackSession>();
        foreach (var mapped in mappedSessions)
        {
            var observation = mapped.Observation;
            if (string.IsNullOrWhiteSpace(observation.NativeSessionId)) continue;
            var key = new PlaybackSessionKey(instance.Id, observation.NativeSessionId);
            desired[key] = FromObservation(
                key, instance.Name, sourceType, observation, mapped.DavItemId, now, PlaybackFreshness.Fresh);
        }

        lock (_gate)
        {
            foreach (var key in _sessions.Keys
                         .Where(key => key.SourceInstanceId == instance.Id && !desired.ContainsKey(key))
                         .ToList())
            {
                _sessions.Remove(key);
            }

            foreach (var pair in desired)
                _sessions[pair.Key] = pair.Value;

            _authorities[instance.Id] = new PlaybackAuthoritySnapshot
            {
                SourceInstanceId = instance.Id,
                SourceInstanceName = instance.Name,
                SourceType = sourceType,
                Available = true,
                IsStale = false,
                LastSuccessfulPollAt = now,
            };
        }
    }

    public void MarkExternalFailure(
        MediaServerInstance instance,
        DateTimeOffset now,
        string errorKind)
    {
        var sourceType = ToPlaybackSourceType(instance.Type);
        lock (_gate)
        {
            foreach (var key in _sessions.Keys.Where(key => key.SourceInstanceId == instance.Id).ToList())
                _sessions[key] = _sessions[key] with { Freshness = PlaybackFreshness.Stale };

            _authorities.TryGetValue(instance.Id, out var previous);
            _authorities[instance.Id] = new PlaybackAuthoritySnapshot
            {
                SourceInstanceId = instance.Id,
                SourceInstanceName = instance.Name,
                SourceType = sourceType,
                Available = false,
                IsStale = true,
                LastSuccessfulPollAt = previous?.LastSuccessfulPollAt,
                LastFailureAt = now,
                LastErrorKind = errorKind,
            };
        }
    }

    public void ExpireStaleExternal(DateTimeOffset now, TimeSpan grace)
    {
        var cutoff = now - grace;
        lock (_gate)
        {
            foreach (var key in _sessions
                         .Where(pair =>
                             pair.Value.SourceType != PlaybackSourceType.InfiniDysk
                             && pair.Value.Freshness == PlaybackFreshness.Stale
                             && pair.Value.LastConfirmedAt < cutoff)
                         .Select(pair => pair.Key)
                         .ToList())
            {
                _sessions.Remove(key);
            }
        }
    }

    public void RemoveInstance(Guid instanceId)
    {
        lock (_gate)
        {
            foreach (var key in _sessions.Keys.Where(key => key.SourceInstanceId == instanceId).ToList())
                _sessions.Remove(key);
            _authorities.Remove(instanceId);
        }
    }

    public bool TryGetNativeDavItemId(string playerSession, out Guid davItemId)
    {
        davItemId = Guid.Empty;
        if (string.IsNullOrWhiteSpace(playerSession))
            return false;

        lock (_gate)
        {
            if (!_sessions.TryGetValue(
                    new PlaybackSessionKey(NativeExploreInstanceId, playerSession),
                    out var session))
                return false;

            davItemId = session.DavItemId;
            return davItemId != Guid.Empty;
        }
    }

    public void UpsertNative(
        string playerSession,
        Guid davItemId,
        string? title,
        string? mediaType,
        PlaybackState state,
        long? positionMs,
        long? durationMs,
        DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(playerSession))
            throw new ArgumentException("playerSession is required.", nameof(playerSession));
        if (davItemId == Guid.Empty)
            throw new ArgumentException("A native playback report requires an exact DavItemId.", nameof(davItemId));

        var key = new PlaybackSessionKey(NativeExploreInstanceId, playerSession);
        var session = new AuthoritativePlaybackSession
        {
            Key = key,
            SourceInstanceName = "InfiniDysk",
            SourceType = PlaybackSourceType.InfiniDysk,
            NativeSessionId = playerSession,
            ClientName = "Explore",
            Title = title,
            MediaType = mediaType,
            State = state,
            PositionMs = positionMs,
            DurationMs = durationMs,
            DeliveryMethod = PlaybackDeliveryMethod.DirectPlay,
            DavItemId = davItemId,
            LastConfirmedAt = now,
            Freshness = PlaybackFreshness.Fresh,
        };

        lock (_gate)
            _sessions[key] = session;
    }

    public void EndNative(string playerSession)
    {
        if (string.IsNullOrWhiteSpace(playerSession)) return;
        lock (_gate)
            _sessions.Remove(new PlaybackSessionKey(NativeExploreInstanceId, playerSession));
    }

    public void PruneNative(DateTimeOffset now, TimeSpan ttl)
    {
        var cutoff = now - ttl;
        lock (_gate)
        {
            foreach (var key in _sessions
                         .Where(pair =>
                             pair.Value.SourceType == PlaybackSourceType.InfiniDysk
                             && pair.Value.LastConfirmedAt < cutoff)
                         .Select(pair => pair.Key)
                         .ToList())
            {
                _sessions.Remove(key);
            }
        }
    }

    private static AuthoritativePlaybackSession FromObservation(
        PlaybackSessionKey key,
        string sourceInstanceName,
        PlaybackSourceType sourceType,
        PlaybackObservation observation,
        Guid davItemId,
        DateTimeOffset now,
        PlaybackFreshness freshness) => new()
    {
        Key = key,
        SourceInstanceName = sourceInstanceName,
        SourceType = sourceType,
        NativeSessionId = observation.NativeSessionId,
        UserName = observation.UserName,
        ClientName = observation.ClientName,
        DeviceName = observation.DeviceName,
        ItemId = observation.ItemId,
        Title = observation.Title,
        MediaType = observation.MediaType,
        SeriesName = observation.SeriesName,
        SeasonNumber = observation.SeasonNumber,
        EpisodeNumber = observation.EpisodeNumber,
        State = observation.State,
        PositionMs = observation.PositionMs,
        DurationMs = observation.DurationMs,
        DeliveryMethod = observation.DeliveryMethod,
        MediaSourceId = observation.MediaSourceId,
        MediaSourcePath = observation.MediaSourcePath,
        DavItemId = davItemId,
        LastConfirmedAt = now,
        Freshness = freshness,
    };

    private static PlaybackSourceType ToPlaybackSourceType(MediaServerType type) => type switch
    {
        MediaServerType.Plex => PlaybackSourceType.Plex,
        MediaServerType.Emby => PlaybackSourceType.Emby,
        MediaServerType.Jellyfin => PlaybackSourceType.Jellyfin,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unsupported media-server type."),
    };
}

public sealed record PlaybackRegistrySnapshot(
    IReadOnlyList<AuthoritativePlaybackSession> Sessions,
    IReadOnlyList<PlaybackAuthoritySnapshot> Authorities);
