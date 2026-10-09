using System.Text.Json;
using Microsoft.Extensions.Hosting;
using NzbWebDAV.Websocket;
using Serilog;

namespace NzbWebDAV.Services;

public sealed class CurrentActivityBroadcaster(
    PlaybackSessionRegistry playbackRegistry,
    CurrentActivityComposer composer,
    IWebsocketPublisher websocketPublisher) : BackgroundService
{
    internal static readonly TimeSpan NativeSessionTtl = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
    private string? _lastPayload;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TickInterval, stoppingToken).ConfigureAwait(false);
                await BroadcastTickAsync(DateTimeOffset.UtcNow).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                Log.Debug(exception, "CurrentActivityBroadcaster tick failed");
            }
        }
    }

    internal async Task BroadcastTickAsync(DateTimeOffset now)
    {
        playbackRegistry.PruneNative(now, NativeSessionTtl);
        playbackRegistry.ExpireStaleExternal(now, MediaServerSessionPoller.StaleGrace);

        var hasSubscribers = websocketPublisher.HasSubscribers(WebsocketTopic.CurrentActivity);
        var snapshot = composer.Compose();
        var payload = JsonSerializer.Serialize(snapshot, JsonOptions);

        // Send changed snapshots even with zero subscribers: WebsocketManager stores
        // State messages before checking connected subscribers, keeping replay state
        // current for the next browser that subscribes.
        //
        // The frontend derives delivery rate from byte deltas between state samples.
        // While a subscriber is present and a transport read exists, also keep sampling
        // an unchanged snapshot at the normal one-second tick so a previously non-zero
        // rate decays promptly to zero instead of remaining frozen until the 15-second
        // read TTL. Stable playback-only/empty snapshots remain deduplicated.
        if (payload == _lastPayload && (!hasSubscribers || snapshot.Reads.Count == 0))
            return;

        await websocketPublisher.SendMessage(WebsocketTopic.CurrentActivity, payload).ConfigureAwait(false);
        _lastPayload = payload;
    }
}
