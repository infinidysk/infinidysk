
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
    public void PlexParser_UsesStrongestVideoAndAudioDeliveryDecision()
    {
        using var document = JsonDocument.Parse("""
        {
          "MediaContainer": {
            "Metadata": [{
              "ratingKey": "mixed-1",
              "type": "movie",
              "title": "Mixed decisions",
              "Player": {"state": "playing"},
              "Session": {"id": "plex-mixed"},
              "Media": [{
                "id": "media-mixed",
                "videoDecision": "directplay",
                "audioDecision": "transcode",
                "Part": [{"id": "part-mixed", "file": "/movies/mixed.mkv"}]
              }]
            }]
          }
        }
        """);

        var session = PlexPlaybackSessionSource.Parse(document.RootElement).Single();

        Assert.Equal(PlaybackDeliveryMethod.Transcode, session.DeliveryMethod);
    }

    [Fact]
    public void PlexParser_UsesAudioDecisionWhenVideoDecisionIsAbsent()
    {
        using var document = JsonDocument.Parse("""
        {
          "MediaContainer": {
            "Metadata": [{
              "ratingKey": "audio-1",
              "type": "track",
              "title": "Audio",
              "Player": {"state": "playing"},
              "Session": {"id": "plex-audio"},
              "Media": [{
                "id": "media-audio",
                "audioDecision": "directplay",
                "Part": [{"id": "part-audio", "file": "/music/audio.flac"}]
              }]
            }]
          }
        }
        """);

        var session = PlexPlaybackSessionSource.Parse(document.RootElement).Single();

        Assert.Equal(PlaybackDeliveryMethod.DirectPlay, session.DeliveryMethod);
    }

    [Fact]
    public void EmbyJellyfinParser_MissingPauseFlag_RemainsUnknown()
    {
        using var document = JsonDocument.Parse("""
        [{
          "Id": "session-unknown",
          "NowPlayingItem": {
            "Id": "movie-1",
            "Name": "Movie",
            "Type": "Movie",
            "Path": "/movies/Movie.mkv"
          },
          "PlayState": {
            "PositionTicks": 12000000000
          }
        }]
        """);

        var session = EmbyJellyfinPlaybackSessionSource.Parse(document.RootElement).Single();

        Assert.Equal(PlaybackState.Unknown, session.State);
        Assert.Equal("/movies/Movie.mkv", session.MediaSourcePath);
    }

    [Fact]
    public void EmbyJellyfinParser_PrefersExplicitActiveMediaSourceOverGenericItemPath()
    {
        using var document = JsonDocument.Parse("""
        [{
          "Id": "session-version-b",
          "NowPlayingItem": {
            "Id": "movie-versioned",
            "Name": "Movie",
            "Type": "Movie",
            "Path": "/movies/default-version.mkv",
            "MediaSources": [
              {"Id": "source-a", "Path": "/movies/version-a.mkv"},
              {"Id": "source-b", "Path": "/movies/version-b.mkv"}
            ]
          },
          "PlayState": {
            "PositionTicks": 12000000000,
            "IsPaused": false,
            "MediaSourceId": "source-b",
            "PlayMethod": "DirectPlay"
          }
        }]
        """);

        var session = EmbyJellyfinPlaybackSessionSource.Parse(document.RootElement).Single();

        Assert.Equal("source-b", session.MediaSourceId);
        Assert.Equal("/movies/version-b.mkv", session.MediaSourcePath);
    }

    [Fact]
    public void EmbyJellyfinParser_AllowsItemPathWhenMediaSourceMatchesNowPlayingItem()
    {
        using var document = JsonDocument.Parse("""
        [{
          "Id": "session-normal",
          "NowPlayingItem": {
            "Id": "source-normal",
            "Name": "Movie",
            "Type": "Movie",
            "Path": "/movies/current-version.mkv"
          },
          "PlayState": {
            "PositionTicks": 12000000000,
            "IsPaused": false,
            "MediaSourceId": "source-normal",
            "PlayMethod": "DirectPlay"
          }
        }]
        """);

        var session = EmbyJellyfinPlaybackSessionSource.Parse(document.RootElement).Single();

        Assert.Equal("source-normal", session.MediaSourceId);
        Assert.Equal("/movies/current-version.mkv", session.MediaSourcePath);
    }

    [Fact]
    public void EmbyJellyfinParser_ExplicitUnknownMediaSourceDoesNotFallBackToGenericItemPath()
    {
        using var document = JsonDocument.Parse("""
        [{
          "Id": "session-missing-version",
          "NowPlayingItem": {
            "Id": "movie-versioned",
            "Name": "Movie",
            "Type": "Movie",
            "Path": "/movies/default-version.mkv",
            "MediaSources": [
              {"Id": "source-a", "Path": "/movies/version-a.mkv"}
            ]
          },
          "PlayState": {
            "PositionTicks": 12000000000,
            "IsPaused": false,
            "MediaSourceId": "source-missing"
          }
        }]
        """);

        var session = EmbyJellyfinPlaybackSessionSource.Parse(document.RootElement).Single();

        Assert.Equal("source-missing", session.MediaSourceId);
        Assert.Null(session.MediaSourcePath);
    }

    [Fact]
    public void EmbyJellyfinParser_DoesNotGuessAmongMultipleSourcesWithoutMediaSourceId()
    {
        using var document = JsonDocument.Parse("""
        [{
          "Id": "session-ambiguous",
          "NowPlayingItem": {
            "Id": "movie-2",
            "Name": "Movie",
            "Type": "Movie",
            "MediaSources": [
              {"Id": "source-a", "Path": "/movies/version-a.mkv"},
              {"Id": "source-b", "Path": "/movies/version-b.mkv"}
            ]
          },
          "PlayState": {
            "PositionTicks": 12000000000,
            "IsPaused": false
          }
        }]
        """);

        var session = EmbyJellyfinPlaybackSessionSource.Parse(document.RootElement).Single();

        Assert.Equal(PlaybackState.Playing, session.State);
        Assert.Null(session.MediaSourceId);
        Assert.Null(session.MediaSourcePath);
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
