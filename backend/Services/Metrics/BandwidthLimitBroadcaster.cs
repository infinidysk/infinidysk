using System.Text.Json;
using Microsoft.Extensions.Hosting;
using NzbWebDAV.Config;
using NzbWebDAV.Extensions;
using NzbWebDAV.Streams;
using NzbWebDAV.Utils;
using NzbWebDAV.Websocket;

namespace NzbWebDAV.Services.Metrics;

/// <summary>
/// Publishes the live Usenet bandwidth-limit snapshot every two seconds so the
/// Streaming settings card can show current vs configured rate. Disabled-state
/// frames are sent even without subscribers so replay is not a stale enabled
/// snapshot after the cap is cleared.
/// </summary>
public sealed class BandwidthLimitBroadcaster(
    WebsocketManager websocketManager,
    UsenetBandwidthLimiter limiter,
    ConfigManager configManager
) : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(2);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly SemaphoreSlim _publishGate = new(1, 1);
    private long _lastChargedBytes;
    private long _lastSampleMs;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        configManager.OnConfigChanged += OnConfigChanged;
        try
        {
            await PublishAsync(force: true, stoppingToken).ConfigureAwait(false);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TickInterval, stoppingToken).ConfigureAwait(false);
                    await PublishAsync(force: false, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (SigtermUtil.IsSigtermTriggered())
                {
                    return;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    ex.LogWarningKnownOrStack("BandwidthLimitBroadcaster tick failed.");
                }
            }
        }
        finally
        {
            configManager.OnConfigChanged -= OnConfigChanged;
        }
    }

    public override void Dispose()
    {
        _publishGate.Dispose();
        base.Dispose();
    }

    private void OnConfigChanged(object? sender, ConfigManager.ConfigEventArgs args)
    {
        if (!args.ChangedConfig.ContainsKey(ConfigKeys.UsenetBandwidthLimitMbps))
            return;
        _ = PublishAsync(force: true);
    }

    internal async Task PublishAsync(bool force, CancellationToken cancellationToken = default)
    {
        await _publishGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var limitBytesPerSecond = limiter.BytesPerSecond;
            var enabled = limitBytesPerSecond > 0;
            if (!force && enabled && !websocketManager.HasSubscribers(WebsocketTopic.BandwidthLimit))
            {
                _lastChargedBytes = limiter.TotalChargedBytes;
                _lastSampleMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                return;
            }

            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var charged = limiter.TotalChargedBytes;
            var elapsedMs = nowMs - _lastSampleMs;
            long currentBytesPerSecond = 0;
            if (enabled && elapsedMs > 0 && _lastSampleMs > 0)
                currentBytesPerSecond = (long)((charged - _lastChargedBytes) * 1000.0 / elapsedMs);

            _lastChargedBytes = charged;
            _lastSampleMs = nowMs;

            var snapshot = enabled
                ? new
                {
                    enabled = true,
                    limitBytesPerSecond,
                    currentBytesPerSecond,
                    ts = nowMs,
                }
                : (object)new { enabled = false, ts = nowMs };

            var payload = JsonSerializer.Serialize(snapshot, JsonOptions);
            await websocketManager.SendMessage(WebsocketTopic.BandwidthLimit, payload).ConfigureAwait(false);
        }
        finally
        {
            _publishGate.Release();
        }
    }
}
