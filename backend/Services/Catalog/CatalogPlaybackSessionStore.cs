namespace NzbWebDAV.Services.Catalog;

/// <summary>One in-flight catalog playback: the URLs the browser uses plus the state the
/// transcode path caches after resolving the signed <c>/view</c> location.</summary>
public sealed class CatalogPlaybackSession
{
    public required Guid Id { get; init; }

    /// <summary>Browser-facing play URL; redirects to signed <c>/view</c> bytes.</summary>
    public required string PublicDirectUrl { get; init; }

    /// <summary>Loopback play URL used only by the transcode resolver.</summary>
    public required string InternalDirectUrl { get; init; }

    public required string TitleType { get; init; }

    public required string TitleId { get; init; }

    public required string ReleaseTitle { get; init; }

    public required string PublicPathPrefix { get; init; }

    public DateTimeOffset LastAccess { get; set; }

    /// <summary>Normalized loopback <c>/view</c> URI, cached after the first successful resolve.</summary>
    public string? ResolvedViewUri { get; set; }

    /// <summary>Probed media duration, cached after the first successful probe.</summary>
    public TimeSpan? MediaDuration { get; set; }
}

/// <summary>
/// Bounded in-memory store for catalog playback sessions. Sessions are keyed by an
/// unguessable GUID, never persisted, and only ever hold the two play URLs the service
/// created — a restart simply requires clicking Play again.
/// </summary>
public sealed class CatalogPlaybackSessionStore(TimeProvider? timeProvider = null)
{
    internal const int MaxSessions = 32;

    internal static readonly TimeSpan IdleExpiry = TimeSpan.FromHours(24);

    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, CatalogPlaybackSession> _sessions = [];
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public int Count
    {
        get
        {
            lock (_gate) return _sessions.Count;
        }
    }

    public CatalogPlaybackSession Add(
        string publicDirectUrl,
        string internalDirectUrl,
        string titleType,
        string titleId,
        string releaseTitle,
        string publicPathPrefix)
    {
        var now = _timeProvider.GetUtcNow();
        var session = new CatalogPlaybackSession
        {
            Id = Guid.NewGuid(),
            PublicDirectUrl = publicDirectUrl,
            InternalDirectUrl = internalDirectUrl,
            TitleType = titleType,
            TitleId = titleId,
            ReleaseTitle = releaseTitle,
            PublicPathPrefix = publicPathPrefix,
            LastAccess = now,
        };

        lock (_gate)
        {
            PruneExpired(now);
            if (_sessions.Count >= MaxSessions)
                EvictLeastRecentlyUsed();
            _sessions[session.Id] = session;
        }

        return session;
    }

    /// <summary>Looks a session up and refreshes its activity timestamp.</summary>
    public bool TryGet(Guid id, out CatalogPlaybackSession session)
    {
        var now = _timeProvider.GetUtcNow();
        lock (_gate)
        {
            if (_sessions.TryGetValue(id, out var found) && found.LastAccess + IdleExpiry > now)
            {
                found.LastAccess = now;
                session = found;
                return true;
            }

            _sessions.Remove(id);
        }

        session = null!;
        return false;
    }

    /// <summary>Membership check that does not refresh activity (used by the cleanup sweep).</summary>
    public bool Contains(Guid id)
    {
        lock (_gate) return _sessions.ContainsKey(id);
    }

    /// <summary>Absolute sessions are dropped regardless of access; used by the cleanup sweep.</summary>
    public void Prune(DateTimeOffset now)
    {
        lock (_gate)
        {
            PruneExpired(now);
        }
    }

    internal IReadOnlyList<CatalogPlaybackSession> Snapshot()
    {
        lock (_gate) return [.. _sessions.Values];
    }

    private void PruneExpired(DateTimeOffset now)
    {
        List<Guid>? expired = null;
        foreach (var (id, session) in _sessions)
        {
            if (session.LastAccess + IdleExpiry > now) continue;
            (expired ??= []).Add(id);
        }

        if (expired is null) return;
        foreach (var id in expired)
            _sessions.Remove(id);
    }

    private void EvictLeastRecentlyUsed()
    {
        Guid? oldestId = null;
        var oldest = DateTimeOffset.MaxValue;
        foreach (var (id, session) in _sessions)
        {
            if (session.LastAccess >= oldest) continue;
            oldest = session.LastAccess;
            oldestId = id;
        }

        if (oldestId is { } evict)
            _sessions.Remove(evict);
    }
}
