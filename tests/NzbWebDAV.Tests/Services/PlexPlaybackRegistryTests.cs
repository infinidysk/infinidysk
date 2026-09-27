using NzbWebDAV.Services.Plex;
using NzbWebDAV.Services.Prefetch;

namespace NzbWebDAV.Tests.Services;

public sealed class PlexPlaybackRegistryTests
{
    [Fact]
    public void DisabledServers_AreRemovedImmediatelyWithoutWaitingForExpiry()
    {
        var registry = new PlexPlaybackRegistry(TimeProvider.System);
        var media = new PlexMediaItem("m", "movie", "Movie", null, null, null, "/movie.mkv", 1, 100, null);
        registry.Record("server", [new PlexSession("session", "user", "playing", media.File, media)], TimeSpan.FromSeconds(300));
        registry.RetainServers([]);
        Assert.False(registry.HasActivePlayback);
    }
    [Fact]
    public void FailedPollCannotRefreshVerifiedPlayback_AndRawReadsAreSeparate()
    {
        var clock = new Clock();
        var registry = new PlexPlaybackRegistry(clock);
        var media = new PlexMediaItem("m", "movie", "Movie", null, null, null, "/movie.mkv", 1, 100, null);
        registry.Record("server", [new PlexSession("session", "user", "playing", media.File, media)], TimeSpan.FromSeconds(30));
        Assert.True(registry.HasActivePlayback);
        clock.Now += TimeSpan.FromSeconds(31);
        var raw = new NzbWebDAV.Services.ActiveReadRegistry();
        raw.GetOrCreate("/movie.mkv", "scanner", "movie.mkv", 100);
        Assert.False(registry.HasActivePlayback);
    }

    [Fact]
    public void EmptyFreshSessions_ClearServerPlaybackImmediately()
    {
        var registry = new PlexPlaybackRegistry(TimeProvider.System);
        var media = new PlexMediaItem("m", "movie", "Movie", null, null, null, "/movie.mkv", 1, 100, null);
        registry.Record("server", [new PlexSession("session", "user", "playing", media.File, media)], TimeSpan.FromSeconds(30));
        registry.Record("server", [], TimeSpan.FromSeconds(30));
        Assert.False(registry.HasActivePlayback);
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
