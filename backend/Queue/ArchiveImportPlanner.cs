using System.Diagnostics;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Contexts;
using NzbWebDAV.Config;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models.Nzb;
using NzbWebDAV.Queue.DeobfuscationSteps._1.FetchFirstSegment;
using NzbWebDAV.Queue.DeobfuscationSteps._2.GetPar2FileDescriptors;
using NzbWebDAV.Queue.DeobfuscationSteps._3.GetFileInfos;
using NzbWebDAV.Queue.FileProcessors;
using NzbWebDAV.Queue.NestedRarExpansion;
using NzbWebDAV.Services;
using NzbWebDAV.Utils;
using Serilog;

namespace NzbWebDAV.Queue;

/// <summary>
/// Runs a stage of <see cref="ArchiveImportPlanner.PlanAsync"/> under the caller's
/// stage reporting and long-running-stage monitoring.
/// </summary>
internal interface IArchiveImportStageRunner
{
    Task<T> RunStageAsync<T>(string stage, Func<Task<T>> action);
}

/// <summary>
/// In-memory result of planning an NZB import: the resolved file infos and the
/// processor results (after nested RAR expansion) that the completion step
/// aggregates, plus per-stage timings for the play-timing log.
/// </summary>
internal sealed record ArchiveImportPlan(
    List<GetFileInfosStep.FileInfo> FileInfos,
    List<BaseProcessor.Result> FileProcessingResults,
    long FirstSegmentMilliseconds,
    long Par2Milliseconds,
    long RarMilliseconds,
    long ProcessorsMilliseconds);

/// <summary>
/// Plans a queue import from an NZB's files: first segments, PAR2 descriptors,
/// file infos, lazy RAR mounting, per-file processors and nested RAR expansion.
/// Planning reads from Usenet but never writes the database, the WebDAV tree,
/// STRM files or *Arr clients; persistence stays with the caller's completion step.
/// </summary>
internal sealed class ArchiveImportPlanner(INntpClient usenetClient, ConfigManager configManager)
{
    public async Task<ArchiveImportPlan> PlanAsync(
        List<NzbFile> nzbFiles,
        string? archivePassword,
        Guid queueItemId,
        IProgress<int> progress,
        IArchiveImportStageRunner stageRunner,
        CancellationToken ct)
    {
        // step 1 -- get name and size of each nzb file
        var stepTimer = Stopwatch.StartNew();
        var part1Progress = progress
            .Scale(50, 100)
            .ToPercentage(nzbFiles.Count);
        var segments = await stageRunner.RunStageAsync(
            "first-segment",
            () => FetchFirstSegmentsStep.FetchFirstSegments(
                nzbFiles, usenetClient, configManager, ct, part1Progress)).ConfigureAwait(false);
        foreach (var (file, header) in segments
            .Where(segment => !segment.MissingFirstSegment && segment.Header is not null)
            .Select(segment => (segment.NzbFile, segment.Header!)))
        {
            var listedCount = file.Segments.Count;
            if (!await file.TryFillOmittedSegmentsAsync(header, usenetClient, ct).ConfigureAwait(false))
                continue;
            Log.Warning(
                "NZB file contains {OmittedCount} omitted numbered part(s) confirmed by yEnc metadata; " +
                "preserving the missing slots for playback and repair. QueueItemId: {QueueItemId}",
                file.Segments.Count - listedCount, queueItemId);
        }
        var msFirstSeg = stepTimer.ElapsedMilliseconds;
        stepTimer.Restart();
        // step 2 progress is split 50-55 (par2) / 55-60 (lazy-rar) / 60-100
        // (processors) so the watchdog sees movement before the first file
        // processor completes.
        IProgress<int> par2Progress = progress
            .Offset(50)
            .Scale(5, 100);
        var par2FileDescriptors = await stageRunner.RunStageAsync(
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
                importantFilesMissing.Select(x => x.NzbFile.Segments[0].MessageId),
                segments.Where(x => missingNzbFiles.Contains(x.NzbFile))
                    .Select(x => x.ProviderGeneration)
                    .FirstOrDefault(x => x.HasValue)
                ?? configManager.GetUsenetProviderSnapshot().Generation);

            var fileNames = string.Join(", ", importantFilesMissing
                .Select(x => string.IsNullOrEmpty(x.FileName) ? x.NzbFile.Subject : x.FileName)
                .Take(3));
            throw new NonRetryableDownloadException(
                $"Missing articles: {importantFilesMissing.Count} important file(s) have missing segments " +
                $"across all providers (e.g. {fileNames}). NZB is likely DMCA'd or expired.");
        }

