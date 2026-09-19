using NzbWebDAV.Config;
using NzbWebDAV.Services;
using NzbWebDAV.Websocket;

namespace NzbWebDAV.Tests.Services;

public class CurrentActivityBroadcasterTests
{
    [Fact]
    public async Task ActiveTransport_IsSampledEvenWhenSnapshotCountersAreUnchanged()
    {
        var playback = new PlaybackSessionRegistry();
        var active = new ActiveReadRegistry();
        var usage = new ProviderUsageTracker(active);
        var publisher = new FakePublisher();
        var composer = new CurrentActivityComposer(playback, active, usage, new ConfigManager());
        var broadcaster = new CurrentActivityBroadcaster(playback, composer, publisher);

        var readId = active.GetOrCreate("/.ids/movie", "rclone", "movie.mkv", 100);
        active.Touch(readId, 64, 64);

        var now = DateTimeOffset.UtcNow;
        await broadcaster.BroadcastTickAsync(now);
        await broadcaster.BroadcastTickAsync(now.AddSeconds(1));

        Assert.Equal(2, publisher.Messages.Count);
        Assert.All(publisher.Messages, message => Assert.Contains("\"bytesRead\":64", message));
    }

    [Fact]
    public async Task StableEmptySnapshot_RemainsDeduplicated()
    {
        var playback = new PlaybackSessionRegistry();
        var active = new ActiveReadRegistry();
        var publisher = new FakePublisher();
        var composer = new CurrentActivityComposer(
            playback,
            active,
            new ProviderUsageTracker(active),
            new ConfigManager());
        var broadcaster = new CurrentActivityBroadcaster(playback, composer, publisher);

        var now = DateTimeOffset.UtcNow;
        await broadcaster.BroadcastTickAsync(now);
        await broadcaster.BroadcastTickAsync(now.AddSeconds(1));

        Assert.Single(publisher.Messages);
    }

    private sealed class FakePublisher : IWebsocketPublisher
    {
        public List<string> Messages { get; } = [];

        public bool HasSubscribers(WebsocketTopic topic) =>
            ReferenceEquals(topic, WebsocketTopic.CurrentActivity);

        public Task SendMessage(WebsocketTopic topic, string message)
        {
            Assert.Same(WebsocketTopic.CurrentActivity, topic);
            Messages.Add(message);
            return Task.CompletedTask;
        }
    }
}
