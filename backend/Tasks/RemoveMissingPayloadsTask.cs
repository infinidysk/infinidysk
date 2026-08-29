using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Clients.RadarrSonarr;
using NzbWebDAV.Clients.RadarrSonarr.BaseModels;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;
using NzbWebDAV.Queue.PostProcessors;
using NzbWebDAV.Services;
using NzbWebDAV.Utils;
using NzbWebDAV.Websocket;
using Serilog;

namespace NzbWebDAV.Tasks;

public sealed class RemoveMissingPayloadsTask : BaseTask
{
    private const int BatchSize = 100;
    private static readonly TimeSpan DefaultProgressHeartbeatInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PreviewLifetime = TimeSpan.FromMinutes(15);
    private static readonly object AuditLock = new();
    private static readonly object PreviewLock = new();
    private static List<string> _auditLines = [];
    private static PreviewApproval? _previewApproval;

    private readonly ConfigManager _configManager;
    private readonly WebsocketManager _websocketManager;
    private readonly ArrReplacementSearchBudget _replacementSearchBudget;
    private readonly bool _isDryRun;
    private readonly Func<DavDatabaseContext>? _createContext;
    private readonly IBlobStore? _blobStore;
    private readonly IReadOnlyList<ArrClient>? _arrClients;
    private readonly string? _previewToken;
    private readonly bool _requirePreviewApproval;
    private readonly TimeSpan _progressHeartbeatInterval;
    private readonly Action<string>? _progressObserver;
    private readonly CleanupStats _stats = new();
    private RemoveUnlinkedFilesTask.ProgressHeartbeat? _progressHeartbeat;

    public RemoveMissingPayloadsTask(
        ConfigManager configManager,
        WebsocketManager websocketManager,
        ArrReplacementSearchBudget replacementSearchBudget,
        bool isDryRun,
        string? previewToken = null)
        : this(
            configManager,
            websocketManager,
            replacementSearchBudget,
            isDryRun,
            createContext: null,
            blobStore: null,
            arrClients: null,
            previewToken: previewToken)
    {
    }

    internal RemoveMissingPayloadsTask(
        ConfigManager configManager,
        WebsocketManager websocketManager,
        ArrReplacementSearchBudget replacementSearchBudget,
        bool isDryRun,
        Func<DavDatabaseContext>? createContext,
        IBlobStore? blobStore,
        IReadOnlyList<ArrClient>? arrClients,
        string? previewToken = null,
        bool requirePreviewApproval = true,
        TimeSpan? progressHeartbeatInterval = null,
        Action<string>? progressObserver = null)
    {
        _configManager = configManager;
        _websocketManager = websocketManager;
        _replacementSearchBudget = replacementSearchBudget;
        _isDryRun = isDryRun;
        _createContext = createContext;
        _blobStore = blobStore;
        _arrClients = arrClients;
        _previewToken = previewToken;
        _requirePreviewApproval = requirePreviewApproval;
        _progressHeartbeatInterval = progressHeartbeatInterval ?? DefaultProgressHeartbeatInterval;
        _progressObserver = progressObserver;
    }

    internal CleanupStats Stats => _stats;
    public bool Succeeded { get; private set; }
    public string? TerminalMessage { get; private set; }
    public string? IssuedPreviewToken { get; private set; }

    private DavDatabaseContext CreateContext() => DavDatabaseContexts.Create(_createContext);

    protected override async Task ExecuteInternal()
    {
        await using var progressHeartbeat =
            new RemoveUnlinkedFilesTask.ProgressHeartbeat(Report, _progressHeartbeatInterval);
        _progressHeartbeat = progressHeartbeat;
        try
        {
            await RunAsync().ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Complete($"Failed: {e.Message}");
            if (e.TryGetKnownErrorMessage(out var reason))
            {
                Log.Warning("Could not clean missing streaming payloads. Reason: {Reason}", reason);
                Log.Debug(e, "Missing-payload cleanup known failure stack");
            }
            else
            {
                Log.Error(e, "Failed to clean missing streaming payloads.");
            }
        }
        finally
        {
            _progressHeartbeat = null;
        }
    }

