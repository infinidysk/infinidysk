using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Api.SabControllers.GetHistory;
using NzbWebDAV.Clients.RadarrSonarr;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Contexts;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models.Nzb;
using NzbWebDAV.Queue.DeobfuscationSteps._1.FetchFirstSegment;
using NzbWebDAV.Queue.DeobfuscationSteps._2.GetPar2FileDescriptors;
using NzbWebDAV.Queue.DeobfuscationSteps._3.GetFileInfos;
using NzbWebDAV.Queue.FileAggregators;
using NzbWebDAV.Queue.FileProcessors;
using NzbWebDAV.Queue.NestedRarExpansion;
using NzbWebDAV.Queue.PostProcessors;
using NzbWebDAV.Queue.SiblingDonors;
using NzbWebDAV.Services;
using NzbWebDAV.Services.Metrics;
using NzbWebDAV.Utils;
using NzbWebDAV.Websocket;
using Serilog;

namespace NzbWebDAV.Queue;

public class QueueItemProcessor(
    QueueItem queueItem,
    Stream? queueNzbStream,
    DavDatabaseClient dbClient,
    INntpClient usenetClient,
    ConfigManager configManager,
    WebsocketManager websocketManager,
    ProviderUsageTracker providerUsageTracker,
    WatchdogLog watchdogLog,
    QueueItemSourceTracker sourceTracker,
    IProgress<int> progress,
    ConcurrentDictionary<Guid, int> retryAttempts,
    SemaphoreSlim? finalizeLock,
    CancellationToken ct,
    Action<string>? stageReporter = null
)
{
    private readonly Action<string> _stageReporter = stageReporter ?? (_ => { });
    public QueueItemProcessor(
        QueueItem queueItem,
        Stream? queueNzbStream,
        DavDatabaseClient dbClient,
        INntpClient usenetClient,
        ConfigManager configManager,
        WebsocketManager websocketManager,
        IProgress<int> progress,
        CancellationToken ct)
        : this(
            queueItem,
            queueNzbStream,
            dbClient,
            usenetClient,
            configManager,
            websocketManager,
            new ProviderUsageTracker(),
            new WatchdogLog(),
            new QueueItemSourceTracker(),
            progress,
            new ConcurrentDictionary<Guid, int>(),
            finalizeLock: null,
            ct)
    {
    }

    private const int MaxProviderRetryAttempts = 20;
    private static readonly TimeSpan StageWarningInterval = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Set by the queue's stuck watchdog when this worker's cancellation should
    /// fail the item into history (repeated stalls) rather than leave it queued
    /// for another retry. Checked in the cancellation catch in <see cref="ProcessAsync"/>.
    /// </summary>
    internal Func<bool>? ShouldFailOnCancel { get; set; }

    /// <summary>
    /// Called once the item reaches a terminal state (completed or failed into
    /// history) so the manager can drop its per-item watchdog stall counter. Not
    /// called for a plain cancellation that leaves the item queued for retry.
    /// </summary>
    internal Action? OnTerminal { get; set; }

    internal static List<string> SelectArticlesForExistenceCheck(
        IEnumerable<IReadOnlyList<string>> segmentsByFile,
        string mode)
    {
        var files = segmentsByFile.ToList();
        return mode == "sampled"
            ? files.SelectMany(segments =>
                HealthCheckService.SampleSegments(segments.ToList())).ToList()
            : files.SelectMany(segments => segments).ToList();
    }

    private static TimeSpan GetProviderRetryBackoff(int attempt)
    {
        var seconds = Math.Min(60d, 10d * Math.Pow(2, attempt - 1));
        return TimeSpan.FromSeconds(seconds);
    }

    public async Task ProcessAsync()
    {
        // initialize
        var startTime = DateTime.Now;
        Log.Information(
            "Processing queue item {JobName} ({QueueItemId}) in category {Category}",
            queueItem.JobName,
            queueItem.Id,
            queueItem.Category);
        _ = websocketManager.SendMessage(WebsocketTopic.QueueItemStatus, $"{queueItem.Id}|Downloading");

        using var providerScope = providerUsageTracker.BeginScope(queueItem.Id);

        // process the job
        try
        {
            await ProcessQueueItemAsync(startTime).ConfigureAwait(false);
        }

        // When a queue-item is removed while processing,
        // then we need to clear any db changes and finish early.
        catch (Exception e) when (e.GetBaseException().IsCancellationException() && e is not OutOfMemoryException)
        {
            // The stuck watchdog flags a repeatedly-stalled item so it fails into
            // history (letting Sonarr/Radarr blocklist and re-grab) instead of
            // pausing and retrying forever. An ordinary cancel — user pause/remove,
            // shutdown, or a non-final stall — keeps the item queued.
            if (ShouldFailOnCancel?.Invoke() == true)
            {
                Log.Warning(
                    "Failing queue item {JobName} ({QueueItemId}) into history after repeated stalls",
                    queueItem.JobName,
                    queueItem.Id);
                dbClient.Ctx.ClearChangeTracker();
                try
                {
                    // `ct` is already cancelled here; finalize with a dedicated
                    // timeout so a wedged finalize-lock holder cannot pin this
                    // worker forever (CancellationToken.None would also ignore
                    // shutdown). On timeout, leave the item queued — the stall
                    // counter persists, so the next attempt fails it again.
                    using var failCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                    await MarkQueueItemCompleted(
                            startTime,
                            error: "Download stalled: no progress across repeated attempts. " +
                                   "Failing so the download client can blocklist and re-grab.",
                            cancellationToken: failCts.Token)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex.IsCancellationException() && ex is not OutOfMemoryException)
                {
                    Log.Error(
                        "Timed out writing history for repeatedly-stalled queue item {JobName}; leaving it queued",
                        queueItem.JobName);
                }
                catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException)
                {
                    Log.Error(ex,
                        "Failed to mark repeatedly-stalled queue item {JobName} as failed",
                        queueItem.JobName);
                }
            }
            else
            {
                Log.Information("Processing of queue item {JobName} was cancelled", queueItem.JobName);
                dbClient.Ctx.ClearChangeTracker();
            }
        }

        catch (Exception e) when (e.IsRetryableDownloadException() && e is not OutOfMemoryException)
        {
            try
            {
                var attempt = retryAttempts.AddOrUpdate(queueItem.Id, 1, (_, prev) => prev + 1);
                e.TryGetKnownErrorMessage(out var reason);
                if (attempt > MaxProviderRetryAttempts)
                {
                    Log.Error(
                        "Giving up on queue item {JobName} after {Attempts} provider-connection failures. Reason: {Reason}",
                        queueItem.JobName,
                        attempt - 1,
                        reason);
                    Log.Debug(e, "Queue item give-up stack for {JobName}", queueItem.JobName);
                    await MarkQueueItemCompleted(startTime, error: e.Message).ConfigureAwait(false);
                    return;
                }

                var backoff = GetProviderRetryBackoff(attempt);
                Log.Warning(
                    "Provider connection issue for queue item {JobName} (attempt {Attempt}/{MaxAttempts}); " +
                    "retrying in {BackoffSeconds:0} seconds. Reason: {Reason}",
                    queueItem.JobName,
                    attempt,
                    MaxProviderRetryAttempts,
                    backoff.TotalSeconds,
                    reason);
                Log.Debug(e, "Queue item retry stack for {JobName}", queueItem.JobName);
                dbClient.Ctx.ClearChangeTracker();
                queueItem.PauseUntil = DateTime.Now + backoff;
                dbClient.Ctx.QueueItems.Attach(queueItem);
                dbClient.Ctx.Entry(queueItem).Property(x => x.PauseUntil).IsModified = true;
                await WithFinalizeLockAsync(() => dbClient.Ctx.SaveChangesAsync(ct))
                    .ConfigureAwait(false);
                _ = websocketManager.SendMessage(WebsocketTopic.QueueItemStatus, $"{queueItem.Id}|Queued");
            }
            catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException)
            {
                Log.Error(ex, "Failed to schedule retry for queue item {JobName}", queueItem.JobName);
            }
        }

        // when any other error is encountered,
        // we must still remove the queue-item and add
        // it to the history as a failed job.
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Remember definitively missing articles so retries of this item and re-grabs
            // of the same release fail in milliseconds at the step-0 precheck instead of
            // re-verifying every article across all providers (issue #732).
            if (e.TryGetCausingException<UsenetArticleNotFoundException>(out var articleNotFound) &&
                articleNotFound is not null)
            {
                HealthCheckService.AddMissingSegmentIds([articleNotFound.SegmentId]);
            }

            try
            {
                await MarkQueueItemCompleted(startTime, error: e.Message).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException)
            {
                Log.Error(
                    ex,
                    "Failed to mark queue item {JobName} as failed after processing error: {ProcessingError}",
                    queueItem.JobName,
                    e.Message);
            }
        }
    }

    private async Task<T> RunStageAsync<T>(string stage, Func<Task<T>> action)
    {
        _stageReporter(stage);
        using var stageCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var stageTimer = Stopwatch.StartNew();
        var monitorTask = MonitorLongRunningStageAsync(stage, stageTimer, stageCts.Token);
        try
        {
            return await action().ConfigureAwait(false);
        }
        finally
        {
            await stageCts.CancelAsync().ConfigureAwait(false);
            await monitorTask.ConfigureAwait(false);
        }
    }

