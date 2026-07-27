using Microsoft.Extensions.Hosting;
using Serilog;

namespace NzbWebDAV.Services.StreamTrace;

/// <summary>
/// Disables UI-enabled stream tracing when its TTL elapses so RAM is released
/// even if the operator forgets to turn it off. Env-sourced tracing (no expiry)
/// is left alone.
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
                    var status = buffer.Disable();
                    Log.Information(
                        "Stream tracing expired; released {Events:n0} buffered events from {Source}",
                        before.EventCount,
                        before.Source);
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
            catch (Exception e)
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