    private async Task RunAsync()
    {
        ResetAudit();
        var libraryDir = _configManager.GetLibraryDir();
        if (string.IsNullOrWhiteSpace(libraryDir))
        {
            Complete(
                "Aborted: Configure the Library Directory under Repairs so library links can be " +
                "verified before cleanup.");
            return;
        }

        if (RemoveUnlinkedFilesTask.IsLibraryDirInsideRcloneMount(
                libraryDir,
                _configManager.GetRcloneMountDir(),
                out var normalizedLibraryDir,
                out var normalizedMountDir))
        {
            Complete(
                $"Aborted: Library Directory '{normalizedLibraryDir}' is inside the rclone mount " +
                $"'{normalizedMountDir}'. Point it at the organized Arr library, then retry.");
            return;
        }

        if (!Directory.Exists(libraryDir))
        {
            Complete($"Aborted: Library Directory '{libraryDir}' is not available.");
            return;
        }

        var startedAt = DateTime.Now;
        var candidates = await FindMissingPayloadsAsync(startedAt).ConfigureAwait(false);
        if (candidates.Count == 0)
        {
            var emptyFingerprint = ComputePreviewFingerprint(
                candidates,
                new Dictionary<Guid, List<OrganizedLinksUtil.DavItemLink>>());
            if (_isDryRun)
                IssuedPreviewToken = IssuePreviewApproval(emptyFingerprint);
            else if (!TryValidatePreviewApproval(emptyFingerprint, out var emptyPreviewError))
            {
                Complete($"Aborted: {emptyPreviewError}");
                return;
            }

            if (!_isDryRun)
                ConsumePreviewApproval(_previewToken);
            Complete("Done. No missing streaming payloads found.");
            return;
        }

        StartPhase("Scanning verified library links...");
        var candidateIds = candidates.Select(item => item.Id).ToHashSet();
        var linksByItem = ScanLinks(candidateIds);
        var previewFingerprint = ComputePreviewFingerprint(candidates, linksByItem);

        if (_isDryRun)
        {
            var dryRunArrStates = await LoadArrStatesAsync().ConfigureAwait(false);
            await BuildDryRunAsync(candidates, linksByItem, dryRunArrStates).ConfigureAwait(false);
            IssuedPreviewToken = IssuePreviewApproval(previewFingerprint);
            Complete(
                $"Done. Identified {candidates.Count} missing payload" +
                $"{(candidates.Count == 1 ? "" : "s")}; " +
                $"{_stats.LinkedFiles} verified library link" +
                $"{(_stats.LinkedFiles == 1 ? "" : "s")}; " +
                $"{_stats.SkippedItems} item{(_stats.SkippedItems == 1 ? "" : "s")} require attention.");
            return;
        }

        if (!TryValidatePreviewApproval(previewFingerprint, out var previewError))
        {
            Complete($"Aborted: {previewError}");
            return;
        }

        DeletionAuditLog.WarnBulkDelete(
            "remove-missing-payloads",
            candidates.Count,
            "manual missing-payload cleanup after a reviewed dry run");
        var arrStates = await LoadArrStatesAsync().ConfigureAwait(false);
        StartPhase($"Removing links for {candidates.Count} missing payloads...");
        var readyForDelete = new List<DavItem>(candidates.Count);
        foreach (var (item, index) in candidates.Select((item, index) => (item, index)))
        {
            CancellationToken.ThrowIfCancellationRequested();
            var externalLinks = GetExternalLinks(item, linksByItem);
            var plans = await PlanLinksAsync(externalLinks, arrStates).ConfigureAwait(false);
            if (plans.Any(plan => plan.BlockReason is not null))
            {
                _stats.SkippedItems++;
                AppendAudit(
                    $"SKIPPED\t{item.Path}\treason={string.Join("; ", plans.Where(plan => plan.BlockReason is not null).Select(plan => plan.BlockReason))}");
                continue;
            }
            if (HasMultipleArrMediaFiles(plans))
            {
                _stats.SkippedItems++;
                AppendAudit(
                    $"SKIPPED\t{item.Path}\treason=multiple distinct Arr media-file records matched one WebDAV item");
                continue;
            }

            if (!await IsStillMissingAsync(item.Id).ConfigureAwait(false))
            {
                _stats.SkippedItems++;
                AppendAudit($"RECOVERED\t{item.Path}\tpayload became available before link cleanup");
                continue;
            }

            if (await ProcessLinkPlansAsync(item, plans).ConfigureAwait(false))
                readyForDelete.Add(item);
            else
                _stats.SkippedItems++;

            UpdatePhase(
                $"Removing links for {candidates.Count} missing payloads...\n" +
                $"Processed {index + 1}/{candidates.Count}; ready {readyForDelete.Count}; " +
                $"skipped {_stats.SkippedItems}.");
        }

        StartPhase("Rechecking the library for new or drifted links...");
        var remainingLinks = ScanLinks(readyForDelete.Select(item => item.Id).ToHashSet());
        readyForDelete = readyForDelete
            .Where(item =>
            {
                var remaining = GetExternalLinks(item, remainingLinks);
                if (remaining.Length == 0)
                    return true;

                _stats.SkippedItems++;
                AppendAudit(
                    $"SKIPPED\t{item.Path}\treason={remaining.Length} library link(s) still target the item after cleanup");
                return false;
            })
            .ToList();

        await DeleteItemsAsync(readyForDelete, startedAt).ConfigureAwait(false);

        ConsumePreviewApproval(_previewToken);
        Complete(
            $"Done. Removed {_stats.RemovedItems} missing-payload item" +
            $"{(_stats.RemovedItems == 1 ? "" : "s")} and {_stats.RemovedLinks} library link" +
            $"{(_stats.RemovedLinks == 1 ? "" : "s")}; requested {_stats.SearchesRequested} " +
            $"replacement search{(_stats.SearchesRequested == 1 ? "" : "es")}; " +
            $"withheld {_stats.SearchesWithheld}; search failures {_stats.SearchesFailed}; " +
            $"skipped {_stats.SkippedItems}.");
    }