#pragma warning disable CA1859 // non-generic facade over RunStageAsync<T>; returning Task<object?> would leak the wrapper's shape for no benefit
    private Task RunStageAsync(string stage, Func<Task> action)
#pragma warning restore CA1859
    {
        return RunStageAsync<object?>(stage, async () =>
        {
            await action().ConfigureAwait(false);
            return null;
        });
    }

    private async Task MonitorLongRunningStageAsync(string stage, Stopwatch stageTimer, CancellationToken stageCt)
    {
        try
        {
            while (true)
            {
                await Task.Delay(StageWarningInterval, stageCt).ConfigureAwait(false);
                var queueContext = ct.GetContext<QueueDownloadContext>();
                Log.Warning(
                    "Queue item {JobName} ({QueueItemId}) remains in {Stage} after {ElapsedSeconds:0}s; " +
                    "primary={IsPrimary} fanOut={FanOut} semaphoreWait={SemaphoreWait}ms",
                    queueItem.JobName,
                    queueItem.Id,
                    stage,
                    stageTimer.Elapsed.TotalSeconds,
                    queueContext?.IsPrimary ?? false,
                    queueContext?.GetFanOutConcurrency() ?? 1,
                    queueContext?.SemaphoreWaitMilliseconds ?? 0);
            }
        }
        catch (OperationCanceledException) when (stageCt.IsCancellationRequested)
        {
            // The stage completed or the worker was cancelled.
        }
    }

    private async Task ProcessQueueItemAsync(DateTime startTime)
    {
        // if the `/blobs` folder is tampered with outside the nzbdav process,
        // then it is possible that the nzb file goes missing.
        if (queueNzbStream is null)
            throw new InvalidOperationException($"The NZB file could not be found.");

        // load config for handling duplicate nzbs
        var existingMountFolder = await GetMountFolder().ConfigureAwait(false);
        var duplicateNzbBehavior = configManager.GetDuplicateNzbBehavior();

        // if the mount folder already exists and setting is `marked-failed`
        // then immediately mark the job as failed.
        var isDuplicateNzb = existingMountFolder is not null;
        if (isDuplicateNzb && duplicateNzbBehavior == "mark-failed")
        {
            const string error = "Duplicate nzb: the download folder for this nzb already exists.";
            await MarkQueueItemCompleted(startTime, error, () => Task.FromResult(existingMountFolder))
                .ConfigureAwait(false);
            return;
        }

        // read the nzb document
        var nzb = await NzbDocument.LoadAsync(queueNzbStream, ct).ConfigureAwait(false);
        var nzbFiles = nzb.Files.Where(x => x.Segments.Count > 0).ToList();
        if (usenetClient is ArticleCachingNntpClient cachingUsenetClient)
            cachingUsenetClient.TrackNzbFiles(nzbFiles);

        // Look for a password in filename and nzb document
        // The file name's password takes priority, as an easy override
        var archivePassword = FilenameUtil.GetNzbPassword(queueItem.FileName) ??
            nzb.Metadata.GetValueOrDefault("password");

        // step 0 -- perform article existence pre-check against cache
        // https://github.com/infinidysk/infinidysk/issues/101
        var articlesToPrecheck = nzbFiles.SelectMany(x => x.Segments).Select(x => x.MessageId);
        HealthCheckService.CheckCachedMissingSegmentIds(articlesToPrecheck);

        await RunStageAsync("sibling-donors",
            () => SiblingDonorAttacher.AttachToNewImportAsync(
                dbClient, queueItem, nzbFiles, configManager, ct)).ConfigureAwait(false);

        // step 1 -- get name and size of each nzb file
        var stepTimer = Stopwatch.StartNew();
        var part1Progress = progress
            .Scale(50, 100)
            .ToPercentage(nzbFiles.Count);
        var segments = await RunStageAsync(
            "first-segment",
            () => FetchFirstSegmentsStep.FetchFirstSegments(
                nzbFiles, usenetClient, configManager, ct, part1Progress)).ConfigureAwait(false);
        var msFirstSeg = stepTimer.ElapsedMilliseconds;
        stepTimer.Restart();
        // step 2 progress is split 50-55 (par2) / 55-60 (lazy-rar) / 60-100
        // (processors) so the watchdog sees movement before the first file
        // processor completes.
        IProgress<int> par2Progress = progress
            .Offset(50)
            .Scale(5, 100);
        var par2FileDescriptors = await RunStageAsync(
            "par2",
            () => GetPar2FileDescriptorsStep.GetPar2FileDescriptors(
                segments, usenetClient, par2Progress, ct)).ConfigureAwait(false);
        var msPar2 = stepTimer.ElapsedMilliseconds;
        stepTimer.Restart();
        var fileInfos = GetFileInfosStep.GetFileInfos(
            segments, par2FileDescriptors);

        // step 1b -- fail fast if any important file has a permanently missing first segment.
        // If the first segment is gone across all providers, the rest are too.
        // Exclude known-unimportant extensions rather than matching important ones so
        // obfuscated filenames (common on DMCA'd content) still trigger the fast abort.
        // (FetchFirstSegmentsStep also aborts mid-fetch on the first important miss.)
        var missingNzbFiles = segments
            .Where(x => x.MissingFirstSegment)
            .Select(x => x.NzbFile)
            .ToHashSet();
        var importantFilesMissing = fileInfos
            .Where(x => missingNzbFiles.Contains(x.NzbFile))
            .Where(x => DeadNzbFailFast.IsImportantFileName(x.FileName))
            .ToList();
        if (importantFilesMissing.Count > 0)
        {
            // Remember the missing first segments so retries of this item and re-grabs
            // of the same release fail in milliseconds via the step-0 precheck instead
            // of re-verifying every article across all providers.
            HealthCheckService.AddMissingSegmentIds(
                importantFilesMissing.Select(x => x.NzbFile.Segments[0].MessageId));

            var fileNames = string.Join(", ", importantFilesMissing
                .Select(x => string.IsNullOrEmpty(x.FileName) ? x.NzbFile.Subject : x.FileName)
                .Take(3));
            throw new NonRetryableDownloadException(
                $"Missing articles: {importantFilesMissing.Count} important file(s) have missing segments " +
                $"across all providers (e.g. {fileNames}). NZB is likely DMCA'd or expired.");
        }

        // step 2a -- try altmount-style lazy RAR mounting for the rar group
        // when enabled. On success, the entire rar group is handled here
        // (only the first volume gets parsed) and skipped in step 2b. On
        // ineligibility — multi-file, compressed, solid, or first-volume
        // parse failure — fall through to the per-part eager pipeline.
        LazyRarProcessor.Result? lazyRarResult = null;
        var rarFiles = fileInfos.Where(x => GetGroupName(x) == "rar").ToList();
        if (configManager.IsLazyRarParsingEnabled() && rarFiles.Count > 0)
        {
            var lazyProc = new LazyRarProcessor(rarFiles, usenetClient, archivePassword, ct);
            IProgress<int> lazyRarProgress = progress
                .Offset(55)
                .Scale(5, 100);
            lazyRarResult = await RunStageAsync("lazy-rar", async () =>
            {
                var result = await lazyProc.ProcessAsync().ConfigureAwait(false);
                lazyRarProgress.Report(100);
                return result;
            }).ConfigureAwait(false) as LazyRarProcessor.Result;
            // Nested archives need the full eager pass + NestedRarExpansionStep.
            if (lazyRarResult is not null &&
                FilenameUtil.IsRarFile(Path.GetFileName(lazyRarResult.PathInArchive)))
            {
                lazyRarResult = null;
            }
        }
        var msRar = stepTimer.ElapsedMilliseconds;
        stepTimer.Restart();

        // step 2b -- per-file processing for everything else (and for the
        // rar group when lazy mounting was skipped or unsupported).
        var skipRarGroup = lazyRarResult is not null;
        using var processorCts = ContextualCancellationTokenSource.CreateLinkedTokenSource(ct);
        var fileProcessors = GetFileProcessors(fileInfos, archivePassword, skipRarGroup, processorCts.Token).ToList();
        var part2Progress = progress
            .Offset(60)
            .Scale(40, 100)
            .ToMultiProgress(fileProcessors.Count);
        var fileProcessingResults = await RunStageAsync("processors", async () =>
        {
            var fileProcessingResultsAll = await fileProcessors
                .Select(x => RunProcessorWithRarSiblingAbortAsync(
                    x!, part2Progress.SubProgress, processorCts, ct))
                .WithConcurrencyAsync(QueueFanOut.GetConcurrency(configManager, ct), ct)
                .GetAllAsync(ct: ct).ConfigureAwait(false);
            var results = fileProcessingResultsAll
                .Where(x => x is not null)
                .Select(x => x!)
                .ToList();
            if (lazyRarResult is not null) results.Add(lazyRarResult);
            return await NestedRarExpansionStep.ExpandAsync(
                results, usenetClient, archivePassword, ct).ConfigureAwait(false);
        }).ConfigureAwait(false);
        var msProcessors = stepTimer.ElapsedMilliseconds;
        stepTimer.Restart();

        // step 3 -- Optionally check full article existence
        var checkedFullHealth = false;
        var healthCheckCategories = configManager.GetEnsureArticleExistenceCategories();
        if (healthCheckCategories.Contains(queueItem.Category.ToLowerInvariant()))
        {
            var segmentsByFile = fileInfos
                .Where(x => x.IsRar || FilenameUtil.IsImportantFileType(x.FileName))
                .Select(x => (IReadOnlyList<string>)x.NzbFile.GetSegmentIds().ToList())
                .ToList();
            var totalArticles = segmentsByFile.Sum(x => x.Count);
            var checkMode = configManager.GetArticleExistenceCheckMode();
            var articlesToCheck = SelectArticlesForExistenceCheck(segmentsByFile, checkMode);
            if (checkMode == "sampled")
            {
                Log.Information(
                    "Article existence check sampled {SampledCount}/{TotalCount} segments across {FileCount} files " +
                    "for queue item {QueueItemId}.",
                    articlesToCheck.Count, totalArticles, segmentsByFile.Count, queueItem.Id);
            }

            var part3Progress = progress
                .Offset(100)
                .ToPercentage(articlesToCheck.Count);
            var healthCheckConcurrency = Math.Min(
                configManager.GetHealthCheckConcurrency(),
                QueueFanOut.GetConcurrency(configManager, ct));
            await RunStageAsync(
                    "health",
                    () => ArticleExistenceChecker
                        .CheckAsync(usenetClient, articlesToCheck, healthCheckConcurrency, part3Progress, ct))
                .ConfigureAwait(false);
            checkedFullHealth = true;
        }
        var msHealth = stepTimer.ElapsedMilliseconds;
        stepTimer.Stop();

        Log.Information(
            "play-timing nzo={NzoId} files={Files} firstSeg={FirstSeg}ms par2={Par2}ms rar={Rar}ms " +
            "processors={Processors}ms health={Health}ms semWait={SemaphoreWait}ms",
            queueItem.Id, nzbFiles.Count, msFirstSeg, msPar2, msRar, msProcessors, msHealth,
            ct.GetContext<QueueDownloadContext>()?.SemaphoreWaitMilliseconds ?? 0);

        // update the database
        await MarkQueueItemCompleted(startTime, error: null, async () =>
        {
            var categoryFolder = await GetOrCreateCategoryFolder().ConfigureAwait(false);
            var mountFolder = await CreateMountFolder(categoryFolder, existingMountFolder, duplicateNzbBehavior)
                .ConfigureAwait(false);
            new RarAggregator(dbClient, mountFolder, checkedFullHealth).UpdateDatabase(fileProcessingResults);
            new FileAggregator(dbClient, mountFolder, checkedFullHealth).UpdateDatabase(fileProcessingResults);
            new SevenZipAggregator(dbClient, mountFolder, checkedFullHealth).UpdateDatabase(fileProcessingResults);
            new MultipartMkvAggregator(dbClient, mountFolder, checkedFullHealth).UpdateDatabase(fileProcessingResults);

            // post-processing
            new RenameDuplicatesPostProcessor(dbClient).RenameDuplicates();
            new BlocklistedFilePostProcessor(configManager, dbClient).RemoveFilteredFiles();

            // validate media files found
            if (configManager.IsEnsureImportableMediaEnabled())
                new EnsureImportableMediaValidator(dbClient).ThrowIfValidationFails();

            // create strm files, if necessary
            if (configManager.GetImportStrategy() == "strm")
                await new CreateStrmFilesPostProcessor(configManager, dbClient, queueItem.Id)
                    .CreateStrmFilesAsync()
                    .ConfigureAwait(false);

            await SiblingDonorAttacher.BackfillCompletedSiblingsAsync(
                dbClient, queueItem, nzb, configManager, ct).ConfigureAwait(false);

            return mountFolder;
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs one file processor. A RAR header timeout/transient failure cancels the
    /// linked stage token so sibling volume scans abort instead of grinding, then
    /// rethrows as <see cref="RetryableDownloadException"/> (without double-wrapping).
    /// Sibling cancellation from that abort is swallowed so <see cref="WithConcurrencyAsync"/>
    /// keeps the first retryable failure authoritative instead of racing to OCE.
    /// </summary>
    internal static async Task<BaseProcessor.Result?> RunProcessorWithRarSiblingAbortAsync(
        BaseProcessor processor,
        IProgress<int> progress,
        ContextualCancellationTokenSource processorCts,
        CancellationToken workerToken)
    {
        try
        {
            return await processor.ProcessAsync(progress).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            processor is RarProcessor &&
            !workerToken.IsCancellationRequested &&
            exception is not OutOfMemoryException &&
            (exception.IsRetryableDownloadException() || exception.IsTransientTransportException()))
        {
            await processorCts.CancelAsync().ConfigureAwait(false);

            if (exception.IsRetryableDownloadException())
                throw;

            throw new RetryableDownloadException(
                "Transient provider failure while reading RAR volume headers.",
                exception);
        }
        catch (Exception exception) when (
            processor is RarProcessor &&
            exception.IsCancellationException(processorCts.Token) &&
            exception is not OutOfMemoryException &&
            !workerToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private IEnumerable<BaseProcessor> GetFileProcessors
    (
        List<GetFileInfosStep.FileInfo> fileInfos,
        string? archivePassword,
        bool skipRarGroup,
        CancellationToken rarProcessorCt
    )
    {
        var groups = GroupFilesForProcessing(fileInfos);

        foreach (var group in groups)
        {
            if (group.Key == "7z")
                yield return new SevenZipProcessor(group.ToList(), usenetClient, configManager, archivePassword, ct);

            else if (group.Key == "rar")
            {
                if (skipRarGroup) continue;
                foreach (var fileInfo in group)
                    yield return new RarProcessor(fileInfo, usenetClient, archivePassword, rarProcessorCt);
            }

            else if (group.Key.StartsWith("split-video:", StringComparison.Ordinal))
                yield return new MultipartMkvProcessor(group.ToList(), usenetClient, ct);

            else if (group.Key == "other")
                foreach (var fileInfo in group)
                    yield return new FileProcessor(fileInfo, usenetClient, configManager, ct);
        }
    }

    internal static string GetGroupName(GetFileInfosStep.FileInfo x) =>
        FilenameUtil.Is7zFile(x.FileName) ? "7z"
        : x.IsRar || FilenameUtil.IsRarFile(x.FileName) ? "rar"
        : FilenameUtil.GetSplitVideoBaseName(x.FileName) is { } baseName
            ? $"split-video:{baseName.ToLowerInvariant()}"
        : "other";

    internal static List<IGrouping<string, GetFileInfosStep.FileInfo>> GroupFilesForProcessing(
        IReadOnlyList<GetFileInfosStep.FileInfo> fileInfos)
    {
        return MaybeMergeSplitVideoGroups(fileInfos.GroupBy(GetGroupName).ToList());
    }

    /// <summary>
    /// When multiple split-video groups have globally disjoint part numbers that
    /// form one contiguous sequence starting at 1, treat them as one inconsistently
    /// named set (PAR2 vs subject vs yEnc header disagreement). Season packs always
    /// collide on part numbers because each splitter restarts at .001.
    /// </summary>
    internal static List<IGrouping<string, GetFileInfosStep.FileInfo>> MaybeMergeSplitVideoGroups(
        List<IGrouping<string, GetFileInfosStep.FileInfo>> groups)
    {
        var splitGroups = groups
            .Where(g => g.Key.StartsWith("split-video:", StringComparison.Ordinal))
            .ToList();
        if (splitGroups.Count < 2)
            return groups;

        var allParts = splitGroups.SelectMany(g => g).ToList();
        var parsedPartNumbers = allParts
            .Select(part => FilenameUtil.GetSplitVideoPartNumber(part.FileName))
            .ToList();
        if (parsedPartNumbers.Any(n => n is null))
            return groups;

        var partNumbers = parsedPartNumbers.Select(n => n!.Value).ToList();
        if (partNumbers.Distinct().Count() != partNumbers.Count)
            return groups;

        var sorted = partNumbers.OrderBy(n => n).ToList();
        if (sorted[0] != 1)
            return groups;
        for (var i = 0; i < sorted.Count; i++)
        {
            if (sorted[i] != i + 1)
                return groups;
        }

        Log.Information(
            "Merging {GroupCount} split-video groups with disjoint contiguous part numbers into one set ({FileCount} parts)",
            splitGroups.Count,
            allParts.Count);

        var mergedKey = splitGroups[0].Key;
        var merged = allParts.GroupBy(_ => mergedKey).Single();
        var result = new List<IGrouping<string, GetFileInfosStep.FileInfo>>(
            groups.Count - splitGroups.Count + 1);
        var mergedInserted = false;
        foreach (var group in groups)
        {
            if (group.Key.StartsWith("split-video:", StringComparison.Ordinal))
            {
                if (!mergedInserted)
                {
                    result.Add(merged);
                    mergedInserted = true;
                }
                continue;
            }
            result.Add(group);
        }

        return result;
    }

    private async Task<DavItem?> GetMountFolder()
    {
        var query = from mountFolder in dbClient.Ctx.Items
                    join categoryFolder in dbClient.Ctx.Items on mountFolder.ParentId equals categoryFolder.Id
                    where mountFolder.Name == queueItem.JobName
                          && mountFolder.ParentId != null
                          && categoryFolder.Name == queueItem.Category
                          && categoryFolder.ParentId == DavItem.ContentFolder.Id
                    select mountFolder;

        return await query.FirstOrDefaultAsync(ct).ConfigureAwait(false);
    }

    private async Task<DavItem> GetOrCreateCategoryFolder()
    {
        // if the category item already exists, return it
        var categoryFolder = await dbClient.GetDirectoryChildAsync(
            DavItem.ContentFolder.Id, queueItem.Category, ct).ConfigureAwait(false);
        if (categoryFolder is not null)
            return categoryFolder;

        // otherwise, create it
        categoryFolder = DavItem.New(
            id: Guid.NewGuid(),
            parent: DavItem.ContentFolder,
            name: queueItem.Category,
            fileSize: null,
            type: DavItem.ItemType.Directory,
            subType: DavItem.ItemSubType.Directory,
            releaseDate: null,
            lastHealthCheck: null,
            historyItemId: null,
            fileBlobId: null
        );
        dbClient.Ctx.Items.Add(categoryFolder);
        return categoryFolder;
    }

    private Task<DavItem> CreateMountFolder
    (
        DavItem categoryFolder,
        DavItem? existingMountFolder,
        string duplicateNzbBehavior
    )
    {
        if (existingMountFolder is not null && duplicateNzbBehavior == "increment")
            return IncrementMountFolder(categoryFolder);

        var mountFolder = DavItem.New(
            id: Guid.NewGuid(),
            parent: categoryFolder,
            name: queueItem.JobName,
            fileSize: null,
            type: DavItem.ItemType.Directory,
            subType: DavItem.ItemSubType.Directory,
            releaseDate: null,
            lastHealthCheck: null,
            historyItemId: queueItem.Id,
            fileBlobId: null
        );
        dbClient.Ctx.Items.Add(mountFolder);
        return Task.FromResult(mountFolder);
    }

    private async Task<DavItem> IncrementMountFolder(DavItem categoryFolder)
    {
        for (var i = 2; i < 100; i++)
        {
            var name = $"{queueItem.JobName} ({i})";
            var existingMountFolder =
                await dbClient.GetDirectoryChildAsync(categoryFolder.Id, name, ct).ConfigureAwait(false);
            if (existingMountFolder is not null) continue;

            var mountFolder = DavItem.New(
                id: Guid.NewGuid(),
                parent: categoryFolder,
                name: name,
                fileSize: null,
                type: DavItem.ItemType.Directory,
                subType: DavItem.ItemSubType.Directory,
                releaseDate: null,
                lastHealthCheck: null,
                historyItemId: queueItem.Id,
                fileBlobId: null
            );
            dbClient.Ctx.Items.Add(mountFolder);
            return mountFolder;
        }

        throw new InvalidOperationException("Duplicate nzb with more than 100 existing copies.");
    }

    private HistoryItem CreateHistoryItem(DavItem? mountFolder, DateTime jobStartTime, string? errorMessage = null)
    {
        return new HistoryItem()
        {
            Id = queueItem.Id,
            CreatedAt = DateTime.Now,
            FileName = queueItem.FileName,
            JobName = queueItem.JobName,
            Category = queueItem.Category,
            DownloadStatus = errorMessage == null
                ? HistoryItem.DownloadStatusOption.Completed
                : HistoryItem.DownloadStatusOption.Failed,
            TotalSegmentBytes = queueItem.TotalSegmentBytes,
            DownloadTimeSeconds = (int)(DateTime.Now - jobStartTime).TotalSeconds,
            FailMessage = errorMessage,
            DownloadDirId = mountFolder?.Id,
            NzbBlobId = queueItem.Id,
            IndexerName = queueItem.IndexerName,
            ContentGroupKey = queueItem.ContentGroupKey,
        };
    }

    private async Task MarkQueueItemCompleted
    (
        DateTime startTime,
        string? error = null,
        Func<Task<DavItem?>>? databaseOperations = null,
        CancellationToken? cancellationToken = null
    )
    {
        // The finalize token defaults to the worker token. The stuck-watchdog
        // failure path must pass a timeout-bounded token: the worker CT is
        // already cancelled, and CancellationToken.None would ignore shutdown
        // and hang forever on a wedged finalize lock.
        var finalizeCt = cancellationToken ?? ct;
        HistoryItem? historyItem = null;
        GetHistoryResponse.HistorySlot? historySlot = null;
        IReadOnlyDictionary<string, long>? providerUsage = null;

        await WithFinalizeLockAsync(async () =>
        {
            dbClient.Ctx.ClearChangeTracker();
            var mountFolder = databaseOperations != null
                ? await databaseOperations.Invoke().ConfigureAwait(false)
                : null;
            historyItem = CreateHistoryItem(mountFolder, startTime, error);
            providerUsage = providerUsageTracker.Snapshot(queueItem.Id);
            var displayByMetricsKey = ProviderUsageHelper
                .BuildDisplayByMetricsKey(configManager.GetUsenetProviderConfig().Providers);
            historySlot = GetHistoryResponse.HistorySlot.FromHistoryItem(
                historyItem, mountFolder, configManager, providerUsage, displayByMetricsKey);
            dbClient.Ctx.QueueItems.Remove(queueItem);
            dbClient.Ctx.HistoryItems.Add(historyItem);
            await dbClient.Ctx.SaveChangesAsync(finalizeCt).ConfigureAwait(false);
        }, finalizeCt).ConfigureAwait(false);

        try
        {
            _ = websocketManager.SendMessage(WebsocketTopic.QueueItemRemoved, queueItem.Id.ToString());
            _ = websocketManager.SendMessage(WebsocketTopic.HistoryItemAdded, historySlot!.ToJson());
            _ = DavDatabaseContext.RcloneVfsForget(["/nzbs"]);
            _ = RefreshMonitoredDownloads();
            if (error is null)
            {
                Log.Information(
                    "Completed queue item {JobName} ({QueueItemId}) in {ElapsedSeconds} seconds",
                    queueItem.JobName,
                    queueItem.Id,
                    historyItem!.DownloadTimeSeconds);
            }
            else
            {
                Log.Error(
                    "Failed queue item {JobName} ({QueueItemId}) after {ElapsedSeconds} seconds: {Reason}",
                    queueItem.JobName,
                    queueItem.Id,
                    historyItem!.DownloadTimeSeconds,
                    error);
            }

            RecordWatchdogAttemptIfExternal(startTime, error, providerUsage!);
        }
        finally
        {
            // The item is already in history; drop retry/stall counters even if
            // post-finalize logging or watchdog recording throws.
            retryAttempts.TryRemove(queueItem.Id, out _);
            OnTerminal?.Invoke();
        }
    }

    private async Task WithFinalizeLockAsync(Func<Task> action, CancellationToken? cancellationToken = null)
    {
        if (finalizeLock is null)
        {
            await action().ConfigureAwait(false);
            return;
        }

        var waitCt = cancellationToken ?? ct;
        await finalizeLock.WaitAsync(waitCt).ConfigureAwait(false);
        try
        {
            await action().ConfigureAwait(false);
        }
        finally
        {
            finalizeLock.Release();
        }
    }

    // Emits a Watchdog attempt entry for queue items that didn't come through
    // ProfilePlayController (which writes its own attempts already). Lets users
    // see third-party SAB-compatible client / Sonarr enqueues with provider attribution
    // on the /watchdog page.
    private void RecordWatchdogAttemptIfExternal(
        DateTime startTime,
        string? error,
        IReadOnlyDictionary<string, long> providerUsage)
    {
        if (sourceTracker.ConsumeIsProfileFlow(queueItem.Id)) return;
        if (!configManager.IsPlaybackWatchdogEnabled()) return;

        var attemptedAt = new DateTimeOffset(startTime.ToUniversalTime(), TimeSpan.Zero);
        var durationMs = (int)Math.Max(0, (DateTime.Now - startTime).TotalMilliseconds);
        var outcome = error == null
            ? WatchdogEntry.Outcome.QueueCompleted
            : WatchdogEntry.Outcome.QueueFailed;
        var providerHost = FormatProviders(providerUsage, configManager);

        watchdogLog.Record(new WatchdogEntry
        {
            ClickId = queueItem.Id,
            AttemptedAt = attemptedAt,
            ContentType = string.IsNullOrEmpty(queueItem.Category) ? "unknown" : queueItem.Category,
            RequestedTitle = queueItem.JobName ?? queueItem.FileName,
            CandidateTitle = queueItem.JobName ?? queueItem.FileName,
            IndexerName = queueItem.IndexerName ?? "—",
            Size = queueItem.TotalSegmentBytes,
            RankIndex = 0,
            Result = outcome,
            FailReason = error,
            DurationMs = durationMs,
            IsWinner = error == null,
            ProviderHost = providerHost,
            QueueItemId = queueItem.Id,
            ContentGroupKey = queueItem.ContentGroupKey,
        });
    }

    private static string? FormatProviders(
        IReadOnlyDictionary<string, long> usage,
        ConfigManager configManager)
    {
        if (usage.Count == 0) return null;
        var labels = ProviderUsageHelper.BuildLabelsByMetricsKey(
            configManager.GetUsenetProviderConfig().Providers);
        string Label(string key) =>
            labels.TryGetValue(key, out var label) && !string.IsNullOrEmpty(label) ? label! : key;

        var total = usage.Values.Sum();
        if (total == 0) return string.Join(", ", usage.Keys.Select(Label));
        return string.Join(", ", usage
            .OrderByDescending(kv => kv.Value)
            .Select(kv => $"{Label(kv.Key)} ({(int)Math.Round(100.0 * kv.Value / total)}%)"));
    }

    private async Task RefreshMonitoredDownloads()
    {
        var tasks = configManager
            .GetArrConfig()
            .GetArrClients()
            .Select(RefreshMonitoredDownloads);
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task RefreshMonitoredDownloads(ArrClient arrClient)
    {
        try
        {
            var downloadClients = await arrClient.GetDownloadClientsAsync().ConfigureAwait(false);
            if (downloadClients.All(x => x.Category != queueItem.Category)) return;
            var queueCount = await arrClient.GetQueueCountAsync().ConfigureAwait(false);
            if (queueCount < 300) await arrClient.RefreshMonitoredDownloads().ConfigureAwait(false);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            Log.Debug(e, "Could not refresh monitored downloads for Arr instance {Host}", arrClient.Host);
        }
    }
}
