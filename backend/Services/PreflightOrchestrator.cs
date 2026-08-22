using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Extensions;
using NzbWebDAV.Utils;
using Serilog;

namespace NzbWebDAV.Services;

public class PreflightOrchestrator(
    ConfigManager configManager,
    PreflightCache preflightCache,
    PlaybackFastVerifier fastVerifier,
    NewznabRateLimiter rateLimiter,
    IndexerHitTracker hitTracker,
    LazyRarResolver lazyRarResolver,
    PreflightSessionRegistry sessionRegistry,
    CandidateNegativeCache negativeCache,
    WardenStore wardenStore,
    NzbFetchCoalescer nzbFetchCoalescer)
{
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(8);

    public void Start(
        string profileToken,
        string type,
        string id,
        IReadOnlyList<NzbResolutionCache.Candidate> candidates)
    {
        var mode = configManager.GetPreflightMode();
        if (mode == "off" || candidates.Count == 0) return;

#pragma warning disable CA2000 // session ownership transfers to the background task, which disposes it on completion
        var session = sessionRegistry.BeginSession(profileToken, type, id);
#pragma warning restore CA2000

        _ = Task.Run(async () =>
        {
            var sw = Stopwatch.StartNew();
            var prepared = 0;
            try
            {
                prepared = await PreflightAsync(mode, candidates, session.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Preflight cancelled: session ended or was superseded.
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                Log.Debug(e, "Preflight failed for {Type}/{Id}", type, id);
            }
            finally
            {
                session.Dispose();
                if (prepared > 0)
                    Log.Debug("Preflight readied {Count} candidate(s) for {Type}/{Id} in {Ms}ms (mode={Mode})",
                        prepared, type, id, sw.ElapsedMilliseconds, mode);
            }
        });
    }

    private async Task<int> PreflightAsync(
        string mode,
        IReadOnlyList<NzbResolutionCache.Candidate> candidates,
        CancellationToken ct)
    {
        var maxAttempts = Math.Min(configManager.GetPreflightMaxAttempts(), candidates.Count);
        var maxWait = TimeSpan.FromSeconds(configManager.GetPreflightIndexerMaxWaitSeconds());
        var indexers = configManager.GetIndexerConfig().Indexers
            .ToDictionary(x => x.Name, x => x);

        for (var i = 0; i < maxAttempts; i++)
        {
            if (ct.IsCancellationRequested) return 0;
            var ok = await PreflightCandidateAsync(mode, candidates[i], indexers, maxWait, ct).ConfigureAwait(false);
            if (ok) return i + 1;
        }
        return 0;
    }

    private async Task<bool> PreflightCandidateAsync(
        string mode,
        NzbResolutionCache.Candidate candidate,
        Dictionary<string, IndexerConfig.ConnectionDetails> indexers,
        TimeSpan maxWait,
        CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return false;

        var fp = WardenFingerprint.Compute(candidate.Size, candidate.Poster, candidate.UsenetDate);
        if (wardenStore.IsDeadAnywhere(fp)) return false;

        if (indexers.TryGetValue(candidate.IndexerName, out var indexer))
        {
            var hitCheck = await hitTracker
                .CheckAsync(candidate.IndexerName, IndexerApiHit.HitType.Download, indexer.DownloadLimit, indexer.HitLimitResetTime, ct)
                .ConfigureAwait(false);
            if (hitCheck is { Allowed: false })
            {
                Log.Debug("Preflight skipped for {Indexer}: {Reason}",
                    candidate.IndexerName, IndexerHitTracker.FormatSkipReason(hitCheck, IndexerApiHit.HitType.Download));
                return false;
            }

            var reserved = await rateLimiter
                .TryWaitAsync(candidate.IndexerName, indexer.MaxRequestsPerMinute, maxWait, ct)
                .ConfigureAwait(false);
            if (!reserved) return false;
        }

        var nzbBytes = await FetchNzbBytesAsync(candidate, ct).ConfigureAwait(false);
        if (nzbBytes is null || ct.IsCancellationRequested) return false;

        _ = hitTracker.RecordAsync(candidate.IndexerName, IndexerApiHit.HitType.Download, CancellationToken.None);

        PlaybackFastVerifier.VerifyOutcome outcome;
        using (var ms = new MemoryStream(nzbBytes, writable: false))
        {
            outcome = await fastVerifier
                .VerifyAsync(ms, "stat", configManager.GetPlayVerifySampleCount(), ct)
                .ConfigureAwait(false);
        }

        if (outcome.Verdict == PlaybackFastVerifier.Verdict.Dead)
        {
            negativeCache.MarkFailed(candidate.NzbUrl);
            wardenStore.MarkDead(fp);
            return false;
        }

        var bytesToCache = mode == "light" ? null : nzbBytes;
        preflightCache.SetVerified(candidate.NzbUrl, bytesToCache, outcome.Verdict, outcome.ResponderHost);

        if (mode == "full" && !ct.IsCancellationRequested)
        {
            await TryPreWarmExistingAsync(candidate, ct).ConfigureAwait(false);
        }

        return true;
    }

    private Task<byte[]?> FetchNzbBytesAsync(
        NzbResolutionCache.Candidate c,
        CancellationToken ct)
    {
        var skipTlsVerification = configManager.GetIndexerConfig().ShouldSkipTlsVerification(c.IndexerName);
        return nzbFetchCoalescer.GetOrFetchAsync(c.NzbUrl, c.ProxyUrl, skipTlsVerification, async innerCt =>
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, c.NzbUrl);
                req.Headers.TryAddWithoutValidation("User-Agent", c.IndexerUserAgent);
                var client = ProxyHttpClientPool.GetClient(c.ProxyUrl, skipTlsVerification);
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(innerCt);
                cts.CancelAfter(FetchTimeout);
                using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) return null;
                try
                {
                    return await HttpContentReadUtil
                        .ReadBoundedAsync(resp.Content, NzbFetchLimits.MaxResponseBytes, cts.Token)
                        .ConfigureAwait(false);
                }
                catch (NzbResponseTooLargeException e)
                {
                    Log.Warning(
                        "Preflight rejected oversized NZB response from {Url}. Limit: {Limit} bytes. Reason: {Reason}",
                        c.NzbUrl, e.MaxBytes, e.Message);
                    Log.Debug(e, "Preflight oversized NZB response stack");
                    return null;
                }
            }
#pragma warning disable CA2016 // CA2016: classify cancellation regardless of the ambient token -- forwarding it would misclassify cancellations from internal timeout/child tokens
            catch (Exception e) when (!e.IsCancellationException() && e is not OutOfMemoryException)
#pragma warning restore CA2016
            {
                Log.Debug("Preflight NZB fetch failed for {Url}: {Message}", c.NzbUrl, e.Message);
                return null;
            }
        }, ct);
    }

    private async Task TryPreWarmExistingAsync(NzbResolutionCache.Candidate candidate, CancellationToken ct)
    {
        try
        {
            var fileName = ProfileReleaseName.ToNzbFileName(candidate.Title);
            await using var ctx = new DavDatabaseContext();

            var historyId = await ctx.HistoryItems.AsNoTracking()
                .Where(h => h.FileName == fileName
                            && h.DownloadStatus == HistoryItem.DownloadStatusOption.Completed)
                .OrderByDescending(h => h.CreatedAt)
                .Select(h => (Guid?)h.Id)
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);
            if (historyId is null) return;

            var completedHistoryId = historyId.Value;
            var davItem = await ctx.Items.AsNoTracking()
                .Where(x => x.HistoryItemId == completedHistoryId
                            && x.Type == DavItem.ItemType.UsenetFile
                            && x.SubType == DavItem.ItemSubType.MultipartFile)
                .OrderByDescending(x => x.FileSize ?? 0)
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);
            if (davItem is null) return;

            var client = new DavDatabaseClient(ctx);
            var mpf = await client.GetDavMultipartFileAsync(davItem, ct).ConfigureAwait(false);
            if (mpf is null || !mpf.Metadata.IsLazy) return;
            if ((mpf.Metadata.PendingParts?.Length ?? 0) == 0) return;

            await lazyRarResolver.EnsureResolvedThroughAsync(mpf, long.MaxValue, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Log.Debug(e, "Preflight lazy pre-warm failed for {Title}", candidate.Title);
        }
    }

}
