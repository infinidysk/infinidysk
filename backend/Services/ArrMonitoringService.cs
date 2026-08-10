using Microsoft.Extensions.Hosting;
using NzbWebDAV.Clients.RadarrSonarr;
using NzbWebDAV.Clients.RadarrSonarr.BaseModels;
using NzbWebDAV.Config;
using NzbWebDAV.Extensions;
using Serilog;

namespace NzbWebDAV.Services;

/// <summary>
/// - This class takes care of monitoring Radarr/Sonarr instances
///   for stuck queue items which usually require manual intervention.
/// - NzbDAV can be configured to automatically remove these stuck items,
///   optionally block these stuck items, and optionally trigger a new
///   search for these stuck items.
/// </summary>
public class ArrMonitoringService : BackgroundService
{
    private readonly ConfigManager _configManager;

    public ArrMonitoringService(ConfigManager configManager)
    {
        _configManager = configManager;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Ensure delay runs on each iteration
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken).ConfigureAwait(false);

                // if all queue-actions are disabled, then do nothing
                var arrConfig = _configManager.GetArrConfig();
                if (arrConfig.QueueRules.All(x => x.Action == ArrConfig.QueueAction.DoNothing))
                    continue;

                // otherwise, handle stuck queue items according to the config
                foreach (var arrClient in arrConfig.GetArrClients())
                    await HandleStuckQueueItems(arrConfig, arrClient, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                e.LogWarningKnownOrStack("Unexpected error in Arr queue monitoring loop.");
            }
        }
    }

    private async Task HandleStuckQueueItems(ArrConfig arrConfig, ArrClient client, CancellationToken ct)
    {
        // A season pack yields one record per episode, so a single stuck release can be
        // hundreds of removals. Logging each at Warning evicted every other warning from
        // the buffer support packs are built from, so detail goes to Debug and the pass
        // reports one Warning per release and action.
        var resolutions = new List<(string? Title, ArrConfig.QueueAction Action)>();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            var queueStatus = await client.GetQueueStatusAsync(timeout.Token).ConfigureAwait(false);
            if (queueStatus is { Warnings: false, UnknownWarnings: false }) return;
            var queue = await client.GetQueueAsync(timeout.Token).ConfigureAwait(false);
            var actionableStatuses = arrConfig.QueueRules.Select(x => x.Message);
            var stuckRecords = queue.Records.Where(x => actionableStatuses.Any(x.HasStatusMessage));
            foreach (var record in stuckRecords)
            {
                var action = await HandleStuckQueueItem(record, arrConfig, client, timeout.Token)
                    .ConfigureAwait(false);
                if (action is null) continue;
                resolutions.Add((record.Title, action.Value));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Monitoring pass aborted on shutdown.
        }
        catch (Exception e) when (e is HttpRequestException { InnerException: System.Net.Sockets.SocketException })
        {
            Log.Debug(e, "Could not reach Arr instance {Host} for queue monitoring", client.Host);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            e.LogWarningKnownOrStack("Error occurred while monitoring queue for {Host}", client.Host);
        }
        finally
        {
            // Preserve the summary even if a later record fails and aborts the pass:
            // earlier successful removals must not disappear from the Warning lane.
            LogResolutionSummary(resolutions, client.Host);
        }
    }

    private async Task<ArrConfig.QueueAction?> HandleStuckQueueItem(
        ArrQueueRecord item, ArrConfig arrConfig, ArrClient client, CancellationToken ct)
    {
        // since there may be multiple status messages, multiple actions may apply.
        // in such case, always perform the strongest action.
        var action = arrConfig.QueueRules
            .Where(x => item.HasStatusMessage(x.Message))
            .Select(x => x.Action)
            .DefaultIfEmpty(ArrConfig.QueueAction.DoNothing)
            .Max();

        if (action is ArrConfig.QueueAction.DoNothing) return null;
        await client.DeleteQueueRecord(item.Id, action).ConfigureAwait(false);
        Log.Debug(
            "Resolved stuck queue record {QueueRecordId} ({QueueItemTitle}) from {Host} with action {Action}",
            item.Id, item.Title, client.Host, action);
        return action;
    }

    internal static IReadOnlyList<((string Title, ArrConfig.QueueAction Action) Key, int Count)>
        GroupResolutions(IEnumerable<(string? Title, ArrConfig.QueueAction Action)> resolutions) =>
        resolutions
            .GroupBy(x => (x.Title ?? "(untitled)", x.Action))
            .Select(g => (g.Key, g.Count()))
            .ToList();

    internal static void LogResolutionSummary(
        IEnumerable<(string? Title, ArrConfig.QueueAction Action)> resolutions,
        string host)
    {
        foreach (var entry in GroupResolutions(resolutions))
        {
            Log.Warning(
                "Resolved {Count} stuck queue item(s) for {QueueItemTitle} from {Host} with action {Action}",
                entry.Count,
                entry.Key.Title,
                host,
                entry.Key.Action);
        }
    }
}
