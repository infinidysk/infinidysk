using NzbWebDAV.Config;
using NzbWebDAV.Models.Playback;
using NzbWebDAV.Services;

namespace NzbWebDAV.Tests.Services;

public class CurrentActivityComposerTests
{
    [Fact]
    public void TwoViewersOnOneDavItem_KeepFileTransportShared()
    {
        var playback = new PlaybackSessionRegistry();
        var active = new ActiveReadRegistry();
        var usage = new ProviderUsageTracker(active);
        var instance = Instance();
        var item = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        playback.ReconcileExternal(instance,
        [
            Mapped("alice", item, 10_000),
            Mapped("bob", item, 20_000),
        ], now);

        var readId = active.GetOrCreate("/.ids/file", "rclone", "movie.mkv", 100, davItemId: item);
        active.Touch(readId, 50, currentOffset: 90);
        using (usage.BeginScope(readId))
            usage.RecordSuccess("news.example.test");

        var snapshot = new CurrentActivityComposer(playback, active, usage, new ConfigManager()).Compose();

        Assert.Equal(2, snapshot.Playback.Count);
        var read = Assert.Single(snapshot.Reads);
        Assert.Equal(TransportCorrelationScope.File, read.CorrelationScope);
        Assert.Equal(2, read.MatchingPlaybackSessionCount);
        Assert.True(read.Shared);
        Assert.Equal(90, read.SourceOffset);
        Assert.All(snapshot.Playback, row => Assert.True(row.HasSharedFileTransport));
        Assert.Equal([10_000L, 20_000L], snapshot.Playback.Select(row => row.Session.PositionMs!.Value).Order());
    }

    [Fact]
    public void UnmatchedTransport_RemainsReadOnlyActivity()
    {
        var active = new ActiveReadRegistry();
        active.GetOrCreate("/scan", "rclone", "scan.mkv", 100);

        var snapshot = new CurrentActivityComposer(
            new PlaybackSessionRegistry(),
            active,
            new ProviderUsageTracker(active),
            new ConfigManager()).Compose();

        Assert.Empty(snapshot.Playback);
        Assert.Equal(TransportCorrelationScope.None, Assert.Single(snapshot.Reads).CorrelationScope);
    }

    [Fact]
    public void NativePlayerSession_HasSessionLevelCorrelation()
    {
        var playback = new PlaybackSessionRegistry();
        var active = new ActiveReadRegistry();
        var item = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        playback.UpsertNative("player-1", item, "Movie", "Video", PlaybackState.Paused, 12_000, 60_000, now);
        active.GetOrCreate(
            "/.ids/movie",
            "browser",
            "movie.mkv",
            100,
            playerSession: "player-1",
            davItemId: item);

        var snapshot = new CurrentActivityComposer(
            playback,
            active,
            new ProviderUsageTracker(active),
            new ConfigManager()).Compose();

        Assert.Equal(PlaybackState.Paused, Assert.Single(snapshot.Playback).Session.State);
        Assert.Equal(12_000, snapshot.Playback.Single().Session.PositionMs);
        Assert.Equal(TransportCorrelationScope.Session, Assert.Single(snapshot.Reads).CorrelationScope);
    }

    private static MediaServerInstance Instance() => new()
    {
        Id = Guid.NewGuid(),
        Type = MediaServerType.Plex,
        Name = "Plex",
        BaseUrl = "http://plex.test",
        Token = "secret",
    };

    private static MappedPlaybackObservation Mapped(string session, Guid id, long position) =>
        new(new PlaybackObservation
        {
            NativeSessionId = session,
            State = PlaybackState.Playing,
            PositionMs = position,
            DurationMs = 60_000,
        }, id);
}
