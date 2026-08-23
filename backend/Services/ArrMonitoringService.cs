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
    private readonly ArrReplacementSearchBudget _replacementSearchBudget;

    public ArrMonitoringService(ConfigManager configManager, TimeProvider? timeProvider = null)
    {
        _configManager = configManager;
        _replacementSearchBudget = new ArrReplacementSearchBudget(timeProvider);
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
        var resolutions = new List<(string? Title, ArrConfig.QueueAction Action, string Reason, string IdentitySource)>();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            var queueStatus = await client.GetQueueStatusAsync(timeout.Token).ConfigureAwait(false);
            if (queueStatus is { Warnings: false, UnknownWarnings: false }) return;
            var queue = await client.GetQueueAsync(timeout.Token).ConfigureAwait(false);
            var stuckRecords = GetActionableStuckRecords(queue, arrConfig.QueueRules);
            foreach (var record in stuckRecords)
            {
                var resolution = await HandleStuckQueueItem(record, arrConfig, client, timeout.Token)
                    .ConfigureAwait(false);
                if (resolution is null) continue;
                resolutions.Add(resolution.Value);
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

    internal static IReadOnlyList<ArrQueueRecord> GetActionableStuckRecords(
        ArrQueue<ArrQueueRecord> queue,
        IEnumerable<ArrConfig.QueueRule> queueRules)
    {
        var actionableStatuses = queueRules
            .Where(x => x.Action is not ArrConfig.QueueAction.DoNothing)
            .Select(x => x.Message)
            .ToArray();

        return queue.Records
            .Where(x => x.IsAwaitingImport)
            .Where(x => actionableStatuses.Any(x.HasStatusMessage))
            .ToList();
    }

    private async Task<(string? Title, ArrConfig.QueueAction Action, string Reason, string IdentitySource)?>
        HandleStuckQueueItem(
        ArrQueueRecord item, ArrConfig arrConfig, ArrClient client, CancellationToken ct)
    {
        // since there may be multiple status messages, multiple actions may apply.
        // in such case, always perform the strongest action.
        var matchingRules = arrConfig.QueueRules
            .Where(x => item.HasStatusMessage(x.Message))
            .ToList();
        var action = matchingRules
            .Select(x => x.Action)
            .DefaultIfEmpty(ArrConfig.QueueAction.DoNothing)
            .Max();

        if (action is ArrConfig.QueueAction.DoNothing) return null;
        var reason = SummarizeReason(
            item.GetMatchingStatusMessages(matchingRules.Where(x => x.Action == action).Select(x => x.Message)),
            matchingRules.Where(x => x.Action == action).Select(x => x.Message));
        var (mediaKey, identitySource) = GetMediaKey(client, item);

        var requestedAction = action;
        action = ApplyReplacementSearchBudget(
            requestedAction,
            mediaKey,
            arrConfig,
            _replacementSearchBudget);
        if (requestedAction is ArrConfig.QueueAction.RemoveAndBlocklistAndSearch &&
            action is ArrConfig.QueueAction.RemoveAndBlocklist)
        {
            reason = $"{reason} Automatic replacement-search limit reached " +
                     $"({arrConfig.EffectiveQueueReplacementSearchLimit()} in " +
                     $"{arrConfig.EffectiveQueueReplacementSearchWindow().TotalMinutes:0} minutes); " +
                     "the release was removed and blocklisted without starting another search.";
        }

        await client.DeleteQueueRecord(item.Id, action, ct).ConfigureAwait(false);
        Log.Debug(
            "Resolved stuck queue record {QueueRecordId} ({QueueItemTitle}) from {Host} with action {Action}. " +
            "Reason: {Reason}. Media identity source: {IdentitySource}",
            item.Id, item.Title, client.Host, action, reason, identitySource);
        return (item.Title, action, reason, identitySource);
    }

    internal static ArrConfig.QueueAction ApplyReplacementSearchBudget(
        ArrConfig.QueueAction requestedAction,
        string mediaKey,
        ArrConfig arrConfig,
        ArrReplacementSearchBudget replacementSearchBudget)
    {
        if (requestedAction is not ArrConfig.QueueAction.RemoveAndBlocklistAndSearch)
            return requestedAction;

        return replacementSearchBudget.TryReserve(
            mediaKey,
            arrConfig.EffectiveQueueReplacementSearchLimit(),
            arrConfig.EffectiveQueueReplacementSearchWindow())
            ? requestedAction
            : ArrConfig.QueueAction.RemoveAndBlocklist;
    }

    private static (string Key, string Source) GetMediaKey(ArrClient client, ArrQueueRecord item)
    {
        var host = client.Host.TrimEnd('/').ToLowerInvariant();
        var mediaIdentity = item.GetMediaIdentity();
        if (mediaIdentity is not null) return ($"{host}|{mediaIdentity}", "Arr media ID");

        if (!string.IsNullOrWhiteSpace(item.DownloadId))
            return ($"{host}|download:{item.DownloadId}", "download ID fallback");

        return ($"{host}|queue:{item.Id}", "queue record ID fallback");
    }

    internal static string SummarizeReason(
        IEnumerable<string> matchingStatusMessages,
        IEnumerable<string> configuredMessages)
    {
        const int maxLength = 512;
        var reasons = matchingStatusMessages
            .Select(FlattenReason)
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (reasons.Count == 0)
        {
            reasons = configuredMessages
                .Select(FlattenReason)
                .Where(x => x.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }

        var reason = string.Join("; ", reasons);
        return reason.Length <= maxLength ? reason : $"{reason[..(maxLength - 1)]}…";
    }

    private static string FlattenReason(string value) =>
        string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

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

    internal static void LogResolutionSummary(
        IEnumerable<(string? Title, ArrConfig.QueueAction Action, string Reason, string IdentitySource)> resolutions,
        string host)
    {
        foreach (var entry in resolutions
                     .GroupBy(x => (x.Title ?? "(untitled)", x.Action, x.Reason, x.IdentitySource))
                     .Select(g => (g.Key, g.Count())))
        {
            Log.Warning(
                "Resolved {Count} stuck queue item(s) for {QueueItemTitle} from {Host} with action {Action}. " +
                "Reason: {Reason}. Media identity source: {IdentitySource}",
                entry.Item2,
                entry.Key.Item1,
                host,
                entry.Key.Action,
                entry.Key.Reason,
                entry.Key.IdentitySource);
        }
    }
}