        // step 2a -- try altmount-style lazy RAR mounting for the rar group
        // when enabled. On success, the first volume is parsed and cached
        // continuation-header prefixes are validated before the rar group is
        // skipped in step 2b. On ineligibility — multi-file, compressed,
        // solid, or header-parse failure — fall through to the eager pipeline.
        var archiveSetAllocator = new ArchiveSetIdAllocator();
        var archiveSets = ArchiveSetGrouping.Resolve(fileInfos, archiveSetAllocator);
        var lazyRarResults = new List<LazyRarProcessor.Result>();
        var lazyRarSetIds = new HashSet<string>(StringComparer.Ordinal);
        var rarSets = archiveSets
            .Where(x => !x.IsSevenZip)
            .Where(x => x.FileInfos.All(fileInfo => FilenameUtil.GetRarVolumeName(fileInfo.FileName) is not null))
            .ToList();
        if (configManager.IsLazyRarParsingEnabled() && rarSets.Count > 0)
        {
            IProgress<int> lazyRarProgress = progress
                .Offset(55)
                .Scale(5, 100);
            await stageRunner.RunStageAsync("lazy-rar", async () =>
            {
                foreach (var archiveSet in rarSets)
                {
                    ct.ThrowIfCancellationRequested();
                    var result = await new LazyRarProcessor(
                        archiveSet.FileInfos,
                        usenetClient,
                        archivePassword,
                        archiveSet.ArchiveSetId,
                        ct).ProcessAsync().ConfigureAwait(false) as LazyRarProcessor.Result;
                    if (result is not null &&
                        !FilenameUtil.IsRarFile(Path.GetFileName(result.PathInArchive)))
                    {
                        lazyRarResults.Add(result);
                        lazyRarSetIds.Add(archiveSet.ArchiveSetId);
                    }
                }

                lazyRarProgress.Report(100);
                return (BaseProcessor.Result?)null;
            }).ConfigureAwait(false);
        }
        var msRar = stepTimer.ElapsedMilliseconds;
        stepTimer.Restart();

        // step 2b -- per-file processing for everything else (and for the
        // rar group when lazy mounting was skipped or unsupported).
        using var processorCts = ContextualCancellationTokenSource.CreateLinkedTokenSource(ct);
        var fileProcessors = GetFileProcessors(
            fileInfos,
            archiveSets,
            lazyRarSetIds,
            archivePassword,
            processorCts.Token,
            ct).ToList();
        var part2Progress = progress
            .Offset(60)
            .Scale(40, 100)
            .ToMultiProgress(fileProcessors.Count);
        var fileProcessingResults = await stageRunner.RunStageAsync("processors", async () =>
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
            results.AddRange(lazyRarResults);
            return await NestedRarExpansionStep.ExpandAsync(
                results, usenetClient, archivePassword, ct).ConfigureAwait(false);
        }).ConfigureAwait(false);
        var msProcessors = stepTimer.ElapsedMilliseconds;

        return new ArchiveImportPlan(fileInfos, fileProcessingResults, msFirstSeg, msPar2, msRar, msProcessors);
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
        List<ArchiveSetDescriptor> archiveSets,
        HashSet<string> lazyRarSetIds,
        string? archivePassword,
        CancellationToken rarProcessorCt,
        CancellationToken ct
    )
    {
        foreach (var archiveSet in archiveSets)
        {
            if (archiveSet.IsSevenZip)
            {
                yield return new SevenZipProcessor(
                    archiveSet.FileInfos,
                    usenetClient,
                    configManager,
                    archivePassword,
                    archiveSet.ArchiveSetId,
                    ct);
            }
            else if (!lazyRarSetIds.Contains(archiveSet.ArchiveSetId))
            {
                foreach (var fileInfo in archiveSet.FileInfos)
                    yield return new RarProcessor(
                        fileInfo,
                        usenetClient,
                        archivePassword,
                        archiveSet.ArchiveSetId,
                        rarProcessorCt);
            }
        }

        var groups = GroupFilesForProcessing(
            fileInfos.Where(x => !x.IsRar && !FilenameUtil.IsRarFile(x.FileName) && !FilenameUtil.Is7zFile(x.FileName))
                .ToList());

        foreach (var group in groups)
        {
            if (group.Key.StartsWith("split-video:", StringComparison.Ordinal))
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
}
