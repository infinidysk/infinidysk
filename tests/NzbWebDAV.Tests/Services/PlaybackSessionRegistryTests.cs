
using NzbWebDAV.Config;
using NzbWebDAV.Models.Playback;
using NzbWebDAV.Services;

namespace NzbWebDAV.Tests.Services;

public class PlaybackSessionRegistryTests
{
    private static MediaServerInstance PlexInstance() => new()
    {
        Id = Guid.NewGuid(),
        Type = MediaServerType.Plex,
        Name = "Home Plex",
        BaseUrl = "http://plex.test",
        Token = "secret",
    };

    private static MappedPlaybackObservation Session(string id, Guid davItemId, PlaybackState state) =>
        new(new PlaybackObservation
        {
            NativeSessionId = id,
            Title = "Movie",
            State = state,
            PositionMs = 12_000,
            DurationMs = 60_000,
            MediaSourcePath = "/movies/movie.mkv",
        }, davItemId);

    [Fact]
    public void SuccessfulReconciliation_IsAuthoritativeAndRemovesAbsentSessions()
    {
        var registry = new PlaybackSessionRegistry();
        var instance = PlexInstance();
        var item = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        registry.ReconcileExternal(instance, [Session("s1", item, PlaybackState.Playing)], now);
        Assert.Single(registry.Snapshot());

        registry.ReconcileExternal(instance, [], now.AddSeconds(5));
        Assert.Empty(registry.Snapshot());
        Assert.True(registry.AuthoritySnapshot().Single().Available);
    }

    [Fact]
    public void TwoViewersOfSameDavItem_RemainDistinctSessions()
    {
        var registry = new PlaybackSessionRegistry();
        var instance = PlexInstance();
        var item = Guid.NewGuid();

        registry.ReconcileExternal(instance,
        [
            Session("alice", item, PlaybackState.Playing),
            Session("bob", item, PlaybackState.Paused),
        ], DateTimeOffset.UtcNow);

        var sessions = registry.Snapshot();
        Assert.Equal(2, sessions.Count);
        Assert.All(sessions, session => Assert.Equal(item, session.DavItemId));
        Assert.Contains(sessions, session => session.State == PlaybackState.Paused);
    }

    [Fact]
    public void PollFailure_MarksSessionsStaleAndRetainsThemUntilGraceExpires()
    {
        var registry = new PlaybackSessionRegistry();
        var instance = PlexInstance();
        var now = DateTimeOffset.UtcNow;
        registry.ReconcileExternal(instance, [Session("s1", Guid.NewGuid(), PlaybackState.Playing)], now);

        registry.MarkExternalFailure(instance, now.AddSeconds(5), "timeout");
        Assert.Equal(PlaybackFreshness.Stale, registry.Snapshot().Single().Freshness);
        Assert.True(registry.AuthoritySnapshot().Single().IsStale);

        registry.ExpireStaleExternal(now.AddSeconds(30), TimeSpan.FromSeconds(60));
        Assert.Single(registry.Snapshot());
        registry.ExpireStaleExternal(now.AddSeconds(61), TimeSpan.FromSeconds(60));
        Assert.Empty(registry.Snapshot());
    }

    [Fact]
    public void NativePausedSession_UsesIndependentHeartbeatTtl()
    {
        var registry = new PlaybackSessionRegistry();
        var now = DateTimeOffset.UtcNow;
        registry.UpsertNative("player-1", Guid.NewGuid(), "Movie", "Video",
            PlaybackState.Paused, 10_000, 20_000, now);

        registry.PruneNative(now.AddSeconds(30), TimeSpan.FromSeconds(90));
        Assert.Equal(PlaybackState.Paused, registry.Snapshot().Single().State);
        registry.PruneNative(now.AddSeconds(91), TimeSpan.FromSeconds(90));
        Assert.Empty(registry.Snapshot());
    }
}
