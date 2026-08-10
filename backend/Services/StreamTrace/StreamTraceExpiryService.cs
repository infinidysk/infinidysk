using Microsoft.Extensions.Hosting;
using Serilog;

namespace NzbWebDAV.Services.StreamTrace;

/// <summary>
/// Stops UI-enabled stream tracing when its TTL elapses, then releases retained
/// captures after their support-pack window. Env-sourced tracing (no expiry) is
/// left alone until explicitly stopped.
/// </summary>
public sealed class StreamTraceExpiryService(
    StreamTraceBuffer buffer,
    StreamTraceStatusBroadcaster broadcaster) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Seed the state topic so late websocket subscribers see the current status
        // even when tracing was never toggled in this process.
        await broadcaster.BroadcastAsync(buffer.GetStatus()).ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (buffer.IsExpired)
                {
                    var before = buffer.GetStatus();
                    var status = buffer.StopRecording();
                    Log.Information(
                        "Stream tracing expired; retaining {Events:n0} events from {Source} for support packs",
                        before.EventCount,
                        before.Source);
                    await broadcaster.BroadcastAsync(status).ConfigureAwait(false);
                }
                else if (buffer.IsRetentionExpired)
                {
                    var before = buffer.GetStatus();
                    var status = buffer.Discard();
                    Log.Information(
                        "Released {Events:n0} retained stream-trace events after the retention window",
                        before.EventCount);
                    await broadcaster.BroadcastAsync(status).ConfigureAwait(false);
                }
                else
                {
                    await broadcaster.BroadcastAsync(buffer.GetStatus()).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                Log.Warning(e, "Stream tracing expiry sweep failed");
            }

            try
            {
                await Task.Delay(PollInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }
}