    private async Task<List<DavItem>> FindMissingPayloadsAsync(DateTime startedAt)
    {
        StartPhase("Scanning streaming payload references...");
        var candidates = new List<DavItem>();
        var lastId = Guid.Empty;
        var scanned = 0;
        while (true)
        {
            CancellationToken.ThrowIfCancellationRequested();
            await using var context = CreateContext();
            var dbClient = new DavDatabaseClient(context, _blobStore);
            var batch = await context.Items
                .AsNoTracking()
                .Where(item => item.Type == DavItem.ItemType.UsenetFile)
                .Where(item => item.CreatedAt < startedAt)
                .Where(item => item.Id > lastId)
                .OrderBy(item => item.Id)
                .Take(BatchSize)
                .ToListAsync(CancellationToken)
                .ConfigureAwait(false);
            if (batch.Count == 0)
                break;

            foreach (var item in batch)
            {
                if (await dbClient.StreamingPayloadExistsAsync(item, CancellationToken).ConfigureAwait(false))
                    continue;

                candidates.Add(item);
            }

            scanned += batch.Count;
            lastId = batch[^1].Id;
            UpdatePhase(
                $"Scanning streaming payload references...\nScanned {scanned}; found {candidates.Count}.");
        }

        _stats.Candidates = candidates.Count;
        return candidates;
    }

    private async Task<bool> IsStillMissingAsync(Guid itemId)
    {
        await using var context = CreateContext();
        var item = await context.Items
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == itemId, CancellationToken)
            .ConfigureAwait(false);
        if (item is null)
            return false;

