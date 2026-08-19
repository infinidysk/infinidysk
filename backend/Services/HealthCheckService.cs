using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using NzbWebDAV.Clients.RadarrSonarr;
using NzbWebDAV.Clients.RadarrSonarr.BaseModels;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Contexts;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models;
using NzbWebDAV.Queue;
using NzbWebDAV.Queue.PostProcessors;
using NzbWebDAV.Services.Repair;
using NzbWebDAV.Utils;
using NzbWebDAV.Websocket;
using Serilog;
using UsenetSharp.Models;

namespace NzbWebDAV.Services;

/// <summary>
/// This service monitors for health checks
/// </summary>
public class HealthCheckService : BackgroundService
{
    private const int MaximumMissingSegmentIds = 100_000;
    private const int NoMatchConfirmationsRequired = 2;
    private static readonly TimeSpan HealthCheckProgressTimeout = TimeSpan.FromMinutes(5);

    // Repeated remove-and-blocklist repairs for the same library path in a short window indicate
    // a replacement loop (Arr keeps re-grabbing a release repair keeps rejecting, issue #732).
    // After the limit is hit, further repairs at that path are deferred instead of deleting again.
    internal const int RepairRecurrenceLimit = 3;
    internal static readonly TimeSpan RepairRecurrenceWindow = TimeSpan.FromHours(6);
    private const int MaximumTrackedRepairPaths = 10_000;

    // How many of a rejected release's segment ids to seed into the fail-fast cache.
    // Bounded so a single large release cannot evict the whole FIFO cache; the queue
    // precheck fails on any overlap, so a prefix is sufficient to reject a re-grab.
    internal const int RejectedReleaseSeedSegments = 200;

    // Files at or below this many segments are checked in full, before any aging taper.
    public const int SampleFloor = 8000;

    /// <summary>
    /// One-time boot delay before the first background sweep. Queue resume, first streams,
    /// and pool warm-up hit cold connection pools simultaneously at startup; giving them a
    /// short grace window keeps health-check STATs out of the connection storm (#881).
    /// <para>
    /// Settable for tests so coverage does not wait on the real delay. Tests that override
    /// it must restore the original value in a finally block; otherwise later tests in the
    /// same process silently skip the grace.
    /// </para>
    /// </summary>
    internal static TimeSpan StartupGracePeriod { get; set; } = TimeSpan.FromSeconds(20);

    // A release keeps full depth for its first year, then tapers until it stops aging at ten.
    private const double FullDepthDays = 365;
    private const double MinDepthDays = 3650;

    private readonly ConfigManager _configManager;
    private readonly UsenetStreamingClient _usenetClient;
    private readonly WebsocketManager _websocketManager;
    private readonly BenchmarkGate _benchmarkGate;
    private readonly StreamingFailureTracker _failureTracker;
    private readonly QueueManager _queueManager;
    private readonly Par2RepairService _par2RepairService;
    private readonly RepairPatchStore _repairPatchStore;

    private static readonly HashSet<string> _missingSegmentIds = [];
    private static readonly Queue<string> _missingSegmentOrder = [];
    private static readonly ConcurrentDictionary<Guid, int> _arrNoMatchConfirmations = new();
    private static readonly ConcurrentDictionary<string, List<DateTimeOffset>> _recentRepairRemovalsByPath = new();

    public HealthCheckService
    (
        ConfigManager configManager,
        UsenetStreamingClient usenetClient,
        WebsocketManager websocketManager,
        BenchmarkGate benchmarkGate,
        StreamingFailureTracker failureTracker,
        QueueManager queueManager,
        Par2RepairService par2RepairService,
        RepairPatchStore repairPatchStore
    )
    {
        _configManager = configManager;
        _usenetClient = usenetClient;
        _websocketManager = websocketManager;
        _benchmarkGate = benchmarkGate;
        _failureTracker = failureTracker;
        _queueManager = queueManager;
        _par2RepairService = par2RepairService;
        _repairPatchStore = repairPatchStore;

        _configManager.OnConfigChanged += (_, configEventArgs) =>
        {
            // when provider settings change, clear the missing segments cache
            if (!configEventArgs.ChangedConfig.ContainsKey(ConfigKeys.UsenetProviders)) return;
            lock (_missingSegmentIds)
            {
                _missingSegmentIds.Clear();
                _missingSegmentOrder.Clear();
            }
        };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupGracePeriod, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        await ClearNonMediaHealthCheckEntries(stoppingToken).ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // pause verification while a connection speed-test is running
                if (_benchmarkGate.IsPaused)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
                    continue;
                }

