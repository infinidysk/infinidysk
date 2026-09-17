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

        if (!websocketPublisher.HasSubscribers(WebsocketTopic.CurrentActivity))
            return;

        var payload = JsonSerializer.Serialize(composer.Compose(), JsonOptions);
        if (payload == _lastPayload)
            return;

        _lastPayload = payload;
        await websocketPublisher.SendMessage(WebsocketTopic.CurrentActivity, payload).ConfigureAwait(false);
    }
}