        var dbClient = new DavDatabaseClient(context, _blobStore);
        return !await dbClient.StreamingPayloadExistsAsync(item, CancellationToken).ConfigureAwait(false);
    }

    private Dictionary<Guid, List<OrganizedLinksUtil.DavItemLink>> ScanLinks(
        HashSet<Guid> candidateIds)
    {
        var result = new Dictionary<Guid, List<OrganizedLinksUtil.DavItemLink>>();
        if (candidateIds.Count == 0)
            return result;

        var scanned = 0;
        foreach (var link in OrganizedLinksUtil.GetLibraryDavItemLinks(_configManager))
        {
            CancellationToken.ThrowIfCancellationRequested();
            scanned++;
            if (candidateIds.Contains(link.DavItemId))
            {
                if (!result.TryGetValue(link.DavItemId, out var links))
                    result[link.DavItemId] = links = [];
                if (!links.Any(existing => PathsEqual(existing.LinkPath, link.LinkPath)))
                    links.Add(link);
            }

            if (scanned % 100 == 0)
                UpdatePhase($"Scanning verified library links...\nScanned {scanned}.");
        }

        return result;
    }

    private async Task<List<ArrClientState>> LoadArrStatesAsync()
    {
        var clients = _arrClients ?? _configManager.GetArrConfig().GetArrClients().ToArray();
        var states = new List<ArrClientState>(clients.Count);
        foreach (var client in clients)
        {
            try
            {
                var rootFolders = await client.GetRootFolders(CancellationToken).ConfigureAwait(false);
                var invalidRoot = rootFolders.Any(root => string.IsNullOrWhiteSpace(root.Path));
                states.Add(new ArrClientState(
                    client,
                    invalidRoot ? null : rootFolders,
                    invalidRoot ? "Arr returned an empty root-folder path" : null));
                if (invalidRoot)
                {
                    Log.Warning(
                        "Missing-payload cleanup cannot safely use {Host}: Arr returned an empty root-folder path.",
                        client.Host);
                }
            }
            catch (OperationCanceledException) when (CancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                var reason = e.TryGetKnownErrorMessage(out var knownReason)
                    ? knownReason
                    : e.Message;
                states.Add(new ArrClientState(client, null, reason));
                Log.Warning(
                    "Missing-payload cleanup could not query root folders from {Host}. Reason: {Reason}",
                    client.Host,
                    reason);
                Log.Debug(e, "Missing-payload cleanup Arr root-folder failure stack");
            }
        }

        return states;
    }

    private async Task BuildDryRunAsync(
        List<DavItem> candidates,
        IReadOnlyDictionary<Guid, List<OrganizedLinksUtil.DavItemLink>> linksByItem,
        IReadOnlyList<ArrClientState> arrStates)
    {
        StartPhase($"Previewing {candidates.Count} missing payloads...");
        var index = 0;
        foreach (var item in candidates)
        {
            CancellationToken.ThrowIfCancellationRequested();
            var links = GetExternalLinks(item, linksByItem);
            var plans = await PlanLinksAsync(links, arrStates).ConfigureAwait(false);
            _stats.LinkedFiles += links.Length;
            var multipleArrFiles = HasMultipleArrMediaFiles(plans);
            if (plans.Any(plan => plan.BlockReason is not null) || multipleArrFiles)
                _stats.SkippedItems++;

            AppendAudit(
                $"CANDIDATE\t{item.Path}\tstore={item.SubType}\tpayload={item.FileBlobId?.ToString() ?? "none"}\tlinks={links.Length}");
            if (multipleArrFiles)
            {
                AppendAudit(
                    "  BLOCKED\tmultiple distinct Arr media-file records matched one WebDAV item");
            }
            foreach (var plan in plans)
            {
                var action = plan.BlockReason is not null
                    ? $"blocked: {plan.BlockReason}"
                    : plan.Client is not null && plan.Match is not null
                        ? $"{plan.Client.GetType().Name} {plan.Client.Host}; remove media file {plan.Match.FileId}; request {string.Join(",", plan.Match.MediaKeys)}"
                        : "remove verified link directly; no Arr media-file match";
                AppendAudit($"  LINK\t{plan.Link.LinkPath}\t{action}");
            }

            if (!string.IsNullOrWhiteSpace(item.GeneratedStrmPath)
                && !links.Any(link => PathsEqual(link.LinkPath, item.GeneratedStrmPath)))
                AppendAudit($"  SIDECAR\t{item.GeneratedStrmPath}\tremove if ownership still matches");

            index++;
            UpdatePhase(
                $"Previewing {candidates.Count} missing payloads...\nReviewed {index}/{candidates.Count}.");
        }
    }

    private async Task<List<LinkPlan>> PlanLinksAsync(
        OrganizedLinksUtil.DavItemLink[] links,
        IReadOnlyList<ArrClientState> arrStates)
    {
        var result = new List<LinkPlan>(links.Length);
        foreach (var link in links)
        {
            var eligible = arrStates
                .Where(state => state.RootFolders?.Any(root =>
                    !string.IsNullOrWhiteSpace(root.Path)
                    && IsPathInsideRoot(link.LinkPath, root.Path!)) == true)
                .ToList();
            var matches = new List<(ArrClient Client, ArrMediaFileMatch Match)>();
            string? lookupFailure = null;
            foreach (var state in eligible)
            {
                try
                {
                    var match = await state.Client
                        .FindMediaFileAsync(link.LinkPath, CancellationToken)
                        .ConfigureAwait(false);
                    if (match is not null)
                        matches.Add((state.Client, match));
                }
                catch (OperationCanceledException) when (CancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    lookupFailure =
                        $"Arr ownership lookup failed on {state.Client.Host}: {e.Message}";
                    Log.Warning(
                        "Missing-payload cleanup could not resolve media ownership on {Host} for {Path}. Reason: {Reason}",
                        state.Client.Host,
                        link.LinkPath,
                        e.Message);
                    Log.Debug(e, "Missing-payload cleanup Arr ownership failure stack");
                }
            }

            if (lookupFailure is not null)
            {
                result.Add(new LinkPlan(link, null, null, lookupFailure));
            }
            else if (arrStates.Any(state => state.Error is not null))
            {
                result.Add(new LinkPlan(
                    link,
                    null,
                    null,
                    "at least one enabled Arr instance was unreachable, so ownership is ambiguous"));
            }
            else if (matches.Count > 1)
            {
                result.Add(new LinkPlan(
                    link,
                    null,
                    null,
                    "multiple Arr instances reported ownership"));
            }
            else if (matches.Count == 1)
            {
                result.Add(new LinkPlan(
                    link,
                    matches[0].Client,
                    matches[0].Match,
                    null));
            }
            else
            {
                result.Add(new LinkPlan(link, null, null, null));
            }
        }

        return result;
    }

    private static bool HasMultipleArrMediaFiles(List<LinkPlan> plans) =>
        plans
            .Where(plan => plan.Client is not null && plan.Match is not null)
            .Select(plan => (
                Host: plan.Client!.Host.TrimEnd('/').ToLowerInvariant(),
                plan.Match!.Kind,
                plan.Match.FileId))
            .Distinct()
            .Take(2)
            .Count() > 1;

    private async Task<bool> ProcessLinkPlansAsync(DavItem item, IReadOnlyCollection<LinkPlan> plans)
    {
        var quarantined = new List<(LinkPlan Plan, OrganizedLinksUtil.QuarantinedLink Link)>();
        foreach (var plan in plans)
        {
            try
            {
                quarantined.Add((
                    plan,
                    OrganizedLinksUtil.QuarantineIfStillTargets(plan.Link, _configManager)));
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                RestoreQuarantinedLinks(quarantined, item);
                AppendAudit(
                    $"SKIPPED\t{item.Path}\tlink={plan.Link.LinkPath}\treason=link quarantine failed: {e.Message}");
                Log.Warning(
                    "Missing-payload cleanup could not quarantine verified library link {LinkPath}. Reason: {Reason}",
                    plan.Link.LinkPath,
                    e.Message);
                Log.Debug(e, "Missing-payload library-link quarantine failure stack");
                return false;
            }
        }

        if (!await IsStillMissingAsync(item.Id).ConfigureAwait(false))
        {
            RestoreQuarantinedLinks(quarantined, item);
            AppendAudit($"RECOVERED\t{item.Path}\tpayload became available before Arr cleanup");
            return false;
        }

        var processedArrMatches = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var (plan, _) in quarantined)
            {
                CancellationToken.ThrowIfCancellationRequested();
                if (plan.Client is null
                    || plan.Match is null
                    || !processedArrMatches.Add(
                        $"{plan.Client.Host.TrimEnd('/')}|{plan.Match.Kind}|{plan.Match.FileId}"))
                {
                    continue;
                }

                var outcome = await plan.Client.RemoveMissingPayloadAndSearchAsync(
                            plan.Match,
                            keys => ReserveSearches(plan.Client, keys),
                            CancellationToken)
                    .ConfigureAwait(false);
                if (outcome == ArrMissingPayloadCleanupOutcome.MediaItemNotFound)
                {
                    RestoreQuarantinedLinks(quarantined, item);
                    AppendAudit(
                        $"SKIPPED\t{item.Path}\tlink={plan.Link.LinkPath}\treason=Arr media-file match disappeared before cleanup");
                    return false;
                }

                TrackSearchOutcome(outcome);
                AppendAudit(
                    $"ARR\t{item.Path}\tlink={plan.Link.LinkPath}\thost={plan.Client.Host}\tfileId={plan.Match.FileId}\toutcome={outcome}");
            }
        }
        catch (OperationCanceledException) when (CancellationToken.IsCancellationRequested)
        {
            RestoreQuarantinedLinks(quarantined, item);
            throw;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            RestoreQuarantinedLinks(quarantined, item);
            var failedPlan = quarantined
                .Select(entry => entry.Plan)
                .FirstOrDefault(plan => plan.Client is not null);
            AppendAudit(
                $"SKIPPED\t{item.Path}\treason=Arr cleanup failed" +
                (failedPlan?.Client is null ? "" : $" on {failedPlan.Client.Host}") +
                $": {e.Message}");
            Log.Warning(
                "Missing-payload cleanup left {Path} in place because Arr cleanup failed. Reason: {Reason}",
                item.Path,
                e.Message);
            Log.Debug(e, "Missing-payload Arr cleanup failure stack");
            return false;
        }

        foreach (var (_, link) in quarantined)
        {
            try
            {
                OrganizedLinksUtil.DeleteQuarantinedLink(link, _configManager);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                RestoreQuarantinedLinks(
                    quarantined.Where(entry =>
                        OrganizedLinksUtil.QuarantinedLinkExists(entry.Link)).ToList(),
                    item);
                AppendAudit(
                    $"SKIPPED\t{item.Path}\tlink={link.OriginalPath}\treason=quarantined link deletion failed: {e.Message}");
                Log.Warning(
                    "Missing-payload cleanup could not remove verified library link {LinkPath}. Reason: {Reason}",
                    link.OriginalPath,
                    e.Message);
                Log.Debug(e, "Missing-payload library-link deletion failure stack");
                return false;
            }

            _stats.RemovedLinks++;
            AppendAudit($"LINK\t{item.Path}\tremoved={link.OriginalPath}");
        }

        return true;
    }

    private static void RestoreQuarantinedLinks(
        IEnumerable<(LinkPlan Plan, OrganizedLinksUtil.QuarantinedLink Link)> quarantined,
        DavItem item)
    {
        foreach (var (_, link) in quarantined.Reverse())
        {
            try
            {
                if (!OrganizedLinksUtil.TryRestoreQuarantinedLink(link)
                    && OrganizedLinksUtil.QuarantinedLinkExists(link))
                {
                    Log.Warning(
                        "Could not restore quarantined library link {QuarantinePath} to {OriginalPath} " +
                        "while retaining missing-payload item {Path}; the original path is occupied.",
                        link.QuarantinePath,
                        link.OriginalPath,
                        item.Path);
                }
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                Log.Warning(
                    "Could not restore quarantined library link {QuarantinePath} to {OriginalPath}. Reason: {Reason}",
                    link.QuarantinePath,
                    link.OriginalPath,
                    e.Message);
                Log.Debug(e, "Missing-payload quarantine restore failure stack");
            }
        }
    }

    private bool ReserveSearches(ArrClient client, IReadOnlyList<string> mediaKeys)
    {
        var arrConfig = _configManager.GetArrConfig();
        var scopedKeys = mediaKeys
            .Select(key => $"{client.Host.TrimEnd('/').ToLowerInvariant()}|{key}")
            .ToArray();
        return _replacementSearchBudget.TryReserveAll(
            scopedKeys,
            arrConfig.EffectiveQueueReplacementSearchLimit(),
            arrConfig.EffectiveQueueReplacementSearchWindow());
    }

    private void TrackSearchOutcome(ArrMissingPayloadCleanupOutcome outcome)
    {
        switch (outcome)
        {
            case ArrMissingPayloadCleanupOutcome.RemovedSearchRequested:
                _stats.SearchesRequested++;
                break;
            case ArrMissingPayloadCleanupOutcome.RemovedSearchWithheld:
                _stats.SearchesWithheld++;
                break;
            case ArrMissingPayloadCleanupOutcome.RemovedSearchFailed:
                _stats.SearchesFailed++;
                break;
        }
    }

    private async Task DeleteItemsAsync(
        List<DavItem> candidates,
        DateTime startedAt)
    {
        StartPhase($"Deleting {candidates.Count} broken WebDAV items...");
        foreach (var candidateBatch in candidates.Chunk(BatchSize))
        {
            CancellationToken.ThrowIfCancellationRequested();
            var batchList = candidateBatch.ToList();
            var remainingLinks = ScanLinks(batchList.Select(item => item.Id).ToHashSet());
            batchList = batchList
                .Where(item =>
                {
                    var remaining = GetExternalLinks(item, remainingLinks);
                    if (remaining.Length == 0)
                        return true;

                    _stats.SkippedItems++;
                    AppendAudit(
                        $"SKIPPED\t{item.Path}\treason={remaining.Length} library link(s) appeared before database deletion");
                    return false;
                })
                .ToList();
            if (batchList.Count == 0)
                continue;

            var ids = batchList.Select(item => item.Id).ToArray();
            await using var context = CreateContext();
            var dbClient = new DavDatabaseClient(context, _blobStore);
            var currentItems = await context.Items
                .Where(item => ids.Contains(item.Id))
                .ToListAsync(CancellationToken)
                .ConfigureAwait(false);
            var deleting = new List<DavItem>();
            foreach (var item in currentItems)
            {
                if (await dbClient.StreamingPayloadExistsAsync(item, CancellationToken).ConfigureAwait(false))
                {
                    _stats.SkippedItems++;
                    AppendAudit($"RECOVERED\t{item.Path}\tpayload became available before deletion");
                    continue;
                }

                if (!TryDeleteGeneratedSidecar(item))
                {
                    _stats.SkippedItems++;
                    AppendAudit(
                        $"SKIPPED\t{item.Path}\treason=generated STRM sidecar could not be removed safely");
                    continue;
                }
                DeletionAuditLog.Record(
                    "remove-missing-payloads",
                    item,
                    "streaming payload and legacy metadata are absent after manual confirmation");
                deleting.Add(item);
            }

            if (deleting.Count == 0)
                continue;

            context.Items.RemoveRange(deleting);
            await context.SaveChangesAsync(CancellationToken).ConfigureAwait(false);
            _stats.RemovedItems += deleting.Count;
            foreach (var item in deleting)
                AppendAudit($"REMOVED\t{item.Path}\tid={item.Id}\tpayload={item.FileBlobId?.ToString() ?? "none"}");
            var cleanupParents = deleting
                .Where(item => item.ParentId.HasValue)
                .Select(item => item.ParentId!.Value)
                .ToHashSet();
            var historyIds = deleting
                .Where(item => item.HistoryItemId.HasValue)
                .Select(item => item.HistoryItemId!.Value)
                .ToHashSet();
            await RemoveEmptyAncestorDirectoriesAsync(cleanupParents, historyIds, startedAt)
                .ConfigureAwait(false);
            await PruneHistoryAsync(historyIds).ConfigureAwait(false);
            UpdatePhase(
                $"Deleting {candidates.Count} broken WebDAV items...\nRemoved {_stats.RemovedItems}/{candidates.Count}.");
        }
    }

    private bool TryDeleteGeneratedSidecar(DavItem item)
    {
        try
        {
            if (!CreateStrmFilesPostProcessor.DeleteStrmFile(item))
            {
                return CanProveGeneratedSidecarNoLongerTargetsItem(item);
            }

            _stats.RemovedSidecars++;
            AppendAudit($"SIDECAR\t{item.Path}\tremoved={item.GeneratedStrmPath}");
            return true;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Log.Warning(
                "Could not remove the generated STRM sidecar for missing-payload item {Path}. " +
                "The WebDAV item will be retained for a later retry. Reason: {Reason}",
                item.Path,
                e.Message);
            Log.Debug(e, "Missing-payload generated-sidecar cleanup failure stack");
            AppendAudit(
                $"SIDECAR-FAILED\t{item.Path}\tpath={item.GeneratedStrmPath}\treason={e.Message}");
            return false;
        }
    }

    private static bool CanProveGeneratedSidecarNoLongerTargetsItem(DavItem item)
    {
        if (string.IsNullOrWhiteSpace(item.GeneratedStrmPath))
            return true;
        if (!OrganizedLinksUtil.PathEntryExists(item.GeneratedStrmPath))
            return true;
        if (string.IsNullOrWhiteSpace(item.GeneratedStrmOutputRoot))
            return false;

        var outputRoot = Path.GetFullPath(item.GeneratedStrmOutputRoot);
        var sidecarPath = Path.GetFullPath(item.GeneratedStrmPath);
        if (!CreateStrmFilesPostProcessor.IsPathWithinRoot(sidecarPath, outputRoot)
            || CreateStrmFilesPostProcessor.HasSymlinkedAncestor(sidecarPath, outputRoot))
        {
            return false;
        }

        var current = SymlinkAndStrmUtil.GetSymlinkOrStrmInfo(new FileInfo(sidecarPath));
        if (current is null)
            return true;
        if (current is not SymlinkAndStrmUtil.StrmInfo strm)
            return false;

        return OrganizedLinksUtil.GetDavItemLink(strm)?.DavItemId != item.Id;
    }

    private async Task RemoveEmptyAncestorDirectoriesAsync(
        HashSet<Guid> pendingIds,
        HashSet<Guid> historyIds,
        DateTime startedAt)
    {
        if (pendingIds.Count == 0)
            return;

        StartPhase("Removing empty directories left by cleanup...");
        while (pendingIds.Count > 0)
        {
            CancellationToken.ThrowIfCancellationRequested();
            await using var context = CreateContext();
            var directories = new List<DavItem>();
            foreach (var ids in pendingIds.Chunk(500).Select(chunk => chunk.ToArray()))
            {
                directories.AddRange(await context.Items
                    .Where(item => ids.Contains(item.Id))
                    .Where(item => item.SubType == DavItem.ItemSubType.Directory)
                    .Where(item => item.CreatedAt < startedAt)
                    .Where(item => item.ParentId != DavItem.ContentFolder.Id)
                    .Where(item => item.ParentId != DavItem.NzbFolder.Id)
                    .ToListAsync(CancellationToken)
                    .ConfigureAwait(false));
            }

            if (directories.Count == 0)
                break;

            var isPostgres = context.Database.IsNpgsql();
            var candidates = directories.Select(item =>
                new RemoveUnlinkedFilesTask.UnlinkedItemInfo(
                    isPostgres
                        ? item.Id.ToString()
                        : item.Id.ToString().ToUpperInvariant(),
                    (int)item.Type,
                    item.Path)).ToArray();
            var deletedCount = 0;
            foreach (var chunk in candidates.Chunk(500))
            {
                deletedCount += await RemoveUnlinkedFilesTask.DeleteEmptyDirectoriesByIdTextAsync(
                        context,
                        chunk,
                        CancellationToken)
                    .ConfigureAwait(false);
            }
            if (deletedCount == 0)
                break;

            var candidateIds = directories.Select(item => item.Id).ToArray();
            var survivors = new HashSet<Guid>();
            foreach (var ids in candidateIds.Chunk(500).Select(chunk => chunk.ToArray()))
            {
                var found = await context.Items
                    .AsNoTracking()
                    .Where(item => ids.Contains(item.Id))
                    .Select(item => item.Id)
                    .ToListAsync(CancellationToken)
                    .ConfigureAwait(false);
                survivors.UnionWith(found);
            }
            var deleted = directories.Where(item => !survivors.Contains(item.Id)).ToList();
            pendingIds = deleted
                .Where(item => item.ParentId.HasValue)
                .Select(item => item.ParentId!.Value)
                .ToHashSet();
            foreach (var item in deleted)
            {
                DeletionAuditLog.Record(
                    "remove-missing-payloads",
                    item,
                    "empty ancestor after missing-payload cleanup");
                if (item.HistoryItemId is { } historyId)
                    historyIds.Add(historyId);
            }

            _ = DavDatabaseContext.RcloneVfsForget(deleted, CancellationToken);
            _stats.RemovedDirectories += deleted.Count;
        }
    }

    private async Task PruneHistoryAsync(HashSet<Guid> historyIds)
    {
        if (historyIds.Count == 0)
            return;

        await using var context = CreateContext();
        var dbClient = new DavDatabaseClient(context, _blobStore);
        var pruned = await dbClient.PruneUnreferencedHistoryItemsAsync(
                historyIds,
                source: "remove-missing-payloads",
                ct: CancellationToken)
            .ConfigureAwait(false);
        if (pruned.Count > 0)
            await dbClient.SaveHistoryRemovalAsync(CancellationToken).ConfigureAwait(false);
    }

    private static OrganizedLinksUtil.DavItemLink[] GetExternalLinks(
        DavItem item,
        IReadOnlyDictionary<Guid, List<OrganizedLinksUtil.DavItemLink>> linksByItem)
    {
        if (!linksByItem.TryGetValue(item.Id, out var links))
            return [];

        return links.ToArray();
    }

    private static bool IsPathInsideRoot(string path, string root)
    {
        try
        {
            var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
            return relative != ".."
                   && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                   && !Path.IsPathRooted(relative);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);

    private void StartPhase(string message) => _progressHeartbeat?.StartPhase(message);

    private void UpdatePhase(string message) => _progressHeartbeat?.UpdatePhase(message);

    private void Complete(string message)
    {
        TerminalMessage = message;
        Succeeded = message.StartsWith("Done.", StringComparison.Ordinal);
        if (_progressHeartbeat is not null)
            _progressHeartbeat.Complete(message);
        else
            Report(message);
    }

    private void Report(string message)
    {
        var prefix = _isDryRun ? "Dry Run - " : string.Empty;
        var progress = $"{prefix}{message}";
        _progressObserver?.Invoke(progress);
        _ = _websocketManager.SendMessage(WebsocketTopic.MissingPayloadCleanupProgress, progress);
    }

    private string ComputePreviewFingerprint(
        List<DavItem> candidates,
        Dictionary<Guid, List<OrganizedLinksUtil.DavItemLink>> linksByItem)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Append(string? value)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(value ?? "<null>"));
            hash.AppendData([0]);
        }

        Append(_configManager.GetLibraryDir());
        Append(_configManager.GetRcloneMountDir());
        foreach (var item in candidates.OrderBy(item => item.Id))
        {
            Append(item.Id.ToString("D"));
            Append(item.Path);
            Append(item.SubType.ToString());
            Append(item.FileBlobId?.ToString("D"));
            Append(item.GeneratedStrmOutputRoot);
            Append(item.GeneratedStrmPath);
            Append(item.GeneratedStrmTarget);
            if (!linksByItem.TryGetValue(item.Id, out var links))
                continue;

            foreach (var link in links.OrderBy(link => link.LinkPath, StringComparer.Ordinal))
            {
                Append(link.LinkPath);
                Append(link.SymlinkOrStrmInfo switch
                {
                    SymlinkAndStrmUtil.SymlinkInfo symlink => symlink.TargetPath,
                    SymlinkAndStrmUtil.StrmInfo strm => strm.TargetUrl,
                    _ => null,
                });
            }
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static string IssuePreviewApproval(string fingerprint)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        lock (PreviewLock)
        {
            _previewApproval = new PreviewApproval(
                token,
                fingerprint,
                DateTimeOffset.UtcNow + PreviewLifetime);
        }

        return token;
    }

    private bool TryValidatePreviewApproval(string fingerprint, out string reason)
    {
        if (!_requirePreviewApproval)
        {
            reason = string.Empty;
            return true;
        }

        if (string.IsNullOrWhiteSpace(_previewToken))
        {
            reason = "Run and review a fresh dry run before cleanup.";
            return false;
        }

        lock (PreviewLock)
        {
            if (_previewApproval is null
                || !string.Equals(
                    _previewApproval.Token,
                    _previewToken,
                    StringComparison.Ordinal))
            {
                reason = "The dry-run approval is missing or was replaced; run the dry run again.";
                return false;
            }

            if (_previewApproval.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                reason = "The dry-run approval expired; run the dry run again.";
                return false;
            }

            if (!string.Equals(
                    _previewApproval.Fingerprint,
                    fingerprint,
                    StringComparison.Ordinal))
            {
                reason = "Payload or library-link state changed after the dry run; review a new dry run.";
                return false;
            }
        }

        reason = string.Empty;
        return true;
    }

    private static void ConsumePreviewApproval(string? previewToken)
    {
        if (string.IsNullOrWhiteSpace(previewToken))
            return;

        lock (PreviewLock)
        {
            if (string.Equals(_previewApproval?.Token, previewToken, StringComparison.Ordinal))
                _previewApproval = null;
        }
    }

    private static void ResetAudit()
    {
        lock (AuditLock)
            _auditLines = [];
    }

    private static void AppendAudit(string line)
    {
        lock (AuditLock)
            _auditLines.Add(line);
    }

    public static string GetAuditReport()
    {
        lock (AuditLock)
        {
            return _auditLines.Count > 0
                ? string.Join(Environment.NewLine, _auditLines)
                : "This list is empty.\nRun a dry run or cleanup first.";
        }
    }

    internal static void ClearAuditForTests() => ResetAudit();
    internal static void ClearPreviewApprovalForTests()
    {
        lock (PreviewLock)
            _previewApproval = null;
    }

    private sealed record ArrClientState(
        ArrClient Client,
        IReadOnlyList<ArrRootFolder>? RootFolders,
        string? Error);

    private sealed record LinkPlan(
        OrganizedLinksUtil.DavItemLink Link,
        ArrClient? Client,
        ArrMediaFileMatch? Match,
        string? BlockReason);

    private sealed record PreviewApproval(
        string Token,
        string Fingerprint,
        DateTimeOffset ExpiresAt);

    internal sealed class CleanupStats
    {
        public int Candidates { get; set; }
        public int LinkedFiles { get; set; }
        public int RemovedItems { get; set; }
        public int RemovedLinks { get; set; }
        public int RemovedSidecars { get; set; }
        public int RemovedDirectories { get; set; }
        public int SearchesRequested { get; set; }
        public int SearchesWithheld { get; set; }
        public int SearchesFailed { get; set; }
        public int SkippedItems { get; set; }
    }
}
