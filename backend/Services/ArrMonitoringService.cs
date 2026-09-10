using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using NzbWebDAV.Clients.RadarrSonarr;
using NzbWebDAV.Clients.RadarrSonarr.BaseModels;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models.Nzb;
using NzbWebDAV.Services.Diagnostics;
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
    private const long RejectedReleaseMaxXmlCharacters = 8L * 1024 * 1024;
    private const int RejectedReleaseMaxSegments = 250_000;
    private static readonly TimeSpan RejectedReleaseCaptureTimeout = TimeSpan.FromSeconds(2);
    private readonly ConfigManager _configManager;
    private readonly ArrReplacementSearchBudget _replacementSearchBudget;
    private readonly IDbContextFactory<DavDatabaseContext> _dbContextFactory;
    private readonly IBlobStore _blobStore;
    private readonly ArrInstanceBackoff _backoff;

    public ArrMonitoringService(
        ConfigManager configManager,
        ArrReplacementSearchBudget replacementSearchBudget,
        IDbContextFactory<DavDatabaseContext> dbContextFactory,
        IBlobStore blobStore,
        ArrInstanceBackoff? backoff = null)
    {
        _configManager = configManager;
        _replacementSearchBudget = replacementSearchBudget;
        _dbContextFactory = dbContextFactory;
        _blobStore = blobStore;
        _backoff = backoff ?? new ArrInstanceBackoff();
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
        var seededDownloadIds = new HashSet<Guid>();

        // Skip a host that is timing out or refusing connections until its backoff elapses.
        if (_backoff.IsInBackoff(client.Host))
        {
            Log.Debug(
                "Arr queue monitoring for {Host} skipped; instance is in backoff for {Remaining}",
                client.Host,
                _backoff.GetRemainingBackoff(client.Host));
            return;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            var queueStatus = await client.GetQueueStatusAsync(timeout.Token).ConfigureAwait(false);
            _backoff.RecordSuccess(client.Host);
            if (queueStatus is { Warnings: false, UnknownWarnings: false }) return;
            var queue = await client.GetQueueAsync(timeout.Token).ConfigureAwait(false);
            var stuckRecords = GetActionableStuckRecords(queue, arrConfig.QueueRules);
            foreach (var record in stuckRecords)
            {
                var resolution = await HandleStuckQueueItem(
                    record, arrConfig, client, seededDownloadIds, timeout.Token)
                    .ConfigureAwait(false);
                if (resolution is null) continue;
                resolutions.Add(resolution.Value);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Monitoring pass aborted on shutdown.
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            Log.Warning("Arr queue monitoring timed out after 20 seconds for {Host}", client.Host);
            _backoff.RecordFailure(client.Host, new TimeoutException("Arr queue monitoring timed out."));
        }
        catch (Exception e) when (e is HttpRequestException { InnerException: System.Net.Sockets.SocketException })
        {
            Log.Debug(e, "Could not reach Arr instance {Host} for queue monitoring", client.Host);
            _backoff.RecordFailure(client.Host, e);
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

    internal async Task<(string? Title, ArrConfig.QueueAction Action, string Reason, string IdentitySource)?>
        HandleStuckQueueItem(
        ArrQueueRecord item,
        ArrConfig arrConfig,
        ArrClient client,
        ISet<Guid>? seededDownloadIds,
        CancellationToken ct)
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
        var shouldCapture = action is ArrConfig.QueueAction.RemoveAndBlocklist
            or ArrConfig.QueueAction.RemoveAndBlocklistAndSearch;
        var rejectedRelease = shouldCapture
            ? await CaptureRejectedReleaseSegmentsAsync(item, client.Host, seededDownloadIds, ct)
                .ConfigureAwait(false)
            : null;
        ct.ThrowIfCancellationRequested();

        var requestedAction = action;
        action = ApplyReplacementSearchBudget(
            requestedAction,
            mediaKey,
            arrConfig,
            _replacementSearchBudget);
        var searchReserved = requestedAction is ArrConfig.QueueAction.RemoveAndBlocklistAndSearch &&
                             action is ArrConfig.QueueAction.RemoveAndBlocklistAndSearch;
        if (requestedAction is ArrConfig.QueueAction.RemoveAndBlocklistAndSearch &&
            action is ArrConfig.QueueAction.RemoveAndBlocklist)
        {
            reason = $"{reason} Automatic replacement-search limit reached " +
                     $"({arrConfig.EffectiveQueueReplacementSearchLimit()} in " +
                     $"{arrConfig.EffectiveQueueReplacementSearchWindow().TotalMinutes:0} minutes); " +
                     "the release was removed and blocklisted without starting another search.";
        }

        // A transport exception below is ambiguous — Arr may have already processed the
        // removal and started the search — so the reservation stays consumed and ambiguity
        // can only under-search, never exceed the cap.
        var status = await client.DeleteQueueRecord(item.Id, action, ct).ConfigureAwait(false);
        if ((int)status is < 200 or >= 300)
        {
            // Arr definitively rejected the removal (commonly 404 when the record is already
            // gone), so no replacement search occurred; refund the reservation.
            if (searchReserved) _replacementSearchBudget.ReleaseLastReservation(mediaKey);
            Log.Debug(
                "Arr instance {Host} rejected removal of queue record {QueueRecordId} ({QueueItemTitle}) " +
                "with status {StatusCode}",
                client.Host, item.Id, item.Title, status);
            return null;
        }

        if (rejectedRelease is not null)
        {
            try
            {
                HealthCheckService.AddMissingSegmentIds(rejectedRelease.Value.SegmentIds);
                seededDownloadIds?.Add(rejectedRelease.Value.DownloadId);
                Log.Debug(
                    "Recorded {SegmentCount} article IDs from blocklisted Arr download {DownloadId}",
                    rejectedRelease.Value.SegmentIds.Length,
                    rejectedRelease.Value.DownloadId);
            }
            catch (OutOfMemoryException exception)
            {
                OomDiagnostics.LogHeapStateOnOom(exception, "Arr queue rejected-release cache seeding");
            }
        }
        Log.Debug(
            "Resolved stuck queue record {QueueRecordId} ({QueueItemTitle}) from {Host} with action {Action}. " +
            "Reason: {Reason}. Media identity source: {IdentitySource}",
            item.Id, item.Title, client.Host, action, reason, identitySource);
        return (item.Title, action, reason, identitySource);
    }

    private async Task<(Guid DownloadId, string[] SegmentIds)?> CaptureRejectedReleaseSegmentsAsync(
        ArrQueueRecord item,
        string host,
        ISet<Guid>? seededDownloadIds,
        CancellationToken ct)
    {
        if (!Guid.TryParse(item.DownloadId, out var downloadId) || downloadId == Guid.Empty)
        {
            Log.Warning(
                "Could not record fail-fast evidence before blocklisting Arr queue record {QueueRecordId} from {Host}. " +
                "Reason: download ID is missing or invalid",
                item.Id,
                host);
            return null;
        }
        if (seededDownloadIds?.Contains(downloadId) is true)
            return null;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RejectedReleaseCaptureTimeout);
        try
        {
            var blobId = await FindCompletedNzbBlobIdAsync(downloadId, timeout.Token).ConfigureAwait(false);
            if (blobId is null)
            {
                Log.Warning(
                    "Could not record fail-fast evidence before blocklisting Arr queue record {QueueRecordId} from {Host}. " +
                    "Reason: no completed local history entry with an NZB blob matches download {DownloadId}",
                    item.Id,
                    host,
                    downloadId);
                return null;
            }

            await using var nzbStream = _blobStore.ReadBlob(blobId.Value);
            if (nzbStream is null)
            {
                Log.Warning(
                    "Could not record fail-fast evidence before blocklisting Arr queue record {QueueRecordId} from {Host}. " +
                    "Reason: stored NZB blob {BlobId} is unavailable",
                    item.Id,
                    host,
                    blobId);
                return null;
            }

            var document = await LoadRejectedReleaseAsync(nzbStream, timeout.Token).ConfigureAwait(false);
            var segments = SelectRejectedReleaseSeedSegments(
                document, HealthCheckService.RejectedReleaseSeedSegments);
            return segments.Length > 0 ? (downloadId, segments) : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            Log.Warning(
                "Could not record fail-fast evidence before blocklisting Arr queue record {QueueRecordId} from {Host}. " +
                "Reason: local NZB capture exceeded {TimeoutSeconds} seconds",
                item.Id,
                host,
                RejectedReleaseCaptureTimeout.TotalSeconds);
            return null;
        }
        catch (OutOfMemoryException exception)
        {
            OomDiagnostics.LogHeapStateOnOom(exception, "Arr queue rejected-release capture");
            return null;
        }
        catch (InvalidDataException exception)
        {
            Log.Warning(
                "Could not record fail-fast evidence before blocklisting Arr queue record {QueueRecordId} from {Host}. " +
                "Reason: stored NZB is unavailable for bounded parsing ({Reason})",
                item.Id,
                host,
                exception.Message);
            Log.Debug(exception, "Arr queue rejected-release NZB parsing failure stack");
            return null;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            exception.LogWarningKnownOrStack(
                "Could not record fail-fast evidence before blocklisting Arr queue record {QueueRecordId} from {Host}.",
                item.Id,
                host);
            return null;
        }
    }
    private async Task<Guid?> FindCompletedNzbBlobIdAsync(Guid downloadId, CancellationToken ct)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await context.HistoryItems
            .AsNoTracking()
            .Where(item => item.Id == downloadId
                && item.DownloadStatus == HistoryItem.DownloadStatusOption.Completed
                && item.NzbBlobId != null)
            .Select(item => item.NzbBlobId)
            .SingleOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }
    private static Task<NzbDocument> LoadRejectedReleaseAsync(Stream nzbStream, CancellationToken ct)
    {
        var options = new NzbReadOptions(
            RejectedReleaseMaxXmlCharacters,
            charge: static _ => { },
            reserve: static _ => NoReservation.Instance)
        {
            MaxSegments = RejectedReleaseMaxSegments,
        };
        return NzbDocument.LoadAsync(nzbStream, options, ct);
    }
    internal static string[] SelectRejectedReleaseSeedSegments(NzbDocument document, int maximum)
    {
        if (maximum <= 0) return [];
        var files = document.Files.Where(file => file.Segments.Count > 0).ToList();
        var result = new List<string>(Math.Min(maximum, files.Sum(file => file.Segments.Count)));
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void TryAdd(string segmentId)
        {
            if (result.Count < maximum && seen.Add(segmentId))
                result.Add(segmentId);
        }

        foreach (var file in files)
        {
            if (result.Count >= maximum) break;
            TryAdd(file.Segments[0].MessageId);
        }
        foreach (var file in files)
        {
            foreach (var segment in file.Segments.Skip(1))
            {
                if (result.Count >= maximum) return result.ToArray();
                TryAdd(segment.MessageId);
            }
        }
        return result.ToArray();
    }
    private sealed class NoReservation : IDisposable
    {
        public static readonly NoReservation Instance = new();
        public void Dispose() { }
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
