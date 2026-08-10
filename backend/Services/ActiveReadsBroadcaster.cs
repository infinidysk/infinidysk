using System.Text.Json;
using Microsoft.Extensions.Hosting;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models.Metrics;
using NzbWebDAV.Services.Metrics;
using NzbWebDAV.Utils;
using NzbWebDAV.Websocket;
using Serilog;

namespace NzbWebDAV.Services;

/// <summary>
/// Ticks once per second to publish the current set of active WebDAV read
/// sessions plus their per-backbone segment counts over the websocket. When no
/// sessions are active, the loop is mostly idle (just a sleep + a Count check).
/// Sends nothing when nothing has changed since the last broadcast.
/// </summary>
public class ActiveReadsBroadcaster(
    ActiveReadRegistry registry,
    ProviderUsageTracker usageTracker,
    WebsocketManager websocketManager,
    MetricsWriter metricsWriter,
    ConfigManager configManager
) : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private string? _lastPayload;
    private bool _wasEmpty = true;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TickInterval, stoppingToken).ConfigureAwait(false);
                await BroadcastTickAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (SigtermUtil.IsSigtermTriggered())
            {
                return;
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                Log.Debug(e, "ActiveReadsBroadcaster tick failed");
            }
        }
    }

    private async Task BroadcastTickAsync()
    {
        // Prune sessions that haven't been touched in the activity window first,
        // so their counters don't leak in the tracker. Each pruned entry becomes
        // a terminal ReadSession row so the dashboard can show historical reads.
        var pruned = registry.PruneExpired();
        foreach (var entry in pruned)
        {
            var failoverSaves = usageTracker.GetFailoverSaves(entry.Id);
            usageTracker.Clear(entry.Id);
            metricsWriter.RecordSession(new ReadSession
            {
                Id = entry.Id,
                StartedAt = entry.StartedAt.ToUnixTimeMilliseconds(),
                EndedAt = entry.LastActivityAt.ToUnixTimeMilliseconds(),
                DurationMs = (int)Math.Min(int.MaxValue,
                    (entry.LastActivityAt - entry.StartedAt).TotalMilliseconds),
                Path = entry.Path,
                FileSize = entry.FileSize,
                BytesServed = Interlocked.Read(ref entry.BytesRead),
                BytesFetched = Interlocked.Read(ref entry.BytesFetched),
                FailoverSaves = (int)Math.Min(int.MaxValue, failoverSaves),
                ClientUserAgent = entry.ClientUserAgent,
                ClientIp = entry.ClientIp,
                EndReason = entry.EndReason,
            });
        }

        if (!websocketManager.HasSubscribers(WebsocketTopic.ActiveReads))
            return;

        var entries = registry.Snapshot();

        // Common case: nothing active, nothing was active. Skip serialization entirely.
        if (entries.Count == 0 && _wasEmpty) return;

        var usage = usageTracker.SnapshotMany(entries.Select(e => e.Id));
        var displayByMetricsKey = ProviderUsageHelper
            .BuildDisplayByMetricsKey(configManager.GetUsenetProviderConfig().Providers);
        var snapshot = new
        {
            reads = entries.Select(e => new
            {
                id = e.Id,
                fileName = e.FileName,
                path = e.Path,
                startedAt = e.StartedAt.ToUnixTimeMilliseconds(),
                lastActivityAt = e.LastActivityAt.ToUnixTimeMilliseconds(),
                bytesRead = Interlocked.Read(ref e.BytesRead),
                currentOffset = Interlocked.Read(ref e.CurrentOffset),
                fileSize = e.FileSize,
                clientIp = e.ClientIp,
                clientUserAgent = e.ClientUserAgent,
                providers = (usage.GetValueOrDefault(e.Id) ?? new Dictionary<string, long>())
                    .Select(kv =>
                    {
                        displayByMetricsKey.TryGetValue(kv.Key, out var display);
                        return new
                        {
                            host = display.Host ?? kv.Key,
                            nickname = display.Nickname,
                            segments = kv.Value,
                        };
                    })
                    .OrderByDescending(p => p.segments)
                    .ToList()
            }).ToList()
        };

        var payload = JsonSerializer.Serialize(snapshot, JsonOptions);
        if (payload == _lastPayload) return;
        _lastPayload = payload;
        _wasEmpty = entries.Count == 0;
        await websocketManager.SendMessage(WebsocketTopic.ActiveReads, payload).ConfigureAwait(false);
    }
}
