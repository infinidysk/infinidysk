
using System.Text.Json;
using NzbWebDAV.Clients.MediaServers;
using NzbWebDAV.Models.Playback;

namespace NzbWebDAV.Tests.Clients.MediaServers;

public class MediaPlaybackSessionSourceTests
{
    [Fact]
    public void SessionHttpHandler_DoesNotAutomaticallyFollowRedirects()
    {
        using var handler = MediaServerHttp.CreateSessionHandler();

        Assert.False(handler.AllowAutoRedirect);
    }

    [Fact]
    public void PlexParser_NormalizesSessionStateProgressAndPath()
    {
        using var document = JsonDocument.Parse("""
        {
          "MediaContainer": {
            "Metadata": [{
              "ratingKey": "123",
              "type": "movie",
              "title": "Dune: Part Two",
              "duration": 9948000,
              "viewOffset": 4462000,
              "User": {"title": "alice"},
              "Player": {"title": "Living Room Shield", "product": "Plex for Android", "state": "playing"},
              "Session": {"id": "plex-session-1"},
              "Media": [{"id": "media-1", "videoDecision": "directplay", "Part": [{"id": "part-1", "file": "/movies/Dune Part Two.mkv"}]}]
            }]
          }
        }
        """);

        var session = PlexPlaybackSessionSource.Parse(document.RootElement).Single();
        Assert.Equal("plex-session-1", session.NativeSessionId);
        Assert.Equal("alice", session.UserName);
        Assert.Equal("Living Room Shield", session.DeviceName);
        Assert.Equal(PlaybackState.Playing, session.State);
        Assert.Equal(4_462_000, session.PositionMs);
        Assert.Equal(PlaybackDeliveryMethod.DirectPlay, session.DeliveryMethod);
        Assert.Equal("/movies/Dune Part Two.mkv", session.MediaSourcePath);
    }

    [Fact]
    public void EmbyJellyfinParser_NormalizesPausedTicksAndMediaSource()
    {
        using var document = JsonDocument.Parse("""
        [{
          "Id": "session-2",
          "Client": "Jellyfin Android TV",
          "DeviceName": "Bedroom Shield",
          "UserName": "bob",
          "NowPlayingItem": {
            "Id": "episode-22",
            "Name": "Episode 3",
            "Type": "Episode",
            "SeriesName": "Example Show",
            "ParentIndexNumber": 2,
            "IndexNumber": 3,
            "RunTimeTicks": 36000000000,
            "MediaSources": [{"Id": "source-1", "Path": "/tv/Example Show/S02E03.mkv"}]
          },
          "PlayState": {
            "PositionTicks": 12000000000,
            "IsPaused": true,
            "MediaSourceId": "source-1",
            "PlayMethod": "DirectStream"
          }
        }]
        """);

        var session = EmbyJellyfinPlaybackSessionSource.Parse(document.RootElement).Single();
        Assert.Equal(PlaybackState.Paused, session.State);
        Assert.Equal(1_200_000, session.PositionMs);
        Assert.Equal(3_600_000, session.DurationMs);
        Assert.Equal(PlaybackDeliveryMethod.DirectStream, session.DeliveryMethod);
        Assert.Equal("source-1", session.MediaSourceId);
        Assert.Equal("/tv/Example Show/S02E03.mkv", session.MediaSourcePath);
    }
}
