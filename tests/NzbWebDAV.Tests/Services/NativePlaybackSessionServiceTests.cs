using NzbWebDAV.Models.Playback;
using NzbWebDAV.Services;

namespace NzbWebDAV.Tests.Services;

public class NativePlaybackSessionServiceTests
{
    [Fact]
    public void Report_RejectsUnknownPlayerSessionWithoutExactBackendIdentity()
    {
        var registry = new PlaybackSessionRegistry();
        var service = new NativePlaybackSessionService(new ActiveReadRegistry(), registry);

        var accepted = service.Report(
            "player-1",
            PlaybackState.Playing,
            1_000,
            10_000,
            "Movie",
            "video",
            DateTimeOffset.UtcNow);

        Assert.False(accepted);
        Assert.Empty(registry.Snapshot());
    }

    [Fact]
    public void Report_EstablishesFromActiveReadThenSurvivesTransportExpiry()
    {
        var activeReads = new ActiveReadRegistry();
        var registry = new PlaybackSessionRegistry();
        var service = new NativePlaybackSessionService(activeReads, registry);
        var davItemId = Guid.NewGuid();
        activeReads.GetOrCreate(
            "/.ids/movie",
            "browser",
            "movie.mkv",
            100,
            playerSession: "player-1",
            davItemId: davItemId);
        var now = DateTimeOffset.UtcNow;

        Assert.True(service.Report(
            "player-1",
            PlaybackState.Playing,
            1_000,
            10_000,
            "Movie",
            "video",
            now));

        activeReads.PruneExpired(now.AddSeconds(16));
        Assert.Empty(activeReads.Snapshot());

        Assert.True(service.Report(
            "player-1",
            PlaybackState.Paused,
            2_000,
            10_000,
            "Movie",
            "video",
            now.AddSeconds(30)));

        var session = Assert.Single(registry.Snapshot());
        Assert.Equal(davItemId, session.DavItemId);
        Assert.Equal(PlaybackState.Paused, session.State);
        Assert.Equal(2_000, session.PositionMs);
        Assert.Equal(now.AddSeconds(30), session.LastConfirmedAt);
    }

    [Fact]
    public void Report_FailsClosedWhenOnePlayerSessionMapsToDifferentActiveItems()
    {
        var activeReads = new ActiveReadRegistry();
        var registry = new PlaybackSessionRegistry();
        var service = new NativePlaybackSessionService(activeReads, registry);
        activeReads.GetOrCreate(
            "/.ids/one",
            "browser",
            "one.mkv",
            100,
            playerSession: "player-1",
            davItemId: Guid.NewGuid());
        activeReads.GetOrCreate(
            "/.ids/two",
            "browser",
            "two.mkv",
            100,
            playerSession: "player-1",
            davItemId: Guid.NewGuid());

        Assert.False(service.Report(
            "player-1",
            PlaybackState.Playing,
            null,
            null,
            null,
            null,
            DateTimeOffset.UtcNow));
        Assert.Empty(registry.Snapshot());
    }

    [Fact]
    public void End_RemovesEstablishedNativeSession()
    {
        var activeReads = new ActiveReadRegistry();
        var registry = new PlaybackSessionRegistry();
        var service = new NativePlaybackSessionService(activeReads, registry);
        activeReads.GetOrCreate(
            "/.ids/movie",
            "browser",
            "movie.mkv",
            100,
            playerSession: "player-1",
            davItemId: Guid.NewGuid());

        Assert.True(service.Report(
            "player-1",
            PlaybackState.Buffering,
            500,
            10_000,
            null,
            "video",
            DateTimeOffset.UtcNow));
        Assert.Single(registry.Snapshot());

        service.End("player-1");

        Assert.Empty(registry.Snapshot());
    }
}
