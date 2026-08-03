using NzbWebDAV.Services;
using NzbWebDAV.Services.Metrics;
using NzbWebDAV.Websocket;

namespace NzbWebDAV.Tests.Services.Metrics;

public class LiveStatsBroadcasterTests
{
    [Fact]
    public async Task BytesServedWindow_StaysWarm_WithoutSubscribers()
    {
        var registry = new ActiveReadRegistry();
        var websocketManager = new WebsocketManager();
        // The remaining dependencies are only used after the subscriber gate;
        // passing null! doubles as a regression check that no expensive work
        // (DB queries, provider snapshots) runs while nobody is subscribed.
        var broadcaster = new LiveStatsBroadcaster(registry, websocketManager, null!, null!, null!);

        var readId = registry.GetOrCreate("/view/movie.mkv", "client", "movie.mkv", 1000);
        registry.Touch(readId, bytesRead: 1000);
        await broadcaster.BroadcastAsync(); // baseline sample

        registry.Touch(readId, bytesRead: 500);
        await broadcaster.BroadcastAsync();

        // The Overview page's initial HTTP load reads this value, so the
        // rolling window must be maintained even with zero subscribers.
        Assert.Equal(500, broadcaster.BytesServedLastMinute);
        // But nothing should have been published.
        Assert.Null(websocketManager.PeekLastMessage(WebsocketTopic.LiveStats));
    }
}
