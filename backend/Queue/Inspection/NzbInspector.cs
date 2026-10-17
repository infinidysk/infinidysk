using System.Globalization;
using System.Security.Cryptography;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Config;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models.Nzb;
using NzbWebDAV.Queue.DeobfuscationSteps._3.GetFileInfos;
using NzbWebDAV.Queue.FileAggregators;
using NzbWebDAV.Queue.FileProcessors;
using NzbWebDAV.Services;
using NzbWebDAV.Utils;
using Serilog;
using SharpCompressCryptographicException = SharpCompress.Common.CryptographicException;

namespace NzbWebDAV.Queue.Inspection;

/// <summary>
/// Reports what the queue import planner can establish about an NZB without importing it.
/// Inspection runs the same <see cref="ArchiveImportPlanner"/> as a queue import, so the
/// report reflects how an import would interpret the release. It fetches first segments,
/// PAR2 descriptors and archive headers through the normal provider machinery (bounded by
/// <see cref="NzbInspectionLimits"/>), so it causes real provider traffic; it never creates
/// queue or history rows, publishes WebDAV items, writes STRM or symlink files, contacts
/// *Arr apps or records misses in the shared missing-article caches that later imports and
/// playback consult. A report describes what could be established during that request,
/// not future availability.
/// </summary>
public sealed class NzbInspector(
    INntpClient usenetClient,
    ConfigManager configManager,
    NzbInspectionLimits? limits = null) : IDisposable
{
    private const string ArchiveHeaderNameSource = "archiveHeader";
    private const string DefaultMountName = "inspection";

    private readonly NzbInspectionLimits _limits = limits ?? NzbInspectionLimits.Default;
    private readonly SemaphoreSlim _admission = new(1, 1);

    public NzbInspectionLimits Limits => _limits;

    /// <summary>
    /// Admits one inspection at a time. Inspection draws on the same provider connections
    /// as playback and imports, so a request made while another inspection is running is
    /// refused immediately rather than queued. Returns <c>null</c> when the slot is taken;
    /// otherwise a lease that releases the slot when disposed.
    /// </summary>
    public IDisposable? TryBeginInspection() =>
        _admission.Wait(0) ? new AdmissionLease(_admission) : null;

    public void Dispose() => _admission.Dispose();

    /// <param name="nzbBytes">The NZB document; callers validate it against the NZB input limits first.</param>
    /// <param name="password">Archive password; falls back to a <c>{{password}}</c> release name and NZB metadata like an import.</param>
    /// <param name="releaseName">Release (job) name used for planned mount names.</param>
    public async Task<NzbInspectionReport> InspectAsync(
        byte[] nzbBytes,
        string? password,
        string? releaseName,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(nzbBytes);
        var stages = new InspectionStageRunner();

        NzbDocument nzb;
        await using (var stream = new MemoryStream(nzbBytes, writable: false))
            nzb = await NzbDocument.LoadAsync(stream, ct).ConfigureAwait(false);
        var nzbFiles = nzb.Files.Where(x => x.Segments.Count > 0).ToList();
        if (nzbFiles.Count > _limits.MaxNzbFiles)
        {
            return FailureReport(stages, "input", InspectionFailureKinds.LimitExceeded,
                string.Create(CultureInfo.InvariantCulture,
                    $"The NZB lists {nzbFiles.Count} files; inspection is limited to {_limits.MaxNzbFiles}."),
                sets: null, missingFirstSegments: [], warnings: [], articleRequests: 0);
        }

        var archivePassword = password
            ?? (releaseName is null ? null : FilenameUtil.GetNzbPassword(releaseName))
            ?? nzb.Metadata.GetValueOrDefault("password");
        var mountName = string.IsNullOrWhiteSpace(releaseName) ? DefaultMountName : releaseName;

        // Same read-only precheck an import performs first: articles already known to be
        // missing across all providers end the inspection before any provider traffic.
        try
        {
            HealthCheckService.CheckCachedMissingSegmentIds(
                nzbFiles.SelectMany(x => x.Segments).Select(x => x.MessageId),
                configManager.GetUsenetProviderSnapshot().Generation);
        }
        catch (UsenetArticleNotFoundException)
        {
            return FailureReport(stages, "precheck", InspectionFailureKinds.MissingArticles,
                "Articles in this NZB are already known to be missing across all providers.",
                sets: null, missingFirstSegments: [], warnings: [], articleRequests: 0);
        }

        var declaredBytes = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var segment in nzbFiles.SelectMany(x => x.Segments))
            declaredBytes.TryAdd(segment.MessageId, segment.Bytes);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_limits.Timeout);
        using var budget = new InspectionBudgetNntpClient(usenetClient, _limits, declaredBytes);
        using var client = new ArticleCachingNntpClient(budget);
        client.TrackNzbFiles(nzbFiles);

        ArchiveImportFileInfoSnapshot? snapshot = null;
        List<ArchiveSetDescriptor>? sets = null;
        var options = new ArchiveImportPlanOptions
        {
            RememberMissingArticles = false,
            OnFileInfosResolved = resolved =>
            {
                snapshot = resolved;
                sets = ArchiveSetGrouping.Resolve(resolved.FileInfos, new ArchiveSetIdAllocator());
                if (sets.Count > _limits.MaxArchiveSets)
                    throw new InspectionLimitExceededException("archive_sets");
            },
        };

        ArchiveImportPlan? plan = null;
        Exception? failure = null;
        try
        {
            plan = await new ArchiveImportPlanner(client, configManager)
                .PlanAsync(nzbFiles, archivePassword, Guid.NewGuid(), NoProgress.Instance, stages, timeout.Token, options)
                .ConfigureAwait(false);
        }
        catch (Exception e) when (!ct.IsCancellationRequested && e is not OutOfMemoryException)
        {
            failure = e;
        }

        var missingFirstSegments = snapshot?.MissingFirstSegment
            .Select(DisplayName)
            .ToList()
            ?? (MissingFileNameOf(failure) is { } missingFile ? [missingFile] : []);
        var warnings = snapshot?.MissingFirstSegment
            .Where(x => !DeadNzbFailFast.IsImportantFileName(x.FileName))
            .Select(x => new InspectionWarning
            {
                Code = InspectionWarningCodes.MissingFirstSegment,
                Message = "First segment missing across all providers.",
                Path = DisplayName(x),
            })
            .ToList() ?? [];

        if (failure is not null || budget.Exceeded || plan is null)
        {
            var (kind, message) = Classify(failure, budget, timeout, snapshot);
            Log.Information("NZB inspection stopped in {Stage}: {Kind} ({Reason})", stages.Current, kind, message);
            return FailureReport(stages, stages.Current, kind, message, sets, missingFirstSegments, warnings,
                budget.ArticleRequests, failure);
        }

        var outputs = PlannedImportOutputs.PlanOutputs(plan.FileProcessingResults, mountName).ToList();
        if (outputs.Count > _limits.MaxManifestFiles)
        {
            return FailureReport(stages, stages.Current, InspectionFailureKinds.LimitExceeded,
                "Inspection limit reached: manifest_files.", sets, missingFirstSegments, warnings,
                budget.ArticleRequests);
        }

        var files = outputs.Select(x => ToInspectedFile(x, plan.FileInfos)).ToList();
        foreach (var opaque in outputs.Where(IsUnexpandedArchive))
        {
            warnings.Add(new InspectionWarning
            {
                Code = InspectionWarningCodes.NestedArchiveOpaque,
                Message = "Archive inside an archive was not expanded; its contents are unknown.",
                Path = opaque.RawPath,
            });
        }

        var archives = BuildArchives(sets ?? [], outputs, warnings);
        var manifestComplete = !outputs.Any(IsUnexpandedArchive);
        Log.Information(
            "Inspected NZB: manifestComplete={ManifestComplete} files={Files} archives={Archives} articleRequests={ArticleRequests}",
            manifestComplete, files.Count, archives.Count, budget.ArticleRequests);
        return new NzbInspectionReport
        {
            ManifestComplete = manifestComplete,
            Archives = archives,
            Files = files,
            Diagnostics = new InspectionDiagnostics
            {
                MissingFirstSegments = missingFirstSegments,
                Warnings = warnings,
                Stages = stages.Stages,
                ArticleRequests = budget.ArticleRequests,
            },
        };
    }

    private (string Kind, string Message) Classify(
        Exception? failure,
        InspectionBudgetNntpClient budget,
        CancellationTokenSource timeout,
        ArchiveImportFileInfoSnapshot? snapshot)
    {
        if (budget.Exceeded)
            return (InspectionFailureKinds.LimitExceeded, $"Inspection limit reached: {budget.ExceededLimit}.");
        if (failure is null)
            return (InspectionFailureKinds.Failed, "Planning did not produce a result.");
        if (failure.TryGetCausingException<InspectionLimitExceededException>(out var limit) && limit is not null)
            return (InspectionFailureKinds.LimitExceeded, limit.Message);
        if (timeout.IsCancellationRequested && failure.IsCancellationException())
        {
            return (InspectionFailureKinds.Timeout, string.Create(CultureInfo.InvariantCulture,
                $"Inspection did not finish within {_limits.Timeout.TotalSeconds:0} seconds."));
        }
        if (failure.TryGetCausingException<SharpCompressCryptographicException>(out var archiveCrypto) && archiveCrypto is not null)
            return (InspectionFailureKinds.Password, archiveCrypto.Message);
        if (failure.TryGetCausingException<CryptographicException>(out var crypto) && crypto is not null)
            return (InspectionFailureKinds.Password, crypto.Message);
        if (failure.TryGetCausingException<PasswordProtectedRarException>(out var protectedRar) && protectedRar is not null)
            return (InspectionFailureKinds.Password, protectedRar.Message);
        if (IsUnsupported(failure))
            return (InspectionFailureKinds.Unsupported, failure.Message);
        if (failure.TryGetCausingException<UsenetArticleNotFoundException>(out _)
            || MissingFileNameOf(failure) is not null
            || (failure is NonRetryableDownloadException
                && snapshot?.MissingFirstSegment.Any(x => DeadNzbFailFast.IsImportantFileName(x.FileName)) == true))
            return (InspectionFailureKinds.MissingArticles, failure.Message);
        if (failure.IsRetryableDownloadException() || failure.IsTransientTransportException())
        {
            return (InspectionFailureKinds.ProviderUnavailable,
                failure.TryGetKnownErrorMessage(out var reason) ? reason : failure.Message);
        }
        return (InspectionFailureKinds.Failed, failure.Message);
    }

    /// <summary>The file named by a first-segment fail-fast (observed evidence, never inferred).</summary>
    private static string? MissingFileNameOf(Exception? failure) =>
        failure is NonRetryableDownloadException
            ? failure.Data[DeadNzbFailFast.MissingFileNameDataKey] as string
            : null;

    private static bool IsUnsupported(Exception failure) =>
        failure.TryGetCausingException<Unsupported7zCompressionMethodException>(out _)
        || failure.TryGetCausingException<UnsupportedRarCompressionMethodException>(out _)
        || failure.TryGetCausingException<UnsupportedRarUnknownSizeException>(out _);

    private static NzbInspectionReport FailureReport(
        InspectionStageRunner stages,
        string? stage,
        string kind,
        string message,
        List<ArchiveSetDescriptor>? sets,
        List<string> missingFirstSegments,
        List<InspectionWarning> warnings,
        int articleRequests,
        Exception? failure = null)
    {
        var unsupportedSevenZip = failure?.TryGetCausingException<Unsupported7zCompressionMethodException>(out _) == true;
        var unsupportedRar = kind == InspectionFailureKinds.Unsupported && !unsupportedSevenZip;
        var archives = (sets ?? [])
            .Select(set =>
            {
                var unsupported = (set.IsSevenZip && unsupportedSevenZip) || (!set.IsSevenZip && unsupportedRar);
                return new InspectedArchive
                {
                    ArchiveSetId = set.ArchiveSetId,
                    ArchiveType = set.IsSevenZip ? "7z" : "rar",
                    Encryption = InspectionEncryption.Unknown,
                    ContentAccess = unsupported ? InspectionContentAccess.Unsupported
                        : kind == InspectionFailureKinds.Password ? InspectionContentAccess.PasswordRequired
                        : InspectionContentAccess.Unknown,
                    StreamSupported = unsupported ? false : null,
                };
            })
            .ToList();
        return new NzbInspectionReport
        {
            ManifestComplete = false,
            Archives = archives,
            Files = [],
            Diagnostics = new InspectionDiagnostics
            {
                MissingFirstSegments = missingFirstSegments,
                Warnings = warnings,
                Failure = new InspectionFailure { Kind = kind, Stage = stage, Message = message },
                Stages = stages.Stages,
                ArticleRequests = articleRequests,
            },
        };
    }

    private static List<InspectedArchive> BuildArchives(
        List<ArchiveSetDescriptor> sets,
        List<PlannedImportOutputs.PlannedOutput> outputs,
        List<InspectionWarning> warnings)
    {
        var nestedExpanded = outputs.Any(x => NestingDepthOf(x) > 0);
        var archives = new List<InspectedArchive>();
        foreach (var set in sets)
            archives.Add(DescribeArchive(set.ArchiveSetId, set.IsSevenZip ? "7z" : "rar", 0, outputs, nestedExpanded, warnings));

        foreach (var nested in outputs
                     .Where(x => NestingDepthOf(x) > 0 && x.ArchiveSetId is not null)
                     .GroupBy(x => x.ArchiveSetId!, StringComparer.Ordinal))
        {
            archives.Add(DescribeArchive(nested.Key, "rar", nested.Max(NestingDepthOf), outputs, nestedExpanded, warnings));
        }

        return archives;
    }

    private static InspectedArchive DescribeArchive(
        string archiveSetId,
        string archiveType,
        int nestingDepth,
        List<PlannedImportOutputs.PlannedOutput> outputs,
        bool nestedExpanded,
        List<InspectionWarning> warnings)
    {
        var members = outputs
            .Where(x => string.Equals(x.ArchiveSetId, archiveSetId, StringComparison.Ordinal))
            .Select(x => (Output: x, Crypto: CryptoOf(x)))
            .ToList();

        string encryption;
        string contentAccess;
        if (members.Count == 0)
        {
            // A set whose only member was a stored archive that expanded successfully:
            // expansion reads the inner headers directly, which requires unencrypted data.
            encryption = nestedExpanded ? InspectionEncryption.None : InspectionEncryption.Unknown;
            contentAccess = nestedExpanded ? InspectionContentAccess.Plain : InspectionContentAccess.Unknown;
        }
        else if (!members.Any(x => x.Crypto.IsEncrypted))
        {
            encryption = InspectionEncryption.None;
            contentAccess = InspectionContentAccess.Plain;
        }
        else
        {
            var encrypted = members.Where(x => x.Crypto.IsEncrypted).ToList();
            encryption = InspectionEncryption.Data;
            if (encrypted.All(x => x.Crypto.PasswordVerified))
            {
                contentAccess = InspectionContentAccess.PasswordValidated;
            }
            else if (encrypted.Any(x => !x.Crypto.HasKey))
            {
                contentAccess = InspectionContentAccess.PasswordRequired;
            }
            else
            {
                contentAccess = InspectionContentAccess.Unknown;
                warnings.Add(new InspectionWarning
                {
                    Code = InspectionWarningCodes.PasswordNotValidated,
                    Message = "A password was supplied but this archive format cannot confirm it without reading the content.",
                    Path = archiveSetId,
                });
            }
        }

        return new InspectedArchive
        {
            ArchiveSetId = archiveSetId,
            ArchiveType = archiveType,
            NestingDepth = nestingDepth,
            Encryption = encryption,
            ContentAccess = contentAccess,
            StreamSupported = true,
        };
    }

    private static (bool IsEncrypted, bool HasKey, bool PasswordVerified) CryptoOf(PlannedImportOutputs.PlannedOutput output) =>
        output.Origin switch
        {
            List<RarProcessor.StoredFileSegment> parts => (
                parts.Any(x => x.IsEncrypted),
                parts.All(x => x.AesParams is not null),
                parts.All(x => x.PasswordVerified)),
            LazyRarProcessor.Result lazy => (lazy.IsEncrypted, lazy.AesParams is not null, lazy.PasswordVerified),
            SevenZipProcessor.SevenZipFile sevenZip => (
                sevenZip.IsEncrypted,
                sevenZip.DavMultipartFileMeta.AesParams is not null,
                false),
            _ => (false, false, false),
        };

    private static int NestingDepthOf(PlannedImportOutputs.PlannedOutput output) =>
        output.Origin is List<RarProcessor.StoredFileSegment> parts ? parts.Max(x => x.NestingDepth) : 0;

    private static bool IsUnexpandedArchive(PlannedImportOutputs.PlannedOutput output)
    {
        var leaf = Path.GetFileName(output.RawPath);
        return output.Kind != PlannedImportOutputs.Kinds.Direct
               && (FilenameUtil.IsRarFile(leaf) || FilenameUtil.Is7zFile(leaf));
    }

    private static InspectedFile ToInspectedFile(
        PlannedImportOutputs.PlannedOutput output,
        IReadOnlyList<GetFileInfosStep.FileInfo> fileInfos) =>
        new()
        {
            RawPath = output.RawPath,
            PlannedName = output.Name,
            Size = output.FileSize,
            MediaExtension = output.SniffedVideoExtension
                             ?? (FilenameUtil.IsVideoFile(output.RawPath) ? Path.GetExtension(output.RawPath) : null),
            NameSource = output.Origin switch
            {
                FileAggregator.PlannedDirectFile direct => fileInfos
                    .FirstOrDefault(x => ReferenceEquals(x.NzbFile, direct.NzbFile))?.NameSource,
                MultipartMkvProcessor.Result split => fileInfos
                    .FirstOrDefault(x => string.Equals(
                        FilenameUtil.GetSplitVideoBaseName(x.FileName), split.Filename, StringComparison.OrdinalIgnoreCase))
                    ?.NameSource,
                _ => ArchiveHeaderNameSource,
            },
            ArchiveSetId = output.Kind is PlannedImportOutputs.Kinds.Direct or PlannedImportOutputs.Kinds.SplitVideo
                ? null
                : output.ArchiveSetId,
            NestingDepth = NestingDepthOf(output),
        };

    private static string DisplayName(GetFileInfosStep.FileInfo fileInfo) =>
        string.IsNullOrEmpty(fileInfo.FileName) ? fileInfo.NzbFile.Subject : fileInfo.FileName;

    private sealed class InspectionStageRunner : IArchiveImportStageRunner
    {
        private readonly List<string> _stages = [];
        private readonly Lock _lock = new();

        public string? Current { get; private set; }

        public IReadOnlyList<string> Stages
        {
            get
            {
                lock (_lock) return _stages.ToList();
            }
        }

        public Task<T> RunStageAsync<T>(string stage, Func<Task<T>> action)
        {
            lock (_lock) _stages.Add(stage);
            Current = stage;
            return action();
        }
    }

    private sealed class AdmissionLease(SemaphoreSlim admission) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                admission.Release();
        }
    }

    private sealed class NoProgress : IProgress<int>
    {
        public static NoProgress Instance { get; } = new();

        public void Report(int value)
        {
        }
    }
}
