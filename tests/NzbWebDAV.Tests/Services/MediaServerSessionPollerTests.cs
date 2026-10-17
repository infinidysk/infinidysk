using System.Text.Json;
using NzbWebDAV.Clients.MediaServers;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Models.Playback;
using NzbWebDAV.Services;

namespace NzbWebDAV.Tests.Services;

public class MediaServerSessionPollerTests
{
    [Fact]
    public async Task SuccessfulPoll_IsAuthoritativeAndRestartSafe()
    {
        var id = Guid.NewGuid();
        var instance = Instance();
        var source = new FakeSource(MediaServerType.Plex);
        source.SetResult([Observation("s1", id)]);
        var registry = new PlaybackSessionRegistry();
        var poller = Poller(instance, source, registry);
        var now = DateTimeOffset.UtcNow;

        await poller.PollDueAsync(now, CancellationToken.None);
        Assert.Single(registry.Snapshot());
        Assert.Equal(id, registry.Snapshot().Single().DavItemId);

        source.SetResult([]);
        await poller.PollDueAsync(now + TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Empty(registry.Snapshot());
    }

    [Fact]
    public async Task FailedWholeServerPoll_RetainsStaleRowsThenExpiresAfterGrace()
    {
        var id = Guid.NewGuid();
        var instance = Instance();
        var source = new FakeSource(MediaServerType.Plex);
        source.SetResult([Observation("s1", id)]);
        var registry = new PlaybackSessionRegistry();
        var poller = Poller(instance, source, registry);
        var now = DateTimeOffset.UtcNow;

        await poller.PollDueAsync(now, CancellationToken.None);
        source.SetException(new HttpRequestException("server unavailable"));
        await poller.PollDueAsync(now + TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Single(registry.Snapshot());
        Assert.Equal(PlaybackFreshness.Stale, registry.Snapshot().Single().Freshness);
        Assert.True(registry.AuthoritySnapshot().Single().IsStale);

        await poller.PollDueAsync(now + TimeSpan.FromSeconds(61), CancellationToken.None);
        Assert.Empty(registry.Snapshot());
    }

    [Fact]
    public async Task MultipleEnabledInstances_PollInParallel()
    {
        var first = Instance();
        var second = Instance();
        second.Id = Guid.NewGuid();
        second.Name = "Plex 2";
        var source = new ConcurrentProbeSource();
        var registry = new PlaybackSessionRegistry();
        var config = Config(first, second);
        var poller = new MediaServerSessionPoller(
            config,
            registry,
            new PlaybackDavItemResolver(config),
            [source],
            () => 0);

        await poller.PollDueAsync(DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.True(source.MaxConcurrent >= 2);
    }

    [Theory]
    [InlineData(1, 5)]
    [InlineData(2, 10)]
    [InlineData(3, 20)]
    [InlineData(4, 30)]
    [InlineData(8, 30)]
    public void FailureDelay_UsesBoundedExponentialSchedule(int failures, int seconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(seconds), MediaServerSessionPoller.FailureDelay(failures, 0));
        Assert.True(MediaServerSessionPoller.FailureDelay(failures, 1) > TimeSpan.FromSeconds(seconds));
    }

    private static MediaServerSessionPoller Poller(
        MediaServerInstance instance,
        IMediaPlaybackSessionSource source,
        PlaybackSessionRegistry registry)
    {
        var config = Config(instance);
        return new MediaServerSessionPoller(
            config,
            registry,
            new PlaybackDavItemResolver(config),
            [source],
            () => 0);
    }

    private static ConfigManager Config(params MediaServerInstance[] instances)
    {
        var config = new ConfigManager();
        config.UpdateValues(
        [
            new ConfigItem
            {
                ConfigName = ConfigKeys.MediaServersInstances,
                ConfigValue = JsonSerializer.Serialize(new MediaServerConfig { Instances = instances.ToList() }),
            },
        ]);
        return config;
    }

    private static MediaServerInstance Instance() => new()
    {
        Id = Guid.NewGuid(),
        Type = MediaServerType.Plex,
        Name = "Plex",
        BaseUrl = "http://plex.test",
        Token = "secret",
    };

    private static PlaybackObservation Observation(string session, Guid id) => new()
    {
        NativeSessionId = session,
        State = PlaybackState.Playing,
        PositionMs = 1234,
        MediaSourcePath = $"https://plex.test/view/.ids/{id}.mkv",
    };

    private sealed class FakeSource(MediaServerType serverType) : IMediaPlaybackSessionSource
    {
        private IReadOnlyList<PlaybackObservation> _result = [];
        private Exception? _exception;
        public MediaServerType ServerType => serverType;

        public void SetResult(IReadOnlyList<PlaybackObservation> result)
        {
            _result = result;
            _exception = null;
        }

        public void SetException(Exception exception) => _exception = exception;

        public Task<IReadOnlyList<PlaybackObservation>> GetSessionsAsync(
            MediaServerInstance instance,
            CancellationToken cancellationToken)
        {
            if (_exception is not null)
                throw _exception;
            return Task.FromResult(_result);
        }
    }

    private sealed class ConcurrentProbeSource : IMediaPlaybackSessionSource
    {
        private int _active;
        private int _maxConcurrent;
        public MediaServerType ServerType => MediaServerType.Plex;
        public int MaxConcurrent => Volatile.Read(ref _maxConcurrent);

        public async Task<IReadOnlyList<PlaybackObservation>> GetSessionsAsync(
            MediaServerInstance instance,
            CancellationToken cancellationToken)
        {
            var active = Interlocked.Increment(ref _active);
            while (true)
            {
                var current = Volatile.Read(ref _maxConcurrent);
                if (active <= current || Interlocked.CompareExchange(ref _maxConcurrent, active, current) == current)
                    break;
            }

            try
            {
                await Task.Delay(50, cancellationToken);
                return [];
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }
    }
}