                // if the repair-job is disabled, then don't do anything
                if (!_configManager.IsRepairJobEnabled())
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
                    continue;
                }

                // Defer new background library health checks while the NZB queue
                // is actively processing. An already-running check finishes normally;
                // queue-item article validation inside QueueItemProcessor is separate.
                if (_queueManager.HasActiveQueueItems)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
                    continue;
                }

                // get concurrency (capped to avoid saturating the NNTP pool)
                var concurrency = _configManager.GetHealthCheckConcurrency();
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);

                // get the davItem to health-check
                await using var dbContext = new DavDatabaseContext();
                var dbClient = new DavDatabaseClient(dbContext);
                var currentDateTime = DateTimeOffset.UtcNow;

                // Stream the ordered queue and take the first media candidate.
                // Urgent repairs (UnixEpoch sentinel) always run regardless of file type.
                DavItem? davItem = null;
                await foreach (var item in GetHealthCheckQueueItems(dbClient)
                    .Where(x => x.NextHealthCheck == null || x.NextHealthCheck < currentDateTime)
                    .AsAsyncEnumerable()
                    .WithCancellation(cts.Token)
                    .ConfigureAwait(false))
                {
                    if (item.NextHealthCheck == DateTimeOffset.UnixEpoch ||
                        FilenameUtil.IsHealthCheckCandidate(item.Name))
                    {
                        davItem = item;
                        break;
                    }
                }

                // if there is no item to health-check, don't do anything
                if (davItem == null)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), cts.Token).ConfigureAwait(false);
                    continue;
                }

                // perform the health check
                await PerformHealthCheck(davItem, dbClient, concurrency, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (SigtermUtil.IsSigtermTriggered())
            {
                // OperationCanceledException is expected on sigterm
                return;
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                if (e.TryGetKnownErrorMessage(out var reason))
                {
                    Log.Warning("Background health check deferred. Reason: {Reason}", reason);
                    Log.Debug(e, "Background health check known failure stack");
                }
                else
                {
                    Log.Error(e, "Unexpected error performing background health checks: {Message}", e.Message);
                }

                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
            }
        }
    }

    public static IOrderedQueryable<DavItem> GetHealthCheckQueueItems(DavDatabaseClient dbClient)
    {
        // Playback-triggered urgent repairs stay first. Never-checked files come next so
        // routine rechecks cannot starve the initial scan.
        return GetHealthCheckQueueItemsQuery(dbClient)
            .OrderBy(x =>
                x.NextHealthCheck == DateTimeOffset.UnixEpoch ? 0 :
                x.NextHealthCheck == null ? 1 : 2)
            .ThenBy(x => x.NextHealthCheck)
            .ThenByDescending(x => x.ReleaseDate)
            .ThenBy(x => x.Id);
    }

    public static IQueryable<DavItem> GetHealthCheckQueueItemsQuery(DavDatabaseClient dbClient)
    {
        // History-linked files are skipped for routine STAT checks so they do not race SAB
        // post-processing. UnixEpoch is the playback-triggered urgent sentinel from
        // ExceptionMiddleware and intentionally overrides only that exclusion.
        return dbClient.Ctx.Items
            .Where(x => x.Type == DavItem.ItemType.UsenetFile)
            .Where(x =>
                x.HistoryItemId == null ||
                x.NextHealthCheck == DateTimeOffset.UnixEpoch);
    }

    /// <summary>
    /// One-shot cleanup: clear <c>NextHealthCheck</c>/<c>LastHealthCheck</c> for non-media files
    /// (images, subtitles, NFOs, etc.) that were queued before the media-type filter was added.
    /// Urgent repairs (<c>UnixEpoch</c> sentinel) are never cleared.
    /// </summary>
    internal static async Task ClearNonMediaHealthCheckEntries(CancellationToken ct)
    {
        try
        {
            await using var dbContext = new DavDatabaseContext();
            await ClearNonMediaHealthCheckEntries(dbContext, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException and not OutOfMemoryException)
        {
            Log.Warning(e, "Could not clear non-media health-check entries: {Message}", e.Message);
        }
    }

    internal static async Task ClearNonMediaHealthCheckEntries(
        DavDatabaseContext dbContext,
        CancellationToken ct)
    {
        var urgent = DateTimeOffset.UnixEpoch;
        const int batchSize = 1000;
        var cleared = 0;
        var lastId = Guid.Empty;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var query = dbContext.Items
                .Where(x => x.Type == DavItem.ItemType.UsenetFile)
                .Where(x => x.NextHealthCheck != urgent)
                .Where(x => x.NextHealthCheck != null || x.LastHealthCheck != null);
            if (lastId != Guid.Empty) query = query.Where(x => x.Id > lastId);

            var batch = await query
                .OrderBy(x => x.Id)
                .Take(batchSize)
                .ToListAsync(ct).ConfigureAwait(false);

            if (batch.Count == 0) break;
            lastId = batch[^1].Id;

            foreach (var item in batch.Where(x => !FilenameUtil.IsHealthCheckCandidate(x.Name)))
            {
                item.NextHealthCheck = null;
                item.LastHealthCheck = null;
                cleared++;
            }

            await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
            dbContext.ChangeTracker.Clear();
        }

        if (cleared > 0)
        {
            Log.Information(
                "Cleared health-check schedule for {Count} non-media file(s) " +
                "(images, subtitles, NFOs, and other non-playable files are no longer health-checked)",
                cleared);
        }
    }

    // internal for tests: the degraded-classification scenarios drive this directly.
    internal async Task PerformHealthCheck
    (
        DavItem davItem,
        DavDatabaseClient dbClient,
        int concurrency,
        CancellationToken ct
    )
    {
        // Urgent sentinel set by ExceptionMiddleware when streaming confirms a permanent failure.
        // Skip the STAT-only recheck and repair immediately: STAT can pass while BODY returns 430
        // (see nzbdav-dev#209), and structurally corrupt archives can have every article present.
        var isUrgentRepair = davItem.NextHealthCheck == DateTimeOffset.UnixEpoch;

        // Attribution for latency histograms — does not change pool admission priority.
        using var maintenanceScope = ct.SetContext(MaintenanceDownloadContext.Instance);

        ContextualCancellationTokenSource? statCts = null;
        try
        {
            if (isUrgentRepair)
            {
                // Validate the local payload before Repair: missing metadata is
                // local data loss, not a bad release, and must not reach the
                // Arr remove-and-blocklist path. Routine checks get the same
                // guarantee from GetAllSegments below.
                await EnsurePayloadExistsAsync(davItem, dbClient, ct).ConfigureAwait(false);
                Log.Information("Performing urgent dynamic repair for {FilePath}", davItem.Path);
                await HandleUrgentRepair(davItem, dbClient, ct).ConfigureAwait(false);
                return;
            }

            // update the release date, if null
            var segments = await GetAllSegments(davItem, dbClient, ct).ConfigureAwait(false);
            if (davItem.ReleaseDate == null) await UpdateReleaseDate(davItem, segments, ct).ConfigureAwait(false);

            // sample large files to reduce NNTP load while keeping head/tail/stride coverage
            var totalSegments = segments.Count;
            var age = _configManager.IsHealthCheckAgingEnabled() && davItem.ReleaseDate is { } posted
                ? DateTimeOffset.UtcNow - posted
                : (TimeSpan?)null;
            var sampled = SampleSegments(segments, _configManager.GetHealthCheckDepth(), age);
            var nzbFile = davItem.SubType == DavItem.ItemSubType.NzbFile
                ? await dbClient.GetDavNzbFileAsync(davItem, ct).ConfigureAwait(false)
                : null;
            var statSegments = nzbFile != null
                ? FilterSegmentsForStat(sampled, segments, nzbFile, _repairPatchStore)
                : sampled;

            // A damaged-but-tolerable video file can only be told apart from a failed one by a
            // full-coverage sweep of an eligible container with recorded segment sizes (#461).
            // PAR2-patched segments are stripped from the STAT list by FilterSegmentsForStat but
            // still count toward coverage: they are served locally and can never be holes, so the
            // full-coverage test uses the sampled count, not the STAT count.
            var segmentRanges = nzbFile?.SegmentByteRanges;
            // SegmentByteRanges is [NotMapped] and never materializes for legacy EF-fallback items, so a non-null value already implies FileBlobId != null.
            var canClassify = _configManager.IsDegradedToleranceEnabled()
                              && davItem.SubType == DavItem.ItemSubType.NzbFile
                              && FilenameUtil.IsDegradedToleranceEligible(davItem.Name)
                              && segmentRanges is not null
                              && segmentRanges.Length == totalSegments
                              && sampled.Count == totalSegments;

            // setup progress tracking
            var progressHook = new Progress<int>();
            var debounce = DebounceUtil.CreateDebounce(TimeSpan.FromMilliseconds(200));
            progressHook.ProgressChanged += (_, progress) =>
            {
                try { statCts?.CancelAfter(HealthCheckProgressTimeout); }
                catch (ObjectDisposedException)
                {
                    // statCts may already be disposed when a progress event races teardown.
                }
                var message = $"{davItem.Id}|{progress}";
                debounce(() => _websocketManager.SendMessage(WebsocketTopic.HealthItemProgress, message));
            };

            // Only cancel a STAT sweep after it has made no progress for a sustained
            // period. A complete/deep scan can otherwise run as long as it continues
            // advancing; cancellation reaches and drains every in-flight STAT request.
            List<int>? confirmedHoles = null;
            using (statCts = ContextualCancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                statCts.CancelAfter(HealthCheckProgressTimeout);
                var progress = progressHook.ToPercentage(statSegments.Count);
                if (!canClassify)
                {
                    await ArticleExistenceChecker.CheckAsync(
                        _usenetClient, statSegments, concurrency, progress, statCts.Token).ConfigureAwait(false);
                }
                else
                {
                    // Sweep-and-collect every confirmed miss so the damage classifier can weigh
                    // the full hole set instead of aborting on the first one. STAT sweep chunk
                    // sizing is fixed in BaseNntpClient; the depth argument is BODY-oriented
                    // interface baggage and unused here.
                    var missingIds = await _usenetClient.CollectMissingSegmentsPipelinedAsync(
                            statSegments, depth: 0, concurrency, progress, statCts.Token)
                        .ConfigureAwait(false);
                    confirmedHoles = await ConfirmHolesThroughFallbacksAsync(
                            missingIds, segments, nzbFile!, concurrency, statCts)
                        .ConfigureAwait(false);
                }
            }
            CompleteHealthProgress(davItem.Id);

            if (confirmedHoles is { Count: > 0 })
            {
                await HandleConfirmedHolesAsync(
                        davItem, dbClient, nzbFile!, segments, segmentRanges!, confirmedHoles, ct)
                    .ConfigureAwait(false);
                return;
            }

            // update the database.
            // the next check is scheduled so the interval doubles with the item's age since release.
            // clamp to a minimum interval: a null release-date (zero-segment item) or a future-dated
            // article header would otherwise schedule the item in the past and hot-loop the service.
            var utcNow = DateTimeOffset.UtcNow;
            davItem.LastHealthCheck = utcNow;
            davItem.NextHealthCheck = ComputeNextHealthCheck(davItem.ReleaseDate, utcNow);
            _failureTracker.ClearFailure(davItem.Id);
            _arrNoMatchConfirmations.TryRemove(davItem.Id, out _);

            // A previously degraded file that now sweeps clean has recovered (provider-side
            // restoration): drop the stale hole record. The probed container class is a
            // permanent property of the file and survives the clear.
            if (canClassify && nzbFile!.MissingSegmentIndices != null)
                await SwapNzbFileBlobAsync(davItem, nzbFile, null, null).ConfigureAwait(false);

            var repairedCount = nzbFile != null
                ? Par2RepairService.CountRepairedSegments(nzbFile, _repairPatchStore)
                : 0;
            var healthyMessage = repairedCount > 0
                ? $"File is healthy ({repairedCount} segment{(repairedCount == 1 ? "" : "s")} repaired from PAR2 parity)."
                : sampled.Count < totalSegments
                    ? $"File is healthy (sampled {sampled.Count}/{totalSegments} segments)."
                    : "File is healthy.";
            await RecordHealthResult(
                dbClient, davItem,
                HealthCheckResult.HealthResult.Healthy,
                HealthCheckResult.RepairAction.None,
                healthyMessage, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            !ct.IsCancellationRequested && statCts?.IsCancellationRequested == true)
        {
            CompleteHealthProgress(davItem.Id);
            var utcNow = DateTimeOffset.UtcNow;
            davItem.LastHealthCheck = utcNow;
            davItem.NextHealthCheck = utcNow + TimeSpan.FromDays(1);
            Log.Warning(
                "Health check for {Path} made no STAT progress for {Timeout}. Deferred next check.",
                davItem.Path, HealthCheckProgressTimeout);
            await RecordHealthResult(
                dbClient, davItem,
                HealthCheckResult.HealthResult.Unhealthy,
                HealthCheckResult.RepairAction.ActionNeeded,
                $"Health check deferred: no STAT progress for {HealthCheckProgressTimeout.TotalMinutes:0} minutes.",
                ct).ConfigureAwait(false);
        }
        catch (MissingFilePayloadException e)
        {
            // Local payload metadata is gone (commonly a database-only restore).
            // This says nothing about the release's health, so surface it for
            // operator action instead of deleting or blocklisting through Arr.
            CompleteHealthProgress(davItem.Id);
            var utcNow = DateTimeOffset.UtcNow;
            davItem.LastHealthCheck = utcNow;
            davItem.NextHealthCheck = utcNow + TimeSpan.FromDays(1);
            Log.Warning(
                "Health check cannot run for {Path}: {Reason}",
                davItem.Path, e.Message);
            Log.Debug(e, "Missing streaming payload stack for {Path}", davItem.Path);
            await RecordHealthResult(
                dbClient, davItem,
                HealthCheckResult.HealthResult.Unhealthy,
                HealthCheckResult.RepairAction.ActionNeeded,
                string.Join(" ", [
                    "The file's streaming data is missing from the server",
                    "(often a database restore without the blobs/ folder).",
                    "Remove and re-download the release, or restore from a backup that includes blobs."
                ]), ct).ConfigureAwait(false);
        }
        catch (UsenetArticleNotFoundException e)
        {
            CompleteHealthProgress(davItem.Id);
            if (FilenameUtil.IsImportantFileType(davItem.Name))
            {
                lock (_missingSegmentIds)
                {
                    if (_missingSegmentIds.Add(e.SegmentId))
                        _missingSegmentOrder.Enqueue(e.SegmentId);
                    while (_missingSegmentIds.Count > MaximumMissingSegmentIds)
                        _missingSegmentIds.Remove(_missingSegmentOrder.Dequeue());
                }
            }

            // when usenet article is missing, try PAR2 repair before Arr remove/re-grab
            if (_configManager.IsPar2RepairEnabled() && _configManager.IsPar2PreferredOverArr()
                && await _par2RepairService.TryPar2RepairAsync(davItem, [e.SegmentId], ct).ConfigureAwait(false))
            {
                var utcNow = DateTimeOffset.UtcNow;
                davItem.LastHealthCheck = utcNow;
                davItem.NextHealthCheck = ComputeNextHealthCheck(davItem.ReleaseDate, utcNow);
                _failureTracker.ClearFailure(davItem.Id);
                _arrNoMatchConfirmations.TryRemove(davItem.Id, out _);
                await RecordHealthResult(
                    dbClient, davItem,
                    HealthCheckResult.HealthResult.Healthy,
                    HealthCheckResult.RepairAction.RepairedViaPar2,
                    "Missing segment repaired from PAR2 parity.", ct).ConfigureAwait(false);
                return;
            }

            await Repair(davItem, dbClient, ct).ConfigureAwait(false);
        }
        catch (UsenetUnexpectedResponseException e)
        {
            // Connection-level STAT failures (e.g. buffered 400 goodbye) must not trigger
            // repair or leave NextHealthCheck unset — defer and surface ActionNeeded.
            CompleteHealthProgress(davItem.Id);
            var utcNow = DateTimeOffset.UtcNow;
            davItem.LastHealthCheck = utcNow;
            davItem.NextHealthCheck = utcNow + TimeSpan.FromDays(1);
            await RecordHealthResult(
                dbClient, davItem,
                HealthCheckResult.HealthResult.Unhealthy,
                HealthCheckResult.RepairAction.ActionNeeded,
                $"Unexpected NNTP response during health check: {e.Message}", ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e.IsTransientTransportException() && e is not OutOfMemoryException)
        {
            // STAT/read timeouts and socket/IO failures must not dump stacks or trigger Arr repair —
            // defer and surface ActionNeeded with a single human-readable Warning.
            CompleteHealthProgress(davItem.Id);
            var utcNow = DateTimeOffset.UtcNow;
            davItem.LastHealthCheck = utcNow;
            davItem.NextHealthCheck = utcNow + TimeSpan.FromDays(1);
            e.TryGetKnownErrorMessage(out var reason);
            Log.Warning(
                "NNTP transport failure during health check for {Path}. Deferred next check. Reason: {Reason}",
                davItem.Path, reason);
            Log.Debug(e, "Health check transport failure stack for {Path}", davItem.Path);
            await RecordHealthResult(
                dbClient, davItem,
                HealthCheckResult.HealthResult.Unhealthy,
                HealthCheckResult.RepairAction.ActionNeeded,
                FormatTransportFailureHealthMessage(reason), ct).ConfigureAwait(false);
        }
        catch (Exception e) when (!e.IsCancellationException(ct) && e is not OutOfMemoryException)
        {
            await DeferHealthCheck(davItem, dbClient, e, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Verdict handling for a full-coverage sweep that found confirmed holes on an
    /// eligible video file (#461): PAR2 reconstruction first, then container-aware
    /// classification. Degraded files persist their holes and stay on the recheck
    /// schedule instead of triggering Arr repair; Failed files take the same repair
    /// path as the legacy first-miss flow.
    /// </summary>
    private async Task HandleConfirmedHolesAsync(
        DavItem davItem,
        DavDatabaseClient dbClient,
        DavNzbFile nzbFile,
        List<string> segments,
        LongRange[] segmentRanges,
        List<int> holeIndices,
        CancellationToken ct)
    {
        var holeSegmentIds = holeIndices.Select(index => segments[index]).ToArray();

        // PAR2 first, with the full hole list: reconstruct from parity before any verdict.
        if (_configManager.IsPar2RepairEnabled() && _configManager.IsPar2PreferredOverArr()
            && await _par2RepairService.TryPar2RepairAsync(davItem, holeSegmentIds, ct).ConfigureAwait(false))
        {
            var utcNow = DateTimeOffset.UtcNow;
            davItem.LastHealthCheck = utcNow;
            davItem.NextHealthCheck = ComputeNextHealthCheck(davItem.ReleaseDate, utcNow);
            _failureTracker.ClearFailure(davItem.Id);
            _arrNoMatchConfirmations.TryRemove(davItem.Id, out _);
            // The patched segments are served locally now; any earlier hole record is obsolete.
            if (nzbFile.MissingSegmentIndices != null)
                await SwapNzbFileBlobAsync(davItem, nzbFile, null, null).ConfigureAwait(false);
            await RecordHealthResult(
                dbClient, davItem,
                HealthCheckResult.HealthResult.Healthy,
                HealthCheckResult.RepairAction.RepairedViaPar2,
                "Missing segment(s) repaired from PAR2 parity.", ct).ConfigureAwait(false);
            return;
        }

        var (containerClass, probedClass, criticalHeadEndExclusive) = await ResolveContainerClassAsync(
                davItem, nzbFile, segments, holeIndices, ct)
            .ConfigureAwait(false);
        var caps = new SegmentDamageCaps(
            _configManager.GetDegradedMaxConsecutiveMissing(),
            _configManager.GetDegradedMaxTotalMissing(),
            _configManager.GetDegradedMaxMissingBytePercent());
        var exactSegmentSizes = segmentRanges.Select(range => range.Count).ToArray();
        var segmentStarts = segmentRanges.Select(range => range.StartInclusive).ToArray();
        var verdict = SegmentDamageClassifier.Classify(
            holeIndices, segments.Count, exactSegmentSizes, segmentStarts,
            containerClass, caps, criticalHeadEndExclusive, out var reason);
        long? probedExtent = probedClass != null ? criticalHeadEndExclusive : null;

        if (verdict == SegmentDamageVerdict.Failed)
        {
            Log.Information(
                "Health check classified {Path} as failed: {Reason} Starting repair.",
                davItem.Path, reason);
            // Persist a fresh probe so a later check can reuse the class/extent without
            // another BODY. Do not record holes: Failed is not a degraded keep-the-file
            // verdict. Repair() commits the swapped FileBlobId with the health row.
            if (probedClass != null)
                await SwapNzbFileBlobAsync(
                    davItem, nzbFile, nzbFile.MissingSegmentIndices, probedClass, probedExtent)
                    .ConfigureAwait(false);
            // Seed the queue precheck with every confirmed miss so a re-grab of this release
            // fails fast pre-import (issue #732), then take today's repair path.
            if (FilenameUtil.IsImportantFileType(davItem.Name))
                AddMissingSegmentIds(holeSegmentIds);
            await Repair(davItem, dbClient, ct).ConfigureAwait(false);
            return;
        }

        // Degraded: keep the file and persist the confirmed holes — but only when the record
        // actually changed, so an unchanged recheck does not churn blobs. Do not call Repair,
        // do not seed the fail-fast reimport cache (a re-grab of a release we chose to keep
        // must not fail), and do not clear the streaming-failure count: confirmed damage is
        // not health, and real playback failures keep escalating toward auto-remove.
        var recordChanged = nzbFile.MissingSegmentIndices is not { } existing
                            || !existing.SequenceEqual(holeIndices)
                            || (probedClass != null && nzbFile.ContainerClass != probedClass)
                            || (probedExtent != null && nzbFile.CriticalHeadEndExclusive != probedExtent);
        if (recordChanged)
            await SwapNzbFileBlobAsync(
                    davItem, nzbFile, holeIndices.ToArray(), probedClass, probedExtent)
                .ConfigureAwait(false);

        Log.Warning(
            "Health check classified {Path} as degraded: {Reason} Playback fills the gaps; repair skipped.",
            davItem.Path, reason);
        var degradedUtcNow = DateTimeOffset.UtcNow;
        davItem.LastHealthCheck = degradedUtcNow;
        davItem.NextHealthCheck = ComputeNextHealthCheck(davItem.ReleaseDate, degradedUtcNow);
        await RecordHealthResult(
            dbClient, davItem,
            HealthCheckResult.HealthResult.Degraded,
            HealthCheckResult.RepairAction.None,
            $"{reason} within tolerance for {MediaContainerClassMapping.Describe(containerClass)}. " +
            "Playback fills the gaps; repair skipped.", ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves the container class for the verdict: fixed by extension for the MKV/TS
    /// family, reused once persisted, otherwise probed once from a bounded read of the
    /// file head (MP4 family only). Probe failures propagate to the caller's catches —
    /// no verdict is recorded this cycle and the probe is retried next time.
    /// </summary>
    private async Task<(MediaContainerClass ContainerClass, byte? ProbedClass, long CriticalHeadEndExclusive)>
        ResolveContainerClassAsync(
            DavItem davItem,
            DavNzbFile nzbFile,
            List<string> segments,
            List<int> holeIndices,
            CancellationToken ct)
    {
        if (MediaContainerClassMapping.ByExtension(davItem.Name) is { } byExtension)
            return (byExtension, null, 0);

        if (nzbFile.ContainerClass is byte persisted && Enum.IsDefined((MediaContainerClass)persisted))
            return ((MediaContainerClass)persisted, null, nzbFile.CriticalHeadEndExclusive ?? 0);

        // A hole at segment 0 fails classification regardless, and probing a missing
        // segment would just throw; never probe when the head is a hole.
        if (holeIndices[0] == 0)
            return (MediaContainerClass.Unknown, null, 0);

        // One bounded head read per file, ever: the probed class is persisted with the
        // hole record. Runs inside the caller's maintenance download context (attribution)
        // and cancellation scope; early disposal of the body stream is by design.
        var response = await _usenetClient.DecodedBodyAsync(segments[0], ct).ConfigureAwait(false);
        if (response.Stream is not { } headStream)
            throw new UsenetUnexpectedResponseException(segments[0], response.ResponseMessage);
        await using (headStream)
        {
            var buffer = new byte[64 * 1024];
            var filled = 0;
            while (filled < buffer.Length)
            {
                var read = await headStream.ReadAsync(buffer.AsMemory(filled), ct).ConfigureAwait(false);
                if (read <= 0) break;
                filled += read;
            }

            var (probed, extent) = Mp4LayoutProbe.ClassifyMp4Head(buffer.AsSpan(0, filled));
            return (probed, (byte)probed, extent);
        }
    }

    /// <summary>
    /// Playback fetches fallback MessageIds before zero-filling
    /// (MultiSegmentStream/UnbufferedMultiSegmentStream), so a primary-miss segment with
    /// any live fallback is servable, not a hole. A segment is a hole only when the
    /// primary and every fallback are definitively missing. Non-definitive responses
    /// throw into the defer catches rather than guessing a verdict.
    /// </summary>
    private async Task<List<int>> ConfirmHolesThroughFallbacksAsync(
        IReadOnlyList<string> primaryMissIds,
        List<string> segments,
        DavNzbFile nzbFile,
        int concurrency,
        ContextualCancellationTokenSource statCts)
    {
        if (primaryMissIds.Count == 0) return [];

        var indexById = segments
            .Select((id, index) => (id, index))
            .ToDictionary(x => x.id, x => x.index, StringComparer.Ordinal);

        var checks = primaryMissIds
            .Select(missId => (Id: missId, Index: indexById.GetValueOrDefault(missId, -1)))
            .Where(miss => miss.Index >= 0)
            .Select(async miss => (
                miss.Index,
                IsHole: await IsConfirmedHoleAsync(miss.Index, nzbFile, statCts.Token).ConfigureAwait(false)))
            .WithConcurrencyAsync(concurrency, statCts.Token);

        var holes = new List<int>();
        await foreach (var (index, isHole) in checks.ConfigureAwait(false))
        {
            // keep the no-progress watchdog armed while fallback STATs are in flight
            statCts.CancelAfter(HealthCheckProgressTimeout);
            if (isHole) holes.Add(index);
        }

        holes.Sort();
        return holes;
    }

    private async Task<bool> IsConfirmedHoleAsync(int segmentIndex, DavNzbFile nzbFile, CancellationToken ct)
    {
        if (nzbFile.SegmentFallbackIds is not { } fallbackIds ||
            segmentIndex >= fallbackIds.Length ||
            fallbackIds[segmentIndex] is not { Length: > 0 } alternates)
            return true;

        foreach (var fallbackId in alternates)
        {
            var response = await _usenetClient.StatAsync(fallbackId, ct).ConfigureAwait(false);
            if (response.ResponseType == UsenetResponseType.ArticleExists)
                return false;
            if (!UsenetArticleAvailability.IsDefinitiveMissing(response))
                throw new UsenetUnexpectedResponseException(fallbackId, response.ResponseMessage);
        }

        return true;
    }

    /// <summary>
    /// Persists the degraded-damage record by writing a NEW blob and swapping
    /// <see cref="DavItem.FileBlobId"/>, committed by the caller's SaveChangesAsync in
    /// the same transaction as the health row. The TR_DavItems_Update_AddBlobCleanup
    /// trigger queues the old blob for deferred cleanup and in-flight readers keep their
    /// open handle. The instance handed in is the shared MetadataCache entry — never
    /// mutate it; write a fresh copy.
    /// </summary>
    private static async Task SwapNzbFileBlobAsync(
        DavItem davItem,
        DavNzbFile nzbFile,
        int[]? missingSegmentIndices,
        byte? probedContainerClass,
        long? probedCriticalHeadEndExclusive = null)
    {
        var updated = new DavNzbFile
        {
            Id = nzbFile.Id,
            SegmentIds = nzbFile.SegmentIds,
            SegmentByteRanges = nzbFile.SegmentByteRanges,
            SegmentFallbackIds = nzbFile.SegmentFallbackIds,
            MissingSegmentIndices = missingSegmentIndices,
            ContainerClass = probedContainerClass ?? nzbFile.ContainerClass,
            CriticalHeadEndExclusive = probedCriticalHeadEndExclusive ?? nzbFile.CriticalHeadEndExclusive,
        };
        var newBlobId = Guid.NewGuid();
        await BlobStore.WriteBlob(newBlobId, updated).ConfigureAwait(false);
        davItem.FileBlobId = newBlobId;
    }

    private void CompleteHealthProgress(Guid davItemId)
    {
        _ = _websocketManager.SendMessage(WebsocketTopic.HealthItemProgress, $"{davItemId}|100");
        _ = _websocketManager.SendMessage(WebsocketTopic.HealthItemProgress, $"{davItemId}|done");
    }

    private async Task DeferHealthCheck(
        DavItem davItem,
        DavDatabaseClient dbClient,
        Exception exception,
        CancellationToken ct)
    {
        var isKnownFailure = exception.TryGetKnownErrorMessage(out var reason);
        var utcNow = DateTimeOffset.UtcNow;
        davItem.LastHealthCheck = utcNow;
        davItem.NextHealthCheck = ComputeFailureNextHealthCheck(utcNow, isKnownFailure);

        CompleteHealthProgress(davItem.Id);

        if (isKnownFailure)
        {
            Log.Warning(
                "Health check failed for {Path}. Deferred next check until {NextHealthCheck}. Reason: {Reason}",
                davItem.Path, davItem.NextHealthCheck, reason);
            Log.Debug(exception, "Health check known failure stack for {Path}", davItem.Path);
        }
        else
        {
            Log.Error(
                exception,
                "Unexpected error during health check for {Path}. Deferred next check until {NextHealthCheck}",
                davItem.Path, davItem.NextHealthCheck);
        }

        try
        {
            await RecordHealthResult(
                dbClient, davItem,
                HealthCheckResult.HealthResult.Unhealthy,
                HealthCheckResult.RepairAction.ActionNeeded,
                isKnownFailure
                    ? $"Health check deferred: {reason}"
                    : $"Unexpected error during health check: {exception.Message}",
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception persistenceException) when (persistenceException is DbUpdateException or InvalidOperationException)
        {
            Log.Error(
                persistenceException,
                "Could not record deferred health-check result for {Path}; retrying schedule persistence",
                davItem.Path);

            foreach (var entry in dbClient.Ctx.ChangeTracker.Entries<HealthCheckResult>()
                         .Where(x => x.State == EntityState.Added))
                entry.State = EntityState.Detached;

            try
            {
                await dbClient.Ctx.SaveChangesAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception scheduleException) when (scheduleException is DbUpdateException or InvalidOperationException)
            {
                Log.Error(
                    scheduleException,
                    "Could not persist deferred health-check schedule for {Path}",
                    davItem.Path);
            }
        }
    }

    /// <summary>
    /// Health-result message for deferred NNTP transport failures (timeouts, socket/IO).
    /// </summary>
    internal static string FormatTransportFailureHealthMessage(string reason) =>
        $"NNTP transport failure during health check: {reason}";

    /// <summary>
    /// Schedules a failed health check far enough in the future to avoid starving
    /// other items, while allowing known provider/configuration failures more time
    /// to be corrected than unexpected application failures.
    /// </summary>
    public static DateTimeOffset ComputeFailureNextHealthCheck(
        DateTimeOffset utcNow,
        bool knownFailure) =>
        utcNow + (knownFailure ? TimeSpan.FromDays(1) : TimeSpan.FromHours(1));

    /// <summary>
    /// Schedules the next health check so the interval doubles with the item's age since release,
    /// floored at one hour from <paramref name="utcNow"/> so null or future release dates cannot
    /// schedule the item in the past and hot-loop the service.
    /// </summary>
    public static DateTimeOffset ComputeNextHealthCheck(DateTimeOffset? releaseDate, DateTimeOffset utcNow)
    {
        var minimumNextHealthCheck = utcNow + TimeSpan.FromHours(1);
        var nextHealthCheck = releaseDate + 2 * (utcNow - releaseDate);
        return nextHealthCheck == null || nextHealthCheck < minimumNextHealthCheck
            ? minimumNextHealthCheck
            : nextHealthCheck.Value;
    }

    /// <summary>
    /// How many segments to STAT for one file. Files up to the floor are checked in full,
    /// larger ones are sampled based on their size, and an optional age scales the result
    /// down from there. <see cref="HealthCheckDepth.Complete"/> skips all of it.
    /// </summary>
    public static int SampleTarget(int segmentCount, HealthCheckDepth depth, TimeSpan? age = null)
    {
        if (depth == HealthCheckDepth.Complete) return segmentCount;
        var multiplier = CurveMultiplier(depth);
        var curve = Math.Max(SampleFloor, multiplier * Math.Sqrt((double)SampleFloor * segmentCount));
        return (int)Math.Min(segmentCount, curve * AgeWeight(age));
    }

    /// <summary>
    /// What each depth multiplies the sampling curve by. Complete has no multiplier because
    /// it never reaches the curve.
    /// </summary>
    private static double CurveMultiplier(HealthCheckDepth depth) => depth switch
    {
        HealthCheckDepth.Standard => 0.5,
        HealthCheckDepth.Enhanced => 1.0,
        HealthCheckDepth.Deep => 2.0,
        _ => throw new ArgumentOutOfRangeException(nameof(depth), depth, "Depth has no curve multiplier."),
    };

    /// <summary>
    /// Scales coverage down as a release ages with the same square root curve used for size.
    /// A post that has survived its first year is far less likely to be broken, so it
    /// gets a lighter check. The taper stops at ten years so nothing decays toward zero.
    /// </summary>
    private static double AgeWeight(TimeSpan? age)
    {
        if (age is not { } elapsed) return 1.0;
        return Math.Sqrt(FullDepthDays / Math.Clamp(elapsed.TotalDays, FullDepthDays, MinDepthDays));
    }

    /// <summary>
    /// Returns a stratified sample of <paramref name="segments"/>: first 100, last 100, and
    /// evenly spaced middle segments, sized by <see cref="SampleTarget"/>.
    /// </summary>
    public static List<string> SampleSegments(
        List<string> segments,
        HealthCheckDepth depth = ConfigManager.DefaultHealthCheckDepth,
        TimeSpan? age = null)
    {
        var count = segments.Count;
        var target = SampleTarget(count, depth, age);
        if (count <= target) return segments;

        const int headCount = 100;
        const int tailCount = 100;

        var result = new HashSet<int>();

        for (var i = 0; i < Math.Min(headCount, count); i++)
            result.Add(i);

        for (var i = Math.Max(0, count - tailCount); i < count; i++)
            result.Add(i);

        // Spread `target` picks across the file with a running remainder, so the sample lands
        // on the target instead of on the nearest whole number stride.
        var carry = 0L;
        for (var i = 0; i < count; i++)
        {
            carry += target;
            if (carry < count) continue;
            carry -= count;
            result.Add(i);
        }

        return result.OrderBy(i => i).Select(i => segments[i]).ToList();
    }

    internal static List<string> FilterSegmentsForStat(
        List<string> sampledSegmentIds,
        List<string> allSegmentIds,
        DavNzbFile nzbFile,
        RepairPatchStore patchStore)
    {
        if (!patchStore.IsCatalogReady) return sampledSegmentIds;

        var indexById = allSegmentIds
            .Select((id, index) => (id, index))
            .ToDictionary(x => x.id, x => x.index, StringComparer.Ordinal);
        var ranges = nzbFile.SegmentByteRanges;

        return sampledSegmentIds
            .Where(segmentId => !indexById.TryGetValue(segmentId, out var index)
                                || ranges == null
                                || index >= ranges.Length
                                || !patchStore.IsRepaired(segmentId, ranges[index].Count))
            .ToList();
    }

    private async Task UpdateReleaseDate(DavItem davItem, List<string> segments, CancellationToken ct)
    {
        var firstSegmentId = StringUtil.EmptyToNull(segments.FirstOrDefault());
        if (firstSegmentId == null) return;
        var articleHeadersResponse = await _usenetClient.HeadAsync(firstSegmentId, ct).ConfigureAwait(false);
        if (articleHeadersResponse.ArticleHeaders is not { } articleHeaders)
            throw new UsenetUnexpectedResponseException(
                firstSegmentId, articleHeadersResponse.ResponseMessage);
        davItem.ReleaseDate = articleHeaders.Date;
    }

    private static async Task EnsurePayloadExistsAsync(
        DavItem davItem,
        DavDatabaseClient dbClient,
        CancellationToken ct)
    {
        var exists = davItem.SubType switch
        {
            DavItem.ItemSubType.NzbFile =>
                await dbClient.GetDavNzbFileAsync(davItem, ct).ConfigureAwait(false) is not null,
            DavItem.ItemSubType.RarFile =>
                await dbClient.GetDavRarFileAsync(davItem, ct).ConfigureAwait(false) is not null,
            DavItem.ItemSubType.MultipartFile =>
                await dbClient.GetDavMultipartFileAsync(davItem, ct).ConfigureAwait(false) is not null,
            _ => true,
        };
        if (!exists) throw new MissingFilePayloadException(davItem, davItem.SubType);
    }

    private async Task<List<string>> GetAllSegments(DavItem davItem, DavDatabaseClient dbClient, CancellationToken ct)
    {
        if (davItem.SubType == DavItem.ItemSubType.NzbFile)
        {
            var nzbFile = await dbClient.GetDavNzbFileAsync(davItem, ct).ConfigureAwait(false);
            return nzbFile?.SegmentIds?.ToList()
                ?? throw new MissingFilePayloadException(davItem, DavItem.ItemSubType.NzbFile);
        }

        if (davItem.SubType == DavItem.ItemSubType.RarFile)
        {
            var rarFile = await dbClient.GetDavRarFileAsync(davItem, ct).ConfigureAwait(false);
            return rarFile?.RarParts?.SelectMany(x => x.SegmentIds)?.ToList()
                ?? throw new MissingFilePayloadException(davItem, DavItem.ItemSubType.RarFile);
        }

        if (davItem.SubType == DavItem.ItemSubType.MultipartFile)
        {
            var multipartFile = await dbClient.GetDavMultipartFileAsync(davItem, ct).ConfigureAwait(false);
            return multipartFile?.Metadata?.FileParts?.SelectMany(x => x.SegmentIds)?.ToList()
                ?? throw new MissingFilePayloadException(davItem, DavItem.ItemSubType.MultipartFile);
        }

        return [];
    }

    /// <summary>
    /// How an urgent (streaming-triggered) repair should proceed given the streaming-failure threshold.
    /// </summary>
    public enum UrgentRepairDisposition
    {
        /// <summary>Call Repair() without forcing deletion.</summary>
        RepairNormally,
        /// <summary>Clear the urgent flag and wait for more streaming failures.</summary>
        Defer,
        /// <summary>Force the repair-delete path even for library-linked items.</summary>
        ForceDelete,
    }

    /// <summary>
    /// Decides urgent-repair disposition from failure count and auto-remove config.
    /// Threshold 0 preserves immediate repair; otherwise all streaming-triggered actions wait
    /// until the configured number of consecutive failures.
    /// </summary>
    public static UrgentRepairDisposition GetUrgentRepairDisposition(
        int threshold,
        int failureCount,
        bool hasLibraryLink,
        bool autoRemoveUnlinkedOnly)
    {
        if (threshold <= 0)
            return UrgentRepairDisposition.RepairNormally;

        if (failureCount < threshold)
            return UrgentRepairDisposition.Defer;

        if (hasLibraryLink && autoRemoveUnlinkedOnly)
            return UrgentRepairDisposition.RepairNormally;

        return UrgentRepairDisposition.ForceDelete;
    }

    /// <summary>
    /// How repair should treat the result of the organized-library lookup.
    /// </summary>
    internal enum LibraryLinkRepairDisposition
    {
        RepairLinked,
        DeferMissingLink,
        ForceDelete,
    }

    internal static LibraryLinkRepairDisposition GetLibraryLinkRepairDisposition(
        string? symlinkOrStrmPath,
        bool forceDelete)
    {
        if (forceDelete)
            return LibraryLinkRepairDisposition.ForceDelete;

        return symlinkOrStrmPath == null
            ? LibraryLinkRepairDisposition.DeferMissingLink
            : LibraryLinkRepairDisposition.RepairLinked;
    }

    internal static bool ShouldDeleteAfterArrNoMatch(int confirmationCount) =>
        confirmationCount >= NoMatchConfirmationsRequired;

    /// <summary>
    /// True when the linked library path has already had <see cref="RepairRecurrenceLimit"/>
    /// downloads removed by repair within <see cref="RepairRecurrenceWindow"/>. The path is the
    /// stable identity across replacement cycles: each re-grab creates a new DavItem, but Arr
    /// imports it to the same library location.
    /// </summary>
    internal static bool IsRepairRateLimited(string linkedPath, DateTimeOffset utcNow)
    {
        if (!_recentRepairRemovalsByPath.TryGetValue(linkedPath, out var removals))
            return false;
        lock (removals)
        {
            removals.RemoveAll(x => utcNow - x >= RepairRecurrenceWindow);
            return removals.Count >= RepairRecurrenceLimit;
        }
    }

    internal static void RecordRepairRemoval(string linkedPath, DateTimeOffset utcNow)
    {
        var removals = _recentRepairRemovalsByPath.GetOrAdd(linkedPath, _ => []);
        lock (removals)
        {
            removals.RemoveAll(x => utcNow - x >= RepairRecurrenceWindow);
            removals.Add(utcNow);
        }

        if (_recentRepairRemovalsByPath.Count <= MaximumTrackedRepairPaths)
            return;

        // Best-effort prune of fully stale paths; a concurrent add racing a TryRemove can at
        // worst drop one timestamp, which only makes the rate limiter marginally more lenient.
        foreach (var entry in _recentRepairRemovalsByPath)
        {
            bool stale;
            lock (entry.Value)
            {
                entry.Value.RemoveAll(x => utcNow - x >= RepairRecurrenceWindow);
                stale = entry.Value.Count == 0;
            }

            if (stale)
                _recentRepairRemovalsByPath.TryRemove(entry.Key, out _);
        }
    }

    /// <summary>
    /// Outcome of consulting Arr instances for a library-linked unhealthy item.
    /// </summary>
    public enum ArrLinkedRepairDecision
    {
        /// <summary>An Arr instance owned the file and remove-and-blocklist succeeded.</summary>
        RemoveAndBlocklistSucceeded,
        /// <summary>
        /// At least one Arr instance was unreachable/unusable and no instance completed repair —
        /// leave the DavItem in place.
        /// </summary>
        DeferUnreachable,
        /// <summary>
        /// The Arr media item exists, but its original download history cannot be identified.
        /// Leave it in place rather than searching again without blocklisting the failed release.
        /// </summary>
        DeferMissingDownloadHistory,
        /// <summary>No reachable Arr instance confirmed ownership; leave the organized link in place.</summary>
        DeferNoMatchingMediaItem,
    }

    /// <summary>
    /// Consults Arr clients to decide whether a library-linked unhealthy item should trigger
    /// remove-and-blocklist or be deferred when ownership cannot be confirmed safely.
    /// Extracted so the unreachable-instance fail-safe can be unit-tested without a full Repair harness.
    /// </summary>
    internal static async Task<ArrLinkedRepairDecision> DecideArrLinkedRepairAsync(
        IEnumerable<ArrClient> arrClients,
        string symlinkOrStrmPath,
        Guid? downloadId,
        CancellationToken ct)
    {
        // Track whether a no-owner result is authoritative enough to explain to the operator.
        // Neither outcome permits deletion: a successful-but-incomplete library/Arr view is
        // indistinguishable from a genuine orphan at this point.
        var anInstanceFailed = false;

        foreach (var arrClient in arrClients)
        {
            ct.ThrowIfCancellationRequested();

            List<ArrRootFolder> rootFolders;
            try
            {
                rootFolders = await arrClient.GetRootFolders(ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException)
            {
                anInstanceFailed = true;
                LogArrRepairFailure(
                    e,
                    "Health-check repair: could not query root folders from {Host}",
                    arrClient.Host);
                continue;
            }

            // Skip null/empty paths: StartsWith(null) throws, and StartsWith("") matches everything.
            // A null/empty path is a malformed response we can't rule this instance in or out
            // with, so if nothing else matches, treat it like an unreachable instance rather
            // than falling through to a delete this instance may not have sanctioned.
            if (!rootFolders.Any(x => !string.IsNullOrEmpty(x.Path) && symlinkOrStrmPath.StartsWith(x.Path, StringComparison.Ordinal)))
            {
                if (rootFolders.Any(x => string.IsNullOrEmpty(x.Path))) anInstanceFailed = true;
                continue;
            }

            if (downloadId == null)
                return ArrLinkedRepairDecision.DeferMissingDownloadHistory;

            ArrRepairOutcome repairOutcome;
            try
            {
                repairOutcome = await arrClient.RemoveAndBlocklist(
                    symlinkOrStrmPath,
                    downloadId.Value,
                    ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException)
            {
                anInstanceFailed = true;
                LogArrRepairFailure(
                    e,
                    "Health-check repair: remove-and-blocklist failed on {Host}",
                    arrClient.Host);
                continue;
            }

            if (repairOutcome == ArrRepairOutcome.RemoveAndBlocklistSucceeded)
                return ArrLinkedRepairDecision.RemoveAndBlocklistSucceeded;

            if (repairOutcome == ArrRepairOutcome.DownloadHistoryNotFound)
                return ArrLinkedRepairDecision.DeferMissingDownloadHistory;

            // A root-folder match with no exact media-item match may be caused by path
            // normalization, stale Arr data, or a transient partial response. Keep checking
            // other configured instances, but never use this single miss to authorize deletion.
        }

        if (anInstanceFailed)
            return ArrLinkedRepairDecision.DeferUnreachable;

        return ArrLinkedRepairDecision.DeferNoMatchingMediaItem;
    }

    private static void LogArrRepairFailure(Exception exception, string messageTemplate, string host)
    {
        if (exception.TryGetCausingException<HttpRequestException>(out var httpException) &&
            httpException is not null)
        {
            var reason = httpException.StatusCode is { } statusCode
                ? $"HTTP {(int)statusCode} ({statusCode})"
                : httpException.Message;
            Log.Warning(messageTemplate + ". Reason: {Reason}", host, reason);
            Log.Debug(exception, "Health-check repair Arr HTTP failure stack");
            return;
        }

        if (exception.TryGetKnownErrorMessage(out var knownReason))
        {
            Log.Warning(messageTemplate + ". Reason: {Reason}", host, knownReason);
            Log.Debug(exception, "Health-check repair Arr known failure stack");
            return;
        }

        Log.Warning(exception, messageTemplate, host);
    }

    private async Task HandleUrgentRepair(DavItem davItem, DavDatabaseClient dbClient, CancellationToken ct)
    {
        var threshold = _configManager.GetAutoRemoveAfterFailures();
        var failureCount = _failureTracker.GetFailureCount(davItem.Id);
        var unlinkedOnly = _configManager.IsAutoRemoveUnlinkedOnly();
        var hasLibraryLink = OrganizedLinksUtil.GetLink(davItem, _configManager) != null;
        var disposition = GetUrgentRepairDisposition(threshold, failureCount, hasLibraryLink, unlinkedOnly);

        if (disposition == UrgentRepairDisposition.Defer)
        {
            var utcNow = DateTimeOffset.UtcNow;
            davItem.LastHealthCheck = utcNow;
            davItem.NextHealthCheck = utcNow + TimeSpan.FromHours(1);
            await RecordHealthResult(
                dbClient, davItem,
                HealthCheckResult.HealthResult.Unhealthy,
                HealthCheckResult.RepairAction.ActionNeeded,
                string.Join(" ", [
                    "File failed during streaming.",
                    $"Streaming failure count: {failureCount}/{threshold}.",
                    "Repair and replacement deferred until the failure threshold is reached."
                ]), ct).ConfigureAwait(false);
            return;
        }

        if (_configManager.IsPar2RepairEnabled() && _configManager.IsPar2PreferredOverArr()
            && await _par2RepairService.TryPar2RepairAsync(davItem, null, ct).ConfigureAwait(false))
        {
            var utcNow = DateTimeOffset.UtcNow;
            davItem.LastHealthCheck = utcNow;
            davItem.NextHealthCheck = ComputeNextHealthCheck(davItem.ReleaseDate, utcNow);
            _failureTracker.ClearFailure(davItem.Id);
            _arrNoMatchConfirmations.TryRemove(davItem.Id, out _);
            await RecordHealthResult(
                dbClient, davItem,
                HealthCheckResult.HealthResult.Healthy,
                HealthCheckResult.RepairAction.RepairedViaPar2,
                "Missing segment(s) repaired from PAR2 parity after streaming failure.", ct)
                .ConfigureAwait(false);
            return;
        }

        await Repair(
            davItem,
            dbClient,
            ct,
            forceDelete: disposition == UrgentRepairDisposition.ForceDelete,
            streamingFailureCount: failureCount).ConfigureAwait(false);
    }

    private async Task Repair(
        DavItem davItem,
        DavDatabaseClient dbClient,
        CancellationToken ct,
        bool forceDelete = false,
        int? streamingFailureCount = null)
    {
        try
        {
            // if the file pattern has been marked as ignored,
            // then don't bother trying to repair it. We can simply delete it.
            var blocklistedFiles = _configManager.GetBlocklistedFiles();
            if (BlocklistedFilePostProcessor.MatchesAnyPattern(davItem.Name, blocklistedFiles))
            {
                DeletionAuditLog.Record(
                    "health-repair",
                    davItem,
                    "health validation failed; filename matches blocklist pattern");
                dbClient.Ctx.Items.Remove(davItem);
                _failureTracker.ClearFailure(davItem.Id);
                await RecordHealthResult(
                    dbClient, davItem,
                    HealthCheckResult.HealthResult.Unhealthy,
                    HealthCheckResult.RepairAction.Deleted,
                    string.Join(" ", [
                        "File failed health validation.",
                        "Filename pattern is marked in settings as an ignored (unwanted) file.",
                        "Deleted file."
                    ]), ct).ConfigureAwait(false);
                return;
            }

            // A missing library link is not proof that the item is orphaned: a FUSE mount can
            // temporarily present a successfully empty or partial view. Only an explicit
            // force-delete policy may remove the item from this branch.
            var symlinkOrStrmPath = OrganizedLinksUtil.GetLink(davItem, _configManager);
            var linkDisposition = GetLibraryLinkRepairDisposition(symlinkOrStrmPath, forceDelete);
            if (linkDisposition != LibraryLinkRepairDisposition.RepairLinked)
                _arrNoMatchConfirmations.TryRemove(davItem.Id, out _);
            if (linkDisposition == LibraryLinkRepairDisposition.ForceDelete)
            {
                if (symlinkOrStrmPath != null)
                    await Task.Run(() => File.Delete(symlinkOrStrmPath)).ConfigureAwait(false);

                var auditReason = streamingFailureCount is > 0
                    ? $"auto-removed after repeated streaming failures (count={streamingFailureCount})"
                    : "auto-removed after repeated streaming failures";
                DeletionAuditLog.Record("health-repair", davItem, auditReason);

                dbClient.Ctx.Items.Remove(davItem);
                _failureTracker.ClearFailure(davItem.Id);

                var failureNote = streamingFailureCount is > 0
                    ? $" Streaming failure count: {streamingFailureCount}."
                    : "";
                string deleteMessage;
                if (symlinkOrStrmPath != null)
                {
                    var forceDeleteLinkType = symlinkOrStrmPath.ToLowerInvariant().EndsWith("strm", StringComparison.Ordinal) ? "strm-file" : "symlink";
                    deleteMessage = string.Join(" ", [
                        "File failed during streaming.",
                        $"Auto-removed after repeated streaming failures.{failureNote}",
                        $"Deleted the webdav-file and {forceDeleteLinkType}."
                    ]);
                }
                else
                {
                    deleteMessage = string.Join(" ", [
                        "File failed during streaming.",
                        $"Auto-removed after repeated streaming failures.{failureNote}",
                        "Deleted file."
                    ]);
                }

                await RecordHealthResult(
                    dbClient, davItem,
                    HealthCheckResult.HealthResult.Unhealthy,
                    HealthCheckResult.RepairAction.Deleted,
                    deleteMessage, ct).ConfigureAwait(false);
                return;
            }

            if (linkDisposition == LibraryLinkRepairDisposition.DeferMissingLink)
            {
                var utcNow = DateTimeOffset.UtcNow;
                davItem.LastHealthCheck = utcNow;
                davItem.NextHealthCheck = utcNow + TimeSpan.FromDays(1);
                await RecordHealthResult(
                    dbClient, davItem,
                    HealthCheckResult.HealthResult.Unhealthy,
                    HealthCheckResult.RepairAction.ActionNeeded,
                    string.Join(" ", [
                        "File failed health validation.",
                        "Could not find a corresponding symlink or strm-file within Library Dir.",
                        "The library scan may be incomplete, so the webdav-file was left in place.",
                        "Use Remove Orphaned Files for deliberate unlinked-item cleanup."
                    ]), ct).ConfigureAwait(false);
                return;
            }

            var linkedPath = symlinkOrStrmPath!;
            var linkType = linkedPath.ToLowerInvariant().EndsWith("strm", StringComparison.Ordinal) ? "strm-file" : "symlink";

            // Rate-limit repairs per library file: when Arr keeps re-importing broken
            // replacements to the same location, deleting again only fuels the loop.
            if (IsRepairRateLimited(linkedPath, DateTimeOffset.UtcNow))
            {
                Log.Warning(
                    "Health-check repair rate limit reached for library file {LinkedPath}: " +
                    "{RemovalCount} downloads already removed for this file within {WindowHours} hours. " +
                    "Leaving the file in place to break the replacement loop.",
                    linkedPath,
                    RepairRecurrenceLimit,
                    RepairRecurrenceWindow.TotalHours);
                var deferUntil = DateTimeOffset.UtcNow;
                davItem.LastHealthCheck = deferUntil;
                davItem.NextHealthCheck = deferUntil + TimeSpan.FromDays(1);
                await RecordHealthResult(
                    dbClient, davItem,
                    HealthCheckResult.HealthResult.Unhealthy,
                    HealthCheckResult.RepairAction.ActionNeeded,
                    string.Join(" ", [
                        "File failed health validation.",
                        $"Repair already removed {RepairRecurrenceLimit} downloads for this library file",
                        $"within the last {RepairRecurrenceWindow.TotalHours:0} hours,",
                        "which indicates a replacement loop.",
                        "Leaving the file in place rather than triggering another replacement."
                    ]), ct).ConfigureAwait(false);
                return;
            }

            // if the unhealthy item is linked within the organized media-library
            // then we must find the corresponding arr instance and trigger a new search.
            var arrDecision = await DecideArrLinkedRepairAsync(
                _configManager.GetArrConfig().GetArrClients(),
                linkedPath,
                davItem.HistoryItemId ?? davItem.NzbBlobId,
                ct).ConfigureAwait(false);

            if (arrDecision != ArrLinkedRepairDecision.DeferNoMatchingMediaItem)
                _arrNoMatchConfirmations.TryRemove(davItem.Id, out _);

            if (arrDecision == ArrLinkedRepairDecision.RemoveAndBlocklistSucceeded)
            {
                RecordRepairRemoval(linkedPath, DateTimeOffset.UtcNow);
                await SeedRejectedReleaseSegmentsAsync(davItem, dbClient, ct).ConfigureAwait(false);
                DeletionAuditLog.Record(
                    "health-repair",
                    davItem,
                    "health validation failed; Arr media removed and original download blocklisted");
                dbClient.Ctx.Items.Remove(davItem);
                _failureTracker.ClearFailure(davItem.Id);
                await RecordHealthResult(
                    dbClient, davItem,
                    HealthCheckResult.HealthResult.Unhealthy,
                    HealthCheckResult.RepairAction.Repaired,
                    string.Join(" ", [
                        "File failed health validation.",
                        $"Corresponding {linkType} found within Library Dir.",
                        "Removed the Arr media file and blocklisted its original download.",
                        "Arr was notified to search for a replacement."
                    ]), ct).ConfigureAwait(false);
                return;
            }

            if (arrDecision == ArrLinkedRepairDecision.DeferMissingDownloadHistory)
            {
                var utcNow = DateTimeOffset.UtcNow;
                davItem.LastHealthCheck = utcNow;
                davItem.NextHealthCheck = utcNow + TimeSpan.FromDays(1);
                await RecordHealthResult(
                    dbClient, davItem,
                    HealthCheckResult.HealthResult.Unhealthy,
                    HealthCheckResult.RepairAction.ActionNeeded,
                    string.Join(" ", [
                        "File failed health validation.",
                        $"Corresponding {linkType} and Arr media item found,",
                        "but the original Arr download history could not be identified.",
                        "Leaving the file in place rather than searching again without blocklisting the failed release."
                    ]), ct).ConfigureAwait(false);
                return;
            }

            // Ownership indeterminate (an instance could not be reached or fully queried, and no
            // instance confirmed the file as an orphan): don't delete a link it may own. Defer like
            // the catch below so the item isn't re-selected every scan cycle while the instance is down.
            if (arrDecision == ArrLinkedRepairDecision.DeferUnreachable)
            {
                var utcNow = DateTimeOffset.UtcNow;
                davItem.LastHealthCheck = utcNow;
                davItem.NextHealthCheck = utcNow + TimeSpan.FromDays(1);
                await RecordHealthResult(
                    dbClient, davItem,
                    HealthCheckResult.HealthResult.Unhealthy,
                    HealthCheckResult.RepairAction.ActionNeeded,
                    string.Join(" ", [
                        "File failed health validation.",
                        $"Corresponding {linkType} found within Library Dir,",
                        "but at least one Arr instance could not be reached or fully queried, so ownership",
                        "of the file could not be determined. Leaving the file in place rather than deleting it."
                    ]), ct).ConfigureAwait(false);
                return;
            }

            // A reachable Arr returning no exact media-item match is inconclusive once, because
            // path normalization or a partial Arr response can produce a false miss. Require a
            // second consecutive fully reachable miss before deliberately removing a confirmed
            // Arr orphan. This also gives genuine orphan links a cleanup path; the maintenance
            // task cannot see them as unlinked while the organized link still exists.
            var noMatchConfirmations = _arrNoMatchConfirmations.AddOrUpdate(
                davItem.Id,
                1,
                (_, previous) => previous + 1);
            if (!ShouldDeleteAfterArrNoMatch(noMatchConfirmations))
            {
                var noMatchUtcNow = DateTimeOffset.UtcNow;
                davItem.LastHealthCheck = noMatchUtcNow;
                davItem.NextHealthCheck = noMatchUtcNow + TimeSpan.FromDays(1);
                await RecordHealthResult(
                    dbClient, davItem,
                    HealthCheckResult.HealthResult.Unhealthy,
                    HealthCheckResult.RepairAction.ActionNeeded,
                    string.Join(" ", [
                        "File failed health validation.",
                        $"Corresponding {linkType} found within Library Dir.",
                        "No configured Radarr/Sonarr instance confirmed a matching media-item.",
                        $"Leaving the webdav-file and {linkType} in place after no-match confirmation {noMatchConfirmations}/{NoMatchConfirmationsRequired}."
                    ]), ct).ConfigureAwait(false);
                return;
            }

            _arrNoMatchConfirmations.TryRemove(davItem.Id, out _);
            await Task.Run(() => File.Delete(linkedPath)).ConfigureAwait(false);
            DeletionAuditLog.Record(
                "health-repair",
                davItem,
                "health validation failed; no Arr media-item after repeated reachable confirmations");
            dbClient.Ctx.Items.Remove(davItem);
            _failureTracker.ClearFailure(davItem.Id);
            var confirmedOrphanUtcNow = DateTimeOffset.UtcNow;
            davItem.LastHealthCheck = confirmedOrphanUtcNow;
            davItem.NextHealthCheck = confirmedOrphanUtcNow + TimeSpan.FromDays(1);
            await RecordHealthResult(
                dbClient, davItem,
                HealthCheckResult.HealthResult.Unhealthy,
                HealthCheckResult.RepairAction.Deleted,
                string.Join(" ", [
                    "File failed health validation.",
                    $"Corresponding {linkType} found within Library Dir.",
                    "No configured Radarr/Sonarr instance confirmed a matching media-item.",
                    $"Deleted the webdav-file and {linkType} after {noMatchConfirmations} consecutive confirmations."
                ]), ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // if an error is encountered during repairs,
            // then mark the item as unhealthy, and check again in a day.
            var utcNow = DateTimeOffset.UtcNow;
            davItem.LastHealthCheck = utcNow;
            davItem.NextHealthCheck = utcNow + TimeSpan.FromDays(1);
            await RecordHealthResult(
                dbClient, davItem,
                HealthCheckResult.HealthResult.Unhealthy,
                HealthCheckResult.RepairAction.ActionNeeded,
                $"Error performing file repair: {e.Message}", ct).ConfigureAwait(false);
        }
    }


    private async Task RecordHealthResult
    (
        DavDatabaseClient dbClient,
        DavItem davItem,
        HealthCheckResult.HealthResult result,
        HealthCheckResult.RepairAction repairStatus,
        string message,
        CancellationToken ct
    )
    {
        var identity = repairStatus is HealthCheckResult.RepairAction.Deleted or HealthCheckResult.RepairAction.Repaired
            ? await GetRepairHistoryIdentityAsync(dbClient.Ctx, davItem, ct).ConfigureAwait(false)
            : null;
        dbClient.Ctx.HealthCheckResults.Add(SendStatus(new HealthCheckResult()
        {
            Id = Guid.NewGuid(),
            DavItemId = davItem.Id,
            Path = davItem.Path,
            NzbFileName = identity?.NzbFileName,
            JobName = identity?.JobName,
            CreatedAt = DateTimeOffset.UtcNow,
            Result = result,
            RepairStatus = repairStatus,
            Message = message
        }));
        try
        {
            await dbClient.Ctx.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException e)
        {
            Log.Warning(
                "Health check result not recorded because the file was deleted while the check was running. Path: {Path}",
                davItem.Path);
            Log.Debug(e, "Health check concurrency stack for {Path}", davItem.Path);
            foreach (var entry in e.Entries)
                entry.State = EntityState.Detached;
        }
    }

    internal static async Task<RepairHistoryIdentity?> GetRepairHistoryIdentityAsync
    (
        DavDatabaseContext context,
        DavItem davItem,
        CancellationToken ct
    )
    {
        if (davItem.NzbBlobId is not Guid nzbBlobId) return null;

        var nzbFileName = await context.NzbNames
            .AsNoTracking()
            .Where(x => x.Id == nzbBlobId)
            .Select(x => x.FileName)
            .SingleOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (nzbFileName is null) return null;

        return new RepairHistoryIdentity(nzbFileName, FilenameUtil.GetJobName(nzbFileName));
    }

    internal sealed record RepairHistoryIdentity(string NzbFileName, string JobName);

    private HealthCheckResult SendStatus(HealthCheckResult result)
    {
        _ = _websocketManager.SendMessage
        (
            WebsocketTopic.HealthItemStatus,
            $"{result.DavItemId}|{(int)result.Result}|{(int)result.RepairStatus}"
        );
        return result;
    }

    /// <summary>
    /// Seeds the fail-fast cache with the segment ids of a release that repair just rejected
    /// via remove-and-blocklist. The cache semantically holds "segments of rejected releases"
    /// here: a corrupt-archive or truncated-stream rejection can have every article present,
    /// but a re-grab of the same release carries identical message-ids, so failing it at the
    /// queue step-0 precheck — pre-import, where Arr blocklisting works — is the intended
    /// outcome regardless of which failure type triggered the repair (issue #732).
    /// </summary>
    private async Task SeedRejectedReleaseSegmentsAsync(
        DavItem davItem,
        DavDatabaseClient dbClient,
        CancellationToken ct)
    {
        try
        {
            var segments = await GetAllSegments(davItem, dbClient, ct).ConfigureAwait(false);
            AddMissingSegmentIds(SelectRejectedReleaseSeedSegments(segments));
        }
        catch (Exception e) when (!e.IsCancellationException(ct) && e is not OutOfMemoryException)
        {
            // A missing blob must not abort the repair; the release is already
            // blocklisted in Arr, we only lose the pre-import fail-fast.
            Log.Warning(
                "Could not seed the fail-fast cache with segments of rejected release {Path}. " +
                "A re-grab of the same release may import once more before failing. Reason: {Reason}",
                davItem.Path, e.Message);
            Log.Debug(e, "Rejected-release cache seeding failure stack for {Path}", davItem.Path);
        }
    }

    /// <summary>
    /// Selects the bounded prefix of a rejected release's segment ids to seed into the cache.
    /// </summary>
    internal static List<string> SelectRejectedReleaseSeedSegments(List<string> segments) =>
        segments.Count <= RejectedReleaseSeedSegments
            ? segments
            : segments.Take(RejectedReleaseSeedSegments).ToList();

    public static void AddMissingSegmentIds(IEnumerable<string> segmentIds)
    {
        lock (_missingSegmentIds)
        {
            foreach (var segmentId in segmentIds)
            {
                if (_missingSegmentIds.Add(segmentId))
                    _missingSegmentOrder.Enqueue(segmentId);
                while (_missingSegmentIds.Count > MaximumMissingSegmentIds)
                    _missingSegmentIds.Remove(_missingSegmentOrder.Dequeue());
            }
        }
    }

    public static void CheckCachedMissingSegmentIds(IEnumerable<string> segmentIds)
    {
        lock (_missingSegmentIds)
        {
            foreach (var segmentId in segmentIds.Where(segmentId => _missingSegmentIds.Contains(segmentId)))
                throw new UsenetArticleNotFoundException(segmentId);
        }
    }
}
