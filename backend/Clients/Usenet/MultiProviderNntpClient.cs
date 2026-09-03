using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using NzbWebDAV.Clients.Usenet.Concurrency;
using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Clients.Usenet.Contexts;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Database.Models.Metrics;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models;
using NzbWebDAV.Services;
using NzbWebDAV.Services.Metrics;
using NzbWebDAV.Services.Observability;
using NzbWebDAV.Services.StreamTrace;
using NzbWebDAV.Streams;
using Serilog;
using Serilog.Context;
using UsenetSharp.Exceptions;
using UsenetSharp.Models;
using UsenetSharp.Streams;

namespace NzbWebDAV.Clients.Usenet;

public class MultiProviderNntpClient(
    List<MultiConnectionNntpClient> providers,
    ProviderUsageTracker? usageTracker = null,
    MetricsWriter? metricsWriter = null,
    ProviderBytesTracker? bytesTracker = null,
    Func<bool>? cascadeEnabled = null,
    Func<bool>? retryPrimaryOnMiss = null,
    StreamTraceBuffer? streamTrace = null,
    ActiveReadRegistry? activeReadRegistry = null,
    ArticleMissNegativeCache? articleMissCache = null,
    ConnectionPoolStats? connectionPoolStats = null,
    ConcurrentReadTracker? concurrentReadTracker = null
) : NntpClient, INntpConnectionStats
{
    private static readonly TimeSpan RecoveryProbeTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Max concurrent batch-failover BODY starts. Admission stays strictly ordered;
    /// this only bounds how many fallback walks may be in flight at once so sequential
    /// consumers cannot deadlock on an unbounded fan-out (see AGENTS.md).
    /// </summary>
    private const int MaxConcurrentFallbackStarts = 4;
    private readonly SemaphoreSlim _batchFallbackStartGate = new(MaxConcurrentFallbackStarts);
    public int InFlightConnections => providers.Sum(p => p.InFlightConnections);

    internal IReadOnlyList<MultiConnectionNntpClient> Providers => providers;

    public override Task PrewarmConnectionsAsync(
        int targetConnections,
        CancellationToken cancellationToken)
    {
        if (targetConnections <= 0)
            return Task.CompletedTask;

        var eligible = providers
            .Select((provider, index) => (Provider: provider, Index: index))
            .Where(item => item.Provider.ProviderType == ProviderType.Pooled)
            .Where(item => item.Provider.GetCircuitBreakerSnapshot().State == ProviderCircuitState.Closed)
            .Where(item => !IsOverLimit(item.Provider))
            .OrderBy(item => item.Provider.Priority)
            .ThenBy(item => item.Index)
            .ToArray();
        if (eligible.Length == 0)
            return Task.CompletedTask;

        var targets = AllocateConnectionTargets(
            eligible.Select(item => item.Provider.PrewarmConnectionCapacity).ToArray(),
            targetConnections);
        return Task.WhenAll(eligible.Select((item, index) =>
            item.Provider.PrewarmConnectionsAsync(targets[index], cancellationToken)));
    }

    internal static int[] AllocateConnectionTargets(
        IReadOnlyList<int> capacities,
        int targetConnections)
    {
        var result = new int[capacities.Count];
        var totalCapacity = capacities.Sum(capacity => Math.Max(0, capacity));
        var target = Math.Min(Math.Max(0, targetConnections), totalCapacity);
        if (target == 0)
            return result;

        var assigned = 0;
        var remainders = new (int Index, long Remainder)[capacities.Count];
        for (var index = 0; index < capacities.Count; index++)
        {
            var capacity = Math.Max(0, capacities[index]);
            var weighted = (long)target * capacity;
            result[index] = (int)(weighted / totalCapacity);
            assigned += result[index];
            remainders[index] = (index, weighted % totalCapacity);
        }

        foreach (var remainder in remainders
                     .OrderByDescending(item => item.Remainder)
                     .ThenBy(item => item.Index)
                     .Take(target - assigned))
        {
            result[remainder.Index]++;
        }
        return result;
    }

    /// <summary>
    /// Applies Streaming Priority odds to every provider's connection gate so a settings
    /// save re-arbitrates playback against maintenance without reconnecting providers.
    /// </summary>
    public void UpdateConnectionPriorityOdds(SemaphorePriorityOdds odds)
    {
        foreach (var provider in providers)
            provider.UpdatePriorityOdds(odds);
    }

    public IReadOnlyList<ProviderCircuitRuntimeSnapshot> GetProviderCircuitSnapshots()
    {
        return providers
            .Select(p => new ProviderCircuitRuntimeSnapshot(
                p.MetricsKey,
                p.Host,
                p.ProviderType,
                p.GetCircuitBreakerSnapshot()))
            .ToList();
    }

    public IReadOnlyList<ProviderConnectionSnapshot> GetProviderConnectionSnapshots()
    {
        return providers
            .Select(p => new ProviderConnectionSnapshot(
                p.MetricsKey,
                p.Host,
                p.ProviderType,
                p.LiveConnections,
                p.IdleConnections,
                p.ActiveConnections,
                p.AvailableConnections,
                p.PendingSelections,
                p.GetConnectionChurn(),
                p.LearnedConnectionLimit,
                p.MaxConnections,
                p.EffectiveMaxConnections,
                p.GetConnectionAdmissionSnapshot()))
            .ToList();
    }

    public async Task ProbeLatchedProvidersAsync(CancellationToken cancellationToken)
    {
        foreach (var provider in providers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (provider.ProviderType == ProviderType.Disabled ||
                provider.GetCircuitBreakerSnapshot().State != ProviderCircuitState.HalfOpen)
            {
                continue;
            }

            Log.Information(
                "Probing provider {Provider} after circuit-breaker cooldown.",
                provider.Host);

            using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            using var timeoutContext = CancellationTokenContext.SetContext(
                probeCts.Token,
                new StreamingTimeoutContext
                {
                    PerSegmentTimeout = RecoveryProbeTimeout,
                    MaxRetries = 0,
                });

            try
            {
                await provider.DateAsync(probeCts.Token).ConfigureAwait(false);
            }
            catch (NntpClientRetiredException e)
            {
                Log.Debug(
                    e,
                    "Stopped provider recovery probes because the NNTP client generation was retired.");
                return;
            }
            catch (Exception e) when (!e.IsCancellationException(cancellationToken) && e is not OutOfMemoryException)
            {
                Log.Debug(
                    e,
                    "Provider {Provider} recovery probe did not succeed.",
                    provider.Host);
            }
        }
    }

    private readonly ProviderUsageTracker _usageTracker = usageTracker ?? new ProviderUsageTracker();
    private static readonly AsyncLocal<Guid?> ReadSessionScope = new();
    internal static Guid? CurrentReadSessionId => ReadSessionScope.Value;

    private static readonly AsyncLocal<StreamTraceRangeContext?> StreamTraceRangeScope = new();
    internal static StreamTraceRangeContext? CurrentStreamTraceRange => StreamTraceRangeScope.Value;

    /// <summary>
    /// Tag the current async flow with a read-session id so SegmentFetch rows
    /// emitted while fulfilling this read can be correlated back to the session.
    /// Also pushes ReadSessionId into the Serilog LogContext for Debug logs.
    /// Disposing the returned scope restores the previous values.
    /// </summary>
    public static IDisposable BeginReadSessionScope(Guid readSessionId)
    {
        var previous = ReadSessionScope.Value;
        ReadSessionScope.Value = readSessionId;
        var logProp = LogContext.PushProperty("ReadSessionId", readSessionId);
        return new ScopeReleaser(() =>
        {
            logProp.Dispose();
            ReadSessionScope.Value = previous;
        });
    }

    /// <summary>
    /// Bind the exact <see cref="StreamTraceRangeContext"/> returned by RangeOpen to this
    /// async flow so overlapping ranges on the same read session keep independent stall
    /// attribution. Disposing restores the previous token.
    /// </summary>
    public static IDisposable BeginStreamTraceRangeScope(StreamTraceRangeContext? range)
    {
        var previous = StreamTraceRangeScope.Value;
        StreamTraceRangeScope.Value = range;
        return new ScopeReleaser(() => StreamTraceRangeScope.Value = previous);
    }

    private sealed class ScopeReleaser(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }

    private sealed class ProviderWalkSummary(int eligibleProviders)
    {
        public int EligibleProviders { get; } = eligibleProviders;
        public int Attempts { get; set; }
        public int CurrentDefinitiveMisses { get; set; }
        public int CachedSkips { get; set; }
        public int StorageGroupSkips { get; set; }
        public int Timeouts { get; set; }
        public int TransportFailures { get; set; }
        public int AuthFailures { get; set; }
        public int ProtocolFailures { get; set; }
        public int CorruptionFailures { get; set; }
        public int UnexpectedResponses { get; set; }
        public int OtherExceptions { get; set; }
        public bool Cancelled { get; set; }
        public bool Retired { get; set; }
        public bool LastOutcomeWasException { get; set; }
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        public TimeSpan Elapsed => _clock.Elapsed;

        public bool IsPureDefinitiveMiss =>
            EligibleProviders > 0
            && !Cancelled
            && !Retired
            && (CurrentDefinitiveMisses > 0 || CachedSkips > 0)
            && Timeouts == 0
            && TransportFailures == 0
            && AuthFailures == 0
            && ProtocolFailures == 0
            && CorruptionFailures == 0
            && UnexpectedResponses == 0
            && OtherExceptions == 0
            && !LastOutcomeWasException;

        public void NoteException(Exception ex)
        {
            var status = ClassifyException(ex);
            switch (status)
            {
                case SegmentFetch.FetchStatus.Missing:
                    CurrentDefinitiveMisses++;
                    break;
                case SegmentFetch.FetchStatus.Timeout:
                    Timeouts++;
                    LastOutcomeWasException = true;
                    break;
                case SegmentFetch.FetchStatus.Network:
                    TransportFailures++;
                    LastOutcomeWasException = true;
                    break;
                case SegmentFetch.FetchStatus.Auth:
                    AuthFailures++;
                    LastOutcomeWasException = true;
                    break;
                case SegmentFetch.FetchStatus.Protocol:
                    ProtocolFailures++;
                    LastOutcomeWasException = true;
                    break;
                case SegmentFetch.FetchStatus.Corrupt:
                    CorruptionFailures++;
                    LastOutcomeWasException = true;
                    break;
                default:
                    OtherExceptions++;
                    LastOutcomeWasException = true;
                    break;
            }
        }
    }

    // Per-call attribution. Caller (e.g. PlaybackFastVerifier) sets a mutable
    // holder on AttributionContext BEFORE invoking; we read it inside the call and
    // mutate Host on a non-"missing" response. AsyncLocal reliably flows the holder
    // reference DOWN to us; mutating its property is then visible to the caller via
    // their reference (which sidesteps AsyncLocal's child→parent non-propagation).
    public sealed class ResponderAttribution { public string? Host; }
    public static readonly AsyncLocal<ResponderAttribution?> AttributionContext = new();

    private readonly object _selectLock = new();

    public override Task ConnectAsync(string host, int port, bool useSsl, CancellationToken ct)
    {
        throw new NotSupportedException("Please connect within the connectionFactory");
    }

    public override Task<UsenetResponse> AuthenticateAsync(string user, string pass, CancellationToken ct)
    {
        throw new NotSupportedException("Please authenticate within the connectionFactory");
    }

    public override Task<UsenetStatResponse> StatAsync(SegmentId segmentId, CancellationToken cancellationToken)
    {
        return RunFromPoolWithBackup(
            x => x.StatAsync(segmentId, cancellationToken), segmentId, NntpOperation.Stat, cancellationToken);
    }

    public override Task<UsenetHeadResponse> HeadAsync(SegmentId segmentId, CancellationToken cancellationToken)
    {
        return RunFromPoolWithBackup(
            x => x.HeadAsync(segmentId, cancellationToken), segmentId, NntpOperation.Head, cancellationToken);
    }

    public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync
    (
        SegmentId segmentId,
        CancellationToken cancellationToken
    )
    {
        return RunFromPoolWithBackup(
            x => x.DecodedBodyAsync(segmentId, cancellationToken), segmentId, NntpOperation.Body, cancellationToken);
    }

    public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync
    (
        SegmentId segmentId,
        CancellationToken cancellationToken
    )
    {
        return RunFromPoolWithBackup(
            x => x.DecodedArticleAsync(segmentId, cancellationToken), segmentId, NntpOperation.Article, cancellationToken);
    }

    public override Task<UsenetDateResponse> DateAsync(CancellationToken cancellationToken)
    {
        return RunFromPoolWithBackup(
            x => x.DateAsync(cancellationToken), articleId: null, NntpOperation.Date, cancellationToken);
    }

    public override async Task<UsenetDecodedBodyResponse> DecodedBodyAsync
    (
        SegmentId segmentId,
        ArticleBodyCompletionHandler? onConnectionReadyAgain,
        CancellationToken cancellationToken
    )
    {
#pragma warning disable CA2000 // fetch scope is disposed on both the success and failure paths below
        var fetchScope = concurrentReadTracker?.BeginSegmentFetch(segmentId);
#pragma warning restore CA2000
        try
        {
            return await RunStreamingFromPoolWithBackup(
                (provider, callback) =>
                    provider.DecodedBodyAsync(segmentId, callback, cancellationToken),
                UsenetResponseType.ArticleRetrievedBodyFollows,
                segmentId,
                (result, failureReason) =>
                {
                    try
                    {
                        ArticleBodyCompletion.InvokeContained(onConnectionReadyAgain, result, failureReason);
                    }
                    finally
                    {
                        fetchScope?.Dispose();
                    }
                },
                NntpOperation.Body,
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            fetchScope?.Dispose();
            throw;
        }
    }

    public override async Task<UsenetDecodedBodyBatch> DecodedBodiesAsync
    (
        IReadOnlyList<SegmentId> segmentIds,
        ArticleBodyCompletionHandler? onConnectionReadyAgain,
        CancellationToken cancellationToken
    )
    {
        var fetchScopes = concurrentReadTracker is null
            ? []
            : segmentIds.Select(x => concurrentReadTracker.BeginSegmentFetch(x)).ToArray();

        void CompleteBatchFetches(ArticleBodyResult result, string? failureReason)
        {
            try
            {
                ArticleBodyCompletion.InvokeContained(onConnectionReadyAgain, result, failureReason);
            }
            finally
            {
                foreach (var fetchScope in fetchScopes)
                    fetchScope.Dispose();
            }
        }

        try
        {
            return await DecodedBodiesCoreAsync().ConfigureAwait(false);
        }
        catch
        {
            foreach (var fetchScope in fetchScopes)
                fetchScope.Dispose();
            throw;
        }

        async Task<UsenetDecodedBodyBatch> DecodedBodiesCoreAsync()
        {
            ExceptionDispatchInfo? lastException = null;
            var orderedProviders = SelectOrderedProviders(NntpOperation.PipelinedBody, out var reserved);
            using var releasePending = new ScopeReleaser(
                () => reserved?.ReleasePending(NntpOperation.PipelinedBody));
            for (var providerIndex = 0; providerIndex < orderedProviders.Count; providerIndex++)
            {
                var provider = orderedProviders[providerIndex];
                var deferredCallback = new DeferredArticleBodyCallback();
                UsenetDecodedBodyBatch? primaryBatch = null;
                ContextualCancellationTokenSource? attemptCts = null;
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    attemptCts = ContextualCancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    primaryBatch = await provider.DecodedBodiesAsync(
                        segmentIds, deferredCallback.Invoke, attemptCts.Token).ConfigureAwait(false);
                    if (primaryBatch.Responses.Count != segmentIds.Count)
                    {
                        throw new InvalidOperationException(
                            $"Pipelined BODY returned {primaryBatch.Responses.Count} responses for {segmentIds.Count} requests.");
                    }

                    var coordinator = new BatchCallbackCoordinator(
                        primaryBatch.Responses.Count, CompleteBatchFetches);
                    deferredCallback.Activate(coordinator.CompleteTransfer);
                    var fallbackProviders = orderedProviders
                        .Skip(providerIndex + 1)
                        .ToArray();
                    var rawResponses =
                        new Task<UsenetDecodedBodyResponse>[primaryBatch.Responses.Count];
                    // Admission (start-order) is separate from transfer completion so segment
                    // N+1 can begin its fallback walk after N has admitted/started, without
                    // waiting for N's body stream to finish. Concurrent starts are bounded by
                    // _batchFallbackStartGate until each transfer's body callback fires.
                    Task previousFallbackAdmission = Task.CompletedTask;
                    for (var index = 0; index < rawResponses.Length; index++)
                    {
                        var fallbackAdmission = new TaskCompletionSource(
                            TaskCreationOptions.RunContinuationsAsynchronously);
#pragma warning disable CA2025 // batch response tasks intentionally outlive this scope: releasePending only returns the pending-admission reservation, while in-flight transfers hold (and release via completion callbacks) their own per-provider connection locks
                        rawResponses[index] = ResolveBatchResponseAsync(
                            primaryBatch.Responses[index],
                            segmentIds[index],
                            provider,
                            fallbackProviders,
                            previousFallbackAdmission,
                            fallbackAdmission,
                            coordinator,
                            cancellationToken);
#pragma warning restore CA2025
                        previousFallbackAdmission = fallbackAdmission.Task;
                    }

                    var output = CreateBatchOutputSources(rawResponses.Length);
                    var publicResponses = new Task<UsenetDecodedBodyResponse>[output.Length];
                    for (var index = 0; index < output.Length; index++)
                        publicResponses[index] = output[index].Task;
                    var publisher = OrderedBatchResponsePublisher.PublishAsync(rawResponses, output);
                    var ownedCts = attemptCts;
                    attemptCts = null;
#pragma warning disable CA2025 // Completion owns the attempt token until transport, coordinator, and publication finish
                    return new UsenetDecodedBodyBatch
                    {
                        Responses = publicResponses,
                        Completion = CompleteOwnedBatchAsync(
                            primaryBatch.Completion,
                            coordinator.Completion,
                            publisher,
                            ownedCts),
                    };
#pragma warning restore CA2025
                }
                catch (NntpClientRetiredException)
                {
                    deferredCallback.Discard();
                    await AbandonProviderAttemptAsync(primaryBatch, attemptCts).ConfigureAwait(false);
                    // Every provider in this client belongs to the same retired generation.
                    // Do not walk the remaining disposed pools or record network failures.
                    ArticleBodyCompletion.InvokeContained(
                        CompleteBatchFetches, ArticleBodyResult.NotRetrieved);
                    throw;
                }
                catch (Exception e) when (e.TryGetCausingException(out UsenetArticleNotFoundException? _) && e is not OutOfMemoryException)
                {
                    deferredCallback.Discard();
                    await AbandonProviderAttemptAsync(primaryBatch, attemptCts).ConfigureAwait(false);
                    // Invalid / permanently missing segment ids are invalid on every provider.
                    ArticleBodyCompletion.InvokeContained(
                        CompleteBatchFetches, ArticleBodyResult.NotRetrieved);
                    throw;
                }
                catch (Exception e) when (!e.IsCancellationException(cancellationToken) && e is not OutOfMemoryException)
                {
                    deferredCallback.Discard();
                    await AbandonProviderAttemptAsync(primaryBatch, attemptCts).ConfigureAwait(false);
                    lastException = ExceptionDispatchInfo.Capture(e);
                }
                catch
                {
                    deferredCallback.Discard();
                    await AbandonProviderAttemptAsync(primaryBatch, attemptCts).ConfigureAwait(false);
                    ArticleBodyCompletion.InvokeContained(
                        CompleteBatchFetches, ArticleBodyResult.NotRetrieved);
                    throw;
                }
            }

            ArticleBodyCompletion.InvokeContained(CompleteBatchFetches, ArticleBodyResult.NotRetrieved);
            lastException?.Throw();
            throw new InvalidOperationException("There are no usenet providers configured.");
        }
    }

    private async Task<UsenetDecodedBodyResponse> ResolveBatchResponseAsync(
        Task<UsenetDecodedBodyResponse> primaryResponse,
        SegmentId segmentId,
        MultiConnectionNntpClient primaryProvider,
        MultiConnectionNntpClient[] fallbackProviders,
        Task previousFallbackAdmission,
        TaskCompletionSource fallbackAdmission,
        BatchCallbackCoordinator coordinator,
        CancellationToken cancellationToken)
    {
        var admissionSignaled = false;
        void SignalAdmission()
        {
            if (admissionSignaled) return;
            admissionSignaled = true;
            fallbackAdmission.TrySetResult();
        }

        var primaryTraceRange = CurrentStreamTraceRange;
        var primaryStopwatch = Stopwatch.StartNew();
        List<(string Host, SegmentFetch.FetchStatus Reason)>? priorMisses = null;
        var walk = new ProviderWalkSummary(1 + fallbackProviders.Length);
        MultiConnectionNntpClient? lastAttemptedProvider = primaryProvider;
        // Fresh per article resolution. When primary re-probe is enabled, do not mark the
        // primary's storage group on the initial batch 430 so that re-probe is not skipped
        // by its own miss. Cross-request negative cache may skip re-probe separately.
        var missingGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            UsenetDecodedBodyResponse? response = null;
            ExceptionDispatchInfo? lastException = null;
            try
            {
                response = await primaryResponse.ConfigureAwait(false);
            }
            catch (NntpClientRetiredException)
            {
                walk.Retired = true;
                throw;
            }
            catch (Exception e) when (!e.IsCancellationException(cancellationToken) && e is not OutOfMemoryException)
            {
                primaryStopwatch.Stop();
                walk.Attempts++;
                walk.NoteException(e);
                var reason = ClassifyAndRecordFailure(
                    primaryProvider.MetricsKey, e, primaryStopwatch.ElapsedMilliseconds, 0,
                    primaryTraceRange, NntpOperation.PipelinedBody, segmentId);
                (priorMisses ??= []).Add((primaryProvider.MetricsKey, reason));
                lastException = ExceptionDispatchInfo.Capture(e);
            }

            if (response?.ResponseType == UsenetResponseType.ArticleRetrievedBodyFollows)
            {
                primaryStopwatch.Stop();
                _usageTracker.RecordSuccess(primaryProvider.MetricsKey);
                RecordFetch(primaryProvider.MetricsKey, SegmentFetch.FetchStatus.Ok,
                    primaryStopwatch.ElapsedMilliseconds, 0, primaryTraceRange);
                return WrapProviderResponse(response, primaryProvider.MetricsKey);
            }

            var definitiveMiss = response != null &&
                UsenetArticleAvailability.IsDefinitiveMissing(response);
            if (definitiveMiss)
            {
                walk.Attempts++;
                walk.CurrentDefinitiveMisses++;
                primaryStopwatch.Stop();
                RecordFetch(primaryProvider.MetricsKey, SegmentFetch.FetchStatus.Missing,
                    primaryStopwatch.ElapsedMilliseconds, 0, primaryTraceRange);
                (priorMisses ??= []).Add((primaryProvider.MetricsKey, SegmentFetch.FetchStatus.Missing));
            }
            else if (response != null)
            {
                walk.Attempts++;
                walk.UnexpectedResponses++;
            }

            // Re-probe primary once on a definitive miss when enabled (default). Multi-node
            // spool routing can return a transient 430/451 on one connection. Operators may
            // disable via usenet.cascade.retry-primary-on-miss; most connection-level
            // failures also re-try the primary once.
            //
            // Exhausted streaming/read timeouts are different: MultiConnectionNntpClient
            // already spent the per-segment retry budget on this provider. Re-probing it
            // before backups only burns more playback time (#723). When no fallbacks exist,
            // keep the primary in the retry list so a solo provider can still recover via
            // a singular BODY.
            //
            // Coherence with ArticleMissNegativeCache:
            // - Never MarkMissing the primary on this initial batch 430 — that would prime
            //   the cache and cause the intentional re-probe below to skip itself.
            // - If a prior request already cached the primary/group miss, skip the re-probe
            //   and walk fallbacks immediately (that is the point of cross-request caching).
            // - MarkMissing only from definitive misses inside the retry/fallback loop below.
            IReadOnlyList<MultiConnectionNntpClient> retryProviders;
            var primaryCachedMiss = IsCachedMissing(segmentId, primaryProvider);
            var exhaustedTimeout = lastException != null
                && lastException.SourceException.TryGetCausingException<TimeoutException>(out _);
            var reprobePrimary = !definitiveMiss
                || (retryPrimaryOnMiss?.Invoke() != false && !primaryCachedMiss);
            if ((exhaustedTimeout && fallbackProviders.Length > 0)
                || (definitiveMiss && !reprobePrimary))
            {
                var primaryGroup = NormalizeStorageGroup(primaryProvider.StorageGroup);
                if (primaryGroup.Length > 0 && definitiveMiss) missingGroups.Add(primaryGroup);
                retryProviders = fallbackProviders;
            }
            else
            {
                retryProviders = [primaryProvider, .. fallbackProviders];
                if ((response == null || !definitiveMiss) && response != null)
                {
                    lastException = ExceptionDispatchInfo.Capture(
                        new UsenetUnexpectedResponseException(segmentId, response.ResponseMessage));
                }
            }

            await previousFallbackAdmission.WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            await _batchFallbackStartGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            var gateHeld = true;
            var gateOwnedByTransfer = false;
            try
            {
                // Admit the next segment before (or while) walking providers so N+1 is not
                // blocked on this segment's body stream — only on ordered start + the gate.
                SignalAdmission();
                foreach (var provider in retryProviders)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var group = NormalizeStorageGroup(provider.StorageGroup);
                    if (group.Length > 0 && missingGroups.Contains(group))
                    {
                        walk.StorageGroupSkips++;
                        Log.Debug(
                            "Skipping provider `{Host}` on storage group `{Group}` — " +
                            "a sibling provider already reported the article missing.",
                            provider.Host, group);
                        continue;
                    }

                    if (IsCachedMissing(segmentId, provider))
                    {
                        walk.CachedSkips++;
                        Log.Debug(
                            "Skipping provider `{Host}` for article `{SegmentId}` — " +
                            "cached as missing. Reason: article-miss-cache",
                            provider.Host, segmentId);
                        continue;
                    }

                    coordinator.AddTransfer();
                    var deferredCallback = new DeferredArticleBodyCallback();
                    var traceRange = CurrentStreamTraceRange;
                    var stopwatch = Stopwatch.StartNew();
                    lastAttemptedProvider = provider;
                    try
                    {
                        walk.Attempts++;
                        response = await provider.DecodedBodyAsync(
                            segmentId, deferredCallback.Invoke, cancellationToken).ConfigureAwait(false);
                        stopwatch.Stop();
                        var responseType = response.ResponseType;
                        if (responseType == UsenetResponseType.ArticleRetrievedBodyFollows)
                        {
                            _usageTracker.RecordSuccess(provider.MetricsKey);
                            RecordSuccessfulFetch(
                                provider.MetricsKey, SegmentFetch.FetchStatus.Ok,
                                stopwatch.ElapsedMilliseconds, priorMisses?.Count ?? 0,
                                traceRange, priorMisses);
                            response = WrapProviderResponse(response, provider.MetricsKey);
                            gateOwnedByTransfer = true;
                            deferredCallback.Activate((result, failureReason) =>
                            {
                                try
                                {
                                    coordinator.CompleteTransfer(result, failureReason);
                                }
                                finally
                                {
                                    _batchFallbackStartGate.Release();
                                }
                            });
                        }
                        else
                        {
                            RecordFetch(provider.MetricsKey, SegmentFetch.FetchStatus.Missing,
                                stopwatch.ElapsedMilliseconds, priorMisses?.Count ?? 0, traceRange);
                            (priorMisses ??= []).Add((provider.MetricsKey, SegmentFetch.FetchStatus.Missing));
                            if (UsenetArticleAvailability.IsDefinitiveMissing(response))
                            {
                                walk.CurrentDefinitiveMisses++;
                                if (group.Length > 0) missingGroups.Add(group);
                                MarkCachedMissing(segmentId, provider);
                            }
                            deferredCallback.Discard();
                            coordinator.CompleteAttempt();
                        }

                        lastException = null;
                    }
                    catch (NntpClientRetiredException)
                    {
                        walk.Retired = true;
                        // The whole provider set belongs to the retired generation.
                        deferredCallback.Discard();
                        coordinator.CompleteAttempt();
                        throw;
                    }
                    catch (Exception e) when (!e.IsCancellationException(cancellationToken) && e is not OutOfMemoryException)
                    {
                        stopwatch.Stop();
                        walk.NoteException(e);
                        MarkCachedMissingOnThrownMiss(e, segmentId, provider, missingGroups);
                        var reason = ClassifyAndRecordFailure(
                            provider.MetricsKey, e, stopwatch.ElapsedMilliseconds,
                            priorMisses?.Count ?? 0, traceRange,
                            NntpOperation.PipelinedBody, segmentId);
                        (priorMisses ??= []).Add((provider.MetricsKey, reason));
                        deferredCallback.Discard();
                        coordinator.CompleteAttempt();
                        lastException = ExceptionDispatchInfo.Capture(e);
                        continue;
                    }
                    catch
                    {
                        deferredCallback.Discard();
                        coordinator.CompleteAttempt();
                        throw;
                    }

                    if (response.ResponseType == UsenetResponseType.ArticleRetrievedBodyFollows)
                    {
                        return response;
                    }
                }
            }
            finally
            {
                if (gateHeld && !gateOwnedByTransfer)
                    _batchFallbackStartGate.Release();
            }

            walk.LastOutcomeWasException = lastException is not null
                && ClassifyException(lastException.SourceException) != SegmentFetch.FetchStatus.Missing;
            LogProviderWalkOutcome(
                walk,
                segmentId,
                NntpOperation.PipelinedBody,
                lastAttemptedProvider.Host,
                lastException?.SourceException);
            lastException?.Throw();
            throw new UsenetArticleNotFoundException(segmentId, response?.ResponseMessage);
        }
        catch
        {
            coordinator.MarkResolutionFailure();
            throw;
        }
        finally
        {
            SignalAdmission();
            coordinator.CompleteDecision();
        }
    }

    private static TaskCompletionSource<UsenetDecodedBodyResponse>[] CreateBatchOutputSources(int count)
    {
        var output = new TaskCompletionSource<UsenetDecodedBodyResponse>[count];
        for (var index = 0; index < count; index++)
        {
            output[index] = new TaskCompletionSource<UsenetDecodedBodyResponse>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }

        return output;
    }

    private static async Task CompleteOwnedBatchAsync(
        Task transportCompletion,
        Task coordinatorCompletion,
        Task publisher,
        ContextualCancellationTokenSource owner)
    {
        try
        {
            await BatchLifecycle.ObserveAllAsync(
                    transportCompletion, coordinatorCompletion, publisher)
                .ConfigureAwait(false);
        }
        finally
        {
            owner.Dispose();
        }
    }

    private static async Task AbandonProviderAttemptAsync(
        UsenetDecodedBodyBatch? batch,
        ContextualCancellationTokenSource? owner)
    {
        if (batch is not null && owner is not null)
        {
            await DecodedBodyBatchCleanup.AbandonAsync(batch, owner).ConfigureAwait(false);
            return;
        }

        owner?.Dispose();
    }

    private sealed class BatchCallbackCoordinator(
        int responseCount,
        ArticleBodyCompletionHandler? callback)
    {
        private int _remaining = responseCount + 1;
        private int _transportFailed;
        private int _resolutionFailed;
        private int _callbackInvoked;
        private string? _firstFailureReason;

        public void AddTransfer()
        {
            Interlocked.Increment(ref _remaining);
        }

        public void CompleteTransfer(ArticleBodyResult result, string? failureReason = null)
        {
            if (result == ArticleBodyResult.NotRetrieved)
            {
                Volatile.Write(ref _transportFailed, 1);
                Interlocked.CompareExchange(ref _firstFailureReason, failureReason, null);
            }
            else if (result == ArticleBodyResult.Cancelled)
            {
                MarkResolutionFailure();
            }

            CompleteOne();
        }

        public void CompleteDecision()
        {
            CompleteOne();
        }

        public void CompleteAttempt()
        {
            CompleteOne();
        }

        public void MarkResolutionFailure()
        {
            Volatile.Write(ref _resolutionFailed, 1);
        }

        private void CompleteOne()
        {
            if (Interlocked.Decrement(ref _remaining) != 0)
                return;

            if (Interlocked.Exchange(ref _callbackInvoked, 1) != 0)
            {
                _completion.TrySetResult();
                return;
            }

            try
            {
                var failed = Volatile.Read(ref _transportFailed) != 0 ||
                             Volatile.Read(ref _resolutionFailed) != 0;
                ArticleBodyCompletion.InvokeContained(
                    callback,
                    failed ? ArticleBodyResult.NotRetrieved : ArticleBodyResult.Retrieved,
                    failed ? Volatile.Read(ref _firstFailureReason) : null);
            }
            finally
            {
                _completion.TrySetResult();
            }
        }

        public Task Completion => _completion.Task;

        private readonly TaskCompletionSource _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public override async Task<UsenetDecodedArticleResponse> DecodedArticleAsync
    (
        SegmentId segmentId,
        ArticleBodyCompletionHandler? onConnectionReadyAgain,
        CancellationToken cancellationToken
    )
    {
        return await RunStreamingFromPoolWithBackup(
            (provider, callback) =>
                provider.DecodedArticleAsync(segmentId, callback, cancellationToken),
            UsenetResponseType.ArticleRetrievedHeadAndBodyFollow,
            segmentId,
            onConnectionReadyAgain,
            NntpOperation.Article,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<T> RunStreamingFromPoolWithBackup<T>(
        Func<INntpClient, ArticleBodyCompletionHandler, Task<T>> task,
        UsenetResponseType successResponseType,
        SegmentId segmentId,
        ArticleBodyCompletionHandler? onConnectionReadyAgain,
        NntpOperation operation,
        CancellationToken cancellationToken)
        where T : UsenetResponse
    {
        var attribution = AttributionContext.Value;
        if (attribution != null) attribution.Host = null;
        ExceptionDispatchInfo? lastException = null;
        T? lastNoArticleResult = null;
        var lastOutcomeWasException = false;
        List<(string Host, SegmentFetch.FetchStatus Reason)>? priorMisses = null;
        var missingGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var orderedProviders = SelectOrderedProviders(operation, out var reserved);
        using var releasePending = new ScopeReleaser(() => reserved?.ReleasePending(operation));
        var walk = new ProviderWalkSummary(orderedProviders.Count);
        MultiConnectionNntpClient? lastAttemptedProvider = null;
        var attemptIndex = 0;
        foreach (var provider in orderedProviders)
        {
            var group = NormalizeStorageGroup(provider.StorageGroup);
            if (group.Length > 0 && missingGroups.Contains(group))
            {
                walk.StorageGroupSkips++;
                Log.Debug(
                    "Skipping provider `{Host}` on storage group `{Group}` — " +
                    "a sibling provider already reported the article missing.",
                    provider.Host, group);
                continue;
            }

            if (IsCachedMissing(segmentId, provider))
            {
                walk.CachedSkips++;
                Log.Debug(
                    "Skipping provider `{Host}` for article `{SegmentId}` — " +
                    "cached as missing. Reason: article-miss-cache",
                    provider.Host, segmentId);
                continue;
            }

            var deferredCallback = new DeferredArticleBodyCallback();
            var traceRange = CurrentStreamTraceRange;
            var stopwatch = Stopwatch.StartNew();
            lastAttemptedProvider = provider;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                walk.Attempts++;
                var result = await task(provider, deferredCallback.Invoke)
                    .ConfigureAwait(false);
                stopwatch.Stop();
                if (result.ResponseType == successResponseType)
                {
                    if (attribution != null) attribution.Host = provider.Host;
                    _usageTracker.RecordSuccess(provider.MetricsKey);
                    RecordSuccessfulFetch(
                        provider.MetricsKey, SegmentFetch.FetchStatus.Ok,
                        stopwatch.ElapsedMilliseconds, attemptIndex, traceRange, priorMisses);
                    result = WrapProviderResponse(result, provider.MetricsKey);
                    deferredCallback.Activate(onConnectionReadyAgain ?? ((_, _) => { }));
                    return result;
                }

                deferredCallback.Discard();
                if (UsenetArticleAvailability.IsDefinitiveMissing(result))
                {
                    walk.CurrentDefinitiveMisses++;
                    RecordFetch(provider.MetricsKey, SegmentFetch.FetchStatus.Missing,
                        stopwatch.ElapsedMilliseconds, attemptIndex, traceRange);
                    (priorMisses ??= []).Add((provider.MetricsKey, SegmentFetch.FetchStatus.Missing));
                    lastNoArticleResult = result;
                    lastOutcomeWasException = false;
                    if (group.Length > 0) missingGroups.Add(group);
                    MarkCachedMissing(segmentId, provider);
                    attemptIndex++;
                    continue;
                }

                walk.UnexpectedResponses++;
                RecordFetch(provider.MetricsKey, SegmentFetch.FetchStatus.Missing,
                    stopwatch.ElapsedMilliseconds, attemptIndex, traceRange);
                ArticleBodyCompletion.InvokeContained(
                    onConnectionReadyAgain, ArticleBodyResult.NotRetrieved);
                return result;
            }
            catch (NntpClientRetiredException)
            {
                walk.Retired = true;
                deferredCallback.Discard();
                ArticleBodyCompletion.InvokeContained(
                    onConnectionReadyAgain, ArticleBodyResult.NotRetrieved);
                throw;
            }
            catch (Exception e) when (!e.IsCancellationException(cancellationToken) && e is not OutOfMemoryException)
            {
                stopwatch.Stop();
                walk.NoteException(e);
                MarkCachedMissingOnThrownMiss(e, segmentId, provider, missingGroups);
                var reason = ClassifyAndRecordFailure(
                    provider.MetricsKey, e, stopwatch.ElapsedMilliseconds, attemptIndex,
                    traceRange, operation, segmentId);
                (priorMisses ??= []).Add((provider.MetricsKey, reason));
                deferredCallback.Discard();
                lastException = ExceptionDispatchInfo.Capture(e);
                lastOutcomeWasException = ClassifyException(e) != SegmentFetch.FetchStatus.Missing;
                attemptIndex++;
            }
            catch
            {
                walk.Cancelled = cancellationToken.IsCancellationRequested;
                deferredCallback.Discard();
                ArticleBodyCompletion.InvokeContained(
                    onConnectionReadyAgain, ArticleBodyResult.NotRetrieved);
                throw;
            }
        }

        // Terminal 430 after skips/exhaustion must fire the completion callback exactly once.
        ArticleBodyCompletion.InvokeContained(onConnectionReadyAgain, ArticleBodyResult.NotRetrieved);
        walk.LastOutcomeWasException = lastOutcomeWasException;
        LogProviderWalkOutcome(
            walk, segmentId, operation, lastAttemptedProvider?.Host, lastException?.SourceException);
        if (lastOutcomeWasException) lastException!.Throw();
        if (lastNoArticleResult is not null) return lastNoArticleResult;
        if (orderedProviders.Count == 0)
            throw new InvalidOperationException("There are no usenet providers configured.");
        lastException?.Throw();
        // All providers were skipped (negative cache / storage-group) without a probe.
        throw new UsenetArticleNotFoundException(segmentId.ToString()!);
    }

    private async Task<T> RunFromPoolWithBackup<T>
    (
        Func<INntpClient, Task<T>> task,
        SegmentId? articleId,
        NntpOperation operation,
        CancellationToken cancellationToken
    ) where T : UsenetResponse
    {
        var attribution = AttributionContext.Value;
        if (attribution != null) attribution.Host = null;
        ExceptionDispatchInfo? lastException = null;
        T? lastNoArticleResult = null;
        var lastOutcomeWasException = false;
        MultiConnectionNntpClient? lastAttemptedProvider = null;
        List<(string Host, SegmentFetch.FetchStatus Reason)>? priorMisses = null;
        var missingGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var orderedProviders = SelectOrderedProviders(operation, out var reserved);
        using var releasePending = new ScopeReleaser(() => reserved?.ReleasePending(operation));
        var walk = new ProviderWalkSummary(orderedProviders.Count);
        var attemptIndex = 0;
        foreach (var provider in orderedProviders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var group = NormalizeStorageGroup(provider.StorageGroup);
            if (group.Length > 0 && missingGroups.Contains(group))
            {
                walk.StorageGroupSkips++;
                Log.Debug(
                    "Skipping provider `{Host}` on storage group `{Group}` — " +
                    "a sibling provider already reported the article missing.",
                    provider.Host, group);
                continue;
            }

            if (articleId is { } segmentId && IsCachedMissing(segmentId, provider))
            {
                walk.CachedSkips++;
                Log.Debug(
                    "Skipping provider `{Host}` for article `{SegmentId}` — " +
                    "cached as missing. Reason: article-miss-cache",
                    provider.Host, segmentId);
                continue;
            }

            if (lastException is not null && lastAttemptedProvider is not null)
            {
                var msg = lastException.SourceException.Message;
                Log.Information(
                    "Provider {FailedProvider} error: {ErrorMessage}. Falling back to {NextProvider}",
                    lastAttemptedProvider.Host,
                    msg,
                    provider.Host);
            }

            lastAttemptedProvider = provider;
            var traceRange = CurrentStreamTraceRange;
            var stopwatch = Stopwatch.StartNew();
            try
            {
                walk.Attempts++;
                var result = await task.Invoke(provider).ConfigureAwait(false);
                stopwatch.Stop();

                // if no article with that message-id is found, try again with the next provider.
                // Only a definitive miss (430 / provider 451) marks the storage group missing —
                // never a connection error.
                if (UsenetArticleAvailability.IsDefinitiveMissing(result))
                {
                    walk.CurrentDefinitiveMisses++;
                    RecordFetch(provider.MetricsKey, SegmentFetch.FetchStatus.Missing,
                        stopwatch.ElapsedMilliseconds, attemptIndex, traceRange);
                    (priorMisses ??= new()).Add((provider.MetricsKey, SegmentFetch.FetchStatus.Missing));
                    lastNoArticleResult = result;
                    lastOutcomeWasException = false;
                    if (group.Length > 0) missingGroups.Add(group);
                    if (articleId is { } missId) MarkCachedMissing(missId, provider);
                    attemptIndex++;
                    continue;
                }

                // attribute the response to this provider, unless it was a "missing" hit
                // from the last provider (in which case nobody actually answered).
                if (attribution != null)
                    attribution.Host = provider.Host;

                // record per-queue-item attribution only for bytes-bearing responses (BODY/ARTICLE).
                if (result is UsenetDecodedBodyResponse or UsenetDecodedArticleResponse
                    && result.ResponseType is UsenetResponseType.ArticleRetrievedBodyFollows
                                          or UsenetResponseType.ArticleRetrievedHeadAndBodyFollow)
                {
                    _usageTracker.RecordSuccess(provider.MetricsKey);
                    RecordSuccessfulFetch(
                        provider.MetricsKey, SegmentFetch.FetchStatus.Ok,
                        stopwatch.ElapsedMilliseconds, attemptIndex, traceRange, priorMisses);
                    result = WrapProviderResponse(result, provider.MetricsKey);
                }
                else if (result is UsenetDecodedBodyResponse or UsenetDecodedArticleResponse)
                {
                    // BODY/ARTICLE response with an unexpected (non-success, non-430) response type.
                    walk.UnexpectedResponses++;
                    RecordFetch(provider.MetricsKey, SegmentFetch.FetchStatus.Missing,
                        stopwatch.ElapsedMilliseconds, attemptIndex, traceRange);
                }
                // STAT/HEAD/DATE successes: intentionally no SegmentFetch row (not a segment transfer;
                // matches StatsPipelinedAsync which records nothing).

                return result;
            }
            catch (NntpClientRetiredException)
            {
                walk.Retired = true;
                throw;
            }
            catch (Exception e) when (!e.IsCancellationException(cancellationToken) && e is not OutOfMemoryException)
            {
                stopwatch.Stop();
                walk.NoteException(e);
                MarkCachedMissingOnThrownMiss(e, articleId, provider, missingGroups);
                var reason = ClassifyAndRecordFailure(
                    provider.MetricsKey, e, stopwatch.ElapsedMilliseconds, attemptIndex,
                    traceRange, operation, articleId);
                (priorMisses ??= new()).Add((provider.MetricsKey, reason));
                lastException = ExceptionDispatchInfo.Capture(e);
                lastOutcomeWasException = ClassifyException(e) != SegmentFetch.FetchStatus.Missing;
                attemptIndex++;
            }
        }

        // Whichever terminal outcome occurred on the last attempted provider wins,
        // matching the original fallback precedence (a later connection error beats
        // an earlier 430, and a later 430 beats an earlier error).
        walk.LastOutcomeWasException = lastOutcomeWasException;
        LogProviderWalkOutcome(
            walk, articleId, operation, lastAttemptedProvider?.Host, lastException?.SourceException);
        if (lastOutcomeWasException)
            lastException!.Throw();
        if (lastNoArticleResult is not null) return lastNoArticleResult;
        if (orderedProviders.Count == 0)
            throw new InvalidOperationException("There are no usenet providers configured.");
        lastException?.Throw();
        // All providers were skipped (negative cache / storage-group) without a probe.
        if (articleId is { } exhaustedId)
            throw new UsenetArticleNotFoundException(exhaustedId.ToString()!);
        throw new InvalidOperationException("There are no usenet providers configured.");
    }

    private bool IsCachedMissing(SegmentId segmentId, MultiConnectionNntpClient provider)
    {
        if (articleMissCache == null) return false;
        return articleMissCache.IsMissing(CacheKey(segmentId, provider));
    }

    private void MarkCachedMissing(SegmentId segmentId, MultiConnectionNntpClient provider)
    {
        articleMissCache?.MarkMissing(CacheKey(segmentId, provider));
    }

    /// <summary>
    /// Production <see cref="BaseNntpClient"/> throws <see cref="UsenetArticleNotFoundException"/>
    /// on 430 instead of returning a response object. The response-object path already
    /// marks the miss cache; this keeps the throw path in the retry/fallback loop
    /// consistent. The initial batch primary 430 must not mark, so the intentional
    /// re-probe is not skipped by its own cache entry.
    /// </summary>
    private void MarkCachedMissingOnThrownMiss(
        Exception exception,
        SegmentId? segmentId,
        MultiConnectionNntpClient provider,
        HashSet<string> missingGroups)
    {
        if (segmentId is not { } id) return;
        if (ClassifyException(exception) != SegmentFetch.FetchStatus.Missing) return;
        var group = NormalizeStorageGroup(provider.StorageGroup);
        if (group.Length > 0) missingGroups.Add(group);
        MarkCachedMissing(id, provider);
    }

    private static string CacheKey(SegmentId segmentId, MultiConnectionNntpClient provider) =>
        ArticleMissNegativeCache.BuildKey(segmentId.ToString()!, provider.MetricsKey, provider.StorageGroup);

    private static void LogProviderWalkOutcome(
        ProviderWalkSummary walk,
        SegmentId? segmentId,
        NntpOperation operation,
        string? lastHost,
        Exception? lastException)
    {
        try
        {
            if (segmentId is null)
                return;

            if (walk.IsPureDefinitiveMiss)
            {
                var fileName = FetchAttributionContext.Current?.FileName;
                if (!ZeroFillLogLimiter.TryLog(fileName, out var suppressed))
                    return;

                if (suppressed > 0)
                {
                    Log.Warning(
                        "Suppressed {SuppressedCount} additional unavailable-segment warnings for {FileName} in the previous 60 seconds.",
                        suppressed,
                        fileName);
                }

                Log.Warning(
                    "Usenet segment was unavailable from all eligible provider sources. " +
                    "Segment: {SegmentId}; File: {FileName}; Operation: {Operation}; " +
                    "EligibleProviders: {EligibleProviders}; Attempts: {Attempts}; " +
                    "CachedSkips: {CachedSkips}; StorageGroupSkips: {StorageGroupSkips}; " +
                    "DurationMs: {DurationMs}",
                    segmentId,
                    fileName,
                    LatencyNames.ToWireName(operation),
                    walk.EligibleProviders,
                    walk.Attempts,
                    walk.CachedSkips,
                    walk.StorageGroupSkips,
                    walk.Elapsed.TotalMilliseconds);
                return;
            }

            if (walk.LastOutcomeWasException && lastException is not null)
                LogExhaustedProviders(lastHost, lastException);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Logging is observational; never change fetch, callback, or fallback ownership.
        }
    }

    /// <summary>
    /// Logs the terminal failure once all providers have been tried. Known
    /// transport/download failures log a human-friendly Warning with the reason;
    /// unexpected exceptions keep their full stack so they aren't lost, and the
    /// residual FetchStatus.Other case retains the concrete exception type name
    /// so support packs stay diagnosable without a schema change.
    /// </summary>
    private static void LogExhaustedProviders(string? providerHost, Exception exception)
    {
        var host = providerHost ?? "unknown";
        var status = ClassifyException(exception);
        if (exception.TryGetKnownErrorMessage(out var reason))
        {
            if (status == SegmentFetch.FetchStatus.Other)
            {
                Log.Warning(
                    "All providers exhausted. Last error from {Provider}. Status={Status} ExceptionType={ExceptionType} Reason: {Reason}",
                    host, status, exception.GetType().FullName, reason);
            }
            else
            {
                Log.Warning(
                    "All providers exhausted. Last error from {Provider}. Status={Status} Reason: {Reason}",
                    host, status, reason);
            }
        }
        else
        {
            Log.Error(
                exception,
                "All providers exhausted. Unexpected last error from {Provider}. Status={Status} ExceptionType={ExceptionType}",
                host, status, exception.GetType().FullName);
        }
    }

    private SegmentFetch RecordFetch(
        string metricsKey,
        SegmentFetch.FetchStatus status,
        long durationMs,
        int retries,
        StreamTraceRangeContext? traceRange,
        bool enqueue = true)
    {
        if (traceRange is { } range)
        {
            streamTrace?.Segment(
                range.SessionId, metricsKey, status, (int)Math.Min(int.MaxValue, durationMs), retries);
            // Billed to the generation captured when the stopwatch started, not the range
            // that happens to be open now — a prefetch can outlive the range that asked for it.
            streamTrace?.AddFetchWait(traceRange, TimeSpan.FromMilliseconds(durationMs));
        }

        var fetch = new SegmentFetch
        {
            At = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Provider = metricsKey,
            ReadSessionId = ReadSessionScope.Value,
            Bytes = 0, // bytes flow lazily through CountingYencStream → ProviderBytesTracker
            DurationMs = (int)Math.Min(int.MaxValue, durationMs),
            Status = status,
            Retries = retries,
        };
        PrometheusMetrics.Current?.RecordSegmentFetch(
            metricsKey,
            status.ToString().ToLowerInvariant(),
            TimeSpan.FromMilliseconds(durationMs));
        if (enqueue)
            metricsWriter?.RecordFetch(fetch);
        return fetch;
    }

    private void RecordSuccessfulFetch(
        string metricsKey,
        SegmentFetch.FetchStatus status,
        long durationMs,
        int retries,
        StreamTraceRangeContext? traceRange,
        List<(string Host, SegmentFetch.FetchStatus Reason)>? priorMisses)
    {
        var fetch = RecordFetch(
            metricsKey, status, durationMs, retries, traceRange, enqueue: false);
        if (priorMisses is not { Count: > 0 })
        {
            metricsWriter?.RecordFetch(fetch);
            return;
        }

        var crossMisses = FilterCrossProviderMisses(priorMisses, metricsKey);
        if (crossMisses is { Count: > 0 })
            _usageTracker.RecordFailoverSave();
        RecordRescue(priorMisses, crossMisses, metricsKey, fetch);
    }

    /// <summary>
    /// Classifies <paramref name="exception"/>, records the SegmentFetch row, and for residual
    /// <see cref="SegmentFetch.FetchStatus.Other"/> records sufficient request context for a
    /// warning-level support pack to identify the unexpected throw site without exposing article IDs.
    /// </summary>
    private SegmentFetch.FetchStatus ClassifyAndRecordFailure(
        string metricsKey, Exception exception, long durationMs, int retries,
        StreamTraceRangeContext? traceRange, NntpOperation operation, SegmentId? segmentId)
    {
        var status = ClassifyException(exception);
        RecordFetch(metricsKey, status, durationMs, retries, traceRange);
        if (status == SegmentFetch.FetchStatus.Other)
        {
            exception.TryGetCausingException<ArgumentException>(out var argumentException);
            var exceptionType = exception.GetType().FullName ?? "unknown";
            var operationName = operation.ToString().ToLowerInvariant();
            var parameterName = argumentException?.ParamName;
            var segmentHash = HashSegmentId(segmentId);
            var innermostException = exception.GetBaseException();
            var reason = RedactSegmentId(exception.Message, segmentId);
            var innermostReason = RedactSegmentId(innermostException.Message, segmentId);
            var warningKey = string.Join(
                '\n',
                metricsKey,
                exceptionType,
                parameterName ?? "",
                operationName);

            // Coalesce only identical unexpected failures. The first event carries all the
            // request context and the stack at Error so warning-level support packs retain it.
            if (ThrottledSegmentWarning.Write(
                    warningKey,
                    "Unclassified Usenet segment fetch failure. " +
                    "ProviderKey={ProviderKey} Operation={Operation} " +
                    "ExceptionType={ExceptionType} Reason={Reason} ParameterName={ParameterName} " +
                    "SegmentHash={SegmentHash} AttemptIndex={AttemptIndex} " +
                    "InnermostExceptionType={InnermostExceptionType} InnermostReason={InnermostReason}",
                    metricsKey,
                    operationName,
                    exceptionType,
                    reason,
                    parameterName,
                    segmentHash,
                    retries,
                    innermostException.GetType().FullName,
                    innermostReason))
            {
                Log.Error(
                    "Unclassified Usenet segment fetch failure stack. " +
                    "ProviderKey={ProviderKey} Operation={Operation} " +
                    "ExceptionType={ExceptionType} Reason={Reason} ParameterName={ParameterName} " +
                    "SegmentHash={SegmentHash} AttemptIndex={AttemptIndex} " +
                    "InnermostExceptionType={InnermostExceptionType} InnermostReason={InnermostReason} " +
                    "Stack={Stack}",
                    metricsKey,
                    operationName,
                    exceptionType,
                    reason,
                    parameterName,
                    segmentHash,
                    retries,
                    innermostException.GetType().FullName,
                    innermostReason,
                    RedactSegmentId(exception.ToString(), segmentId));
            }
        }
        return status;
    }

    private static string? HashSegmentId(SegmentId? segmentId)
    {
        var value = segmentId?.ToString();
        if (string.IsNullOrEmpty(value)) return null;

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..12];
    }

    private static string RedactSegmentId(string value, SegmentId? segmentId)
    {
        var segment = segmentId?.ToString();
        return string.IsNullOrEmpty(segment)
            ? value
            : value
                .Replace($"<{segment}>", "[segment]", StringComparison.Ordinal)
                .Replace(segment, "[segment]", StringComparison.Ordinal);
    }

    /// <summary>
    /// Same-provider self-retries (timeout → re-probe primary) are not backup rescues.
    /// Overview FailoverSaves / FailoverMisses only keep misses from a different provider.
    /// </summary>
    private static List<(string Host, SegmentFetch.FetchStatus Reason)>? FilterCrossProviderMisses(
        List<(string Host, SegmentFetch.FetchStatus Reason)>? priorMisses,
        string rescuer)
    {
        if (priorMisses is not { Count: > 0 }) return null;
        List<(string Host, SegmentFetch.FetchStatus Reason)>? cross = null;
        foreach (var miss in priorMisses.Where(miss => !string.Equals(miss.Host, rescuer, StringComparison.OrdinalIgnoreCase)))
        {
            (cross ??= []).Add(miss);
        }
        return cross;
    }

    /// <summary>
    /// Stream traces keep every prior-miss edge (including same-provider retries) for
    /// support-pack stall attribution. Overview FailoverMisses only get cross-provider edges.
    /// </summary>
    private void RecordRescue(
        List<(string Host, SegmentFetch.FetchStatus Reason)>? allMisses,
        List<(string Host, SegmentFetch.FetchStatus Reason)>? crossMisses,
        string rescuer,
        SegmentFetch fetch)
    {
        if (allMisses != null && ReadSessionScope.Value is { } sessionId)
        {
            foreach (var (from, reason) in allMisses)
                streamTrace?.Failover(sessionId, from, rescuer, reason.ToString());
        }

        if (metricsWriter == null) return;
        if (crossMisses is not { Count: > 0 })
        {
            metricsWriter.RecordFetch(fetch);
            return;
        }

        var misses = new List<FailoverMiss>(crossMisses.Count);
        foreach (var (from, reason) in crossMisses)
        {
            misses.Add(new FailoverMiss
            {
                At = fetch.At,
                FromProvider = from,
                ToProvider = rescuer,
                Reason = reason,
            });
        }
        metricsWriter.RecordRescue(
            fetch,
            new MetricEvent
            {
                At = fetch.At,
                Kind = MetricsWriter.FailoverSaveEventKind,
                Tag1 = rescuer,
            },
            misses);
    }

    private T WrapProviderResponse<T>(T result, string metricsKey) where T : UsenetResponse
    {
        return result switch
        {
            UsenetDecodedBodyResponse b
                => (T)(object)(b with
                {
                    Stream = WrapProviderStream(b.Stream!, b.SegmentId, metricsKey)
                }),
            UsenetDecodedArticleResponse a
                => (T)(object)(a with
                {
                    Stream = WrapProviderStream(a.Stream!, a.SegmentId, metricsKey)
                }),
            _ => result,
        };
    }

    private YencStream WrapProviderStream(YencStream stream, SegmentId segmentId, string metricsKey)
    {
        YencStream wrapped = new CorruptionDetectingYencStream(stream, segmentId, metricsKey);
        if (bytesTracker != null)
            wrapped = new CountingYencStream(wrapped, bytesTracker, metricsKey, activeReadRegistry);
        return wrapped;
    }

    /// <summary>
    /// Maps a fetch failure to a <see cref="SegmentFetch.FetchStatus"/> for metrics/UI.
    /// Walks the exception chain (<see cref="ExceptionExtensions.TryGetCausingException{T}"/>)
    /// so a known cause wrapped by an outer exception is still classified correctly.
    /// Anything left over falls into <see cref="SegmentFetch.FetchStatus.Other"/> — callers
    /// should log the concrete exception type there so support packs stay diagnosable.
    /// Do not renumber existing enum values; only append.
    /// </summary>
    internal static SegmentFetch.FetchStatus ClassifyException(Exception ex)
    {
        // Singular BODY/HEAD and streaming paths surface a definitive 430/451 as a thrown
        // UsenetArticleNotFoundException; STAT/batch paths return it as a response that is
        // already recorded Missing. Classify both the same.
        if (ex.TryGetCausingException<UsenetArticleNotFoundException>(out _))
            return SegmentFetch.FetchStatus.Missing;

        if (ex.TryGetCausingException<TimeoutException>(out _))
            return SegmentFetch.FetchStatus.Timeout;

        // yEnc decode failures escape as InvalidDataException, which derives from
        // IOException — it must be checked before the IOException -> Network case.
        if (ex.TryGetCausingException<UsenetCorruptArticleException>(out _) ||
            ex.TryGetCausingException<System.IO.InvalidDataException>(out _))
            return SegmentFetch.FetchStatus.Corrupt;

        if (ex.TryGetCausingException<CouldNotLoginToUsenetException>(out _) ||
            ex.TryGetCausingException<UnauthorizedAccessException>(out _))
            return SegmentFetch.FetchStatus.Auth;

        if (ex.TryGetCausingException<CouldNotConnectToUsenetException>(out _) ||
            ex.TryGetCausingException<UsenetNotConnectedException>(out _) ||
            ex.TryGetCausingException<UsenetException>(out _) ||
            ex.TryGetCausingException<System.Net.Sockets.SocketException>(out _) ||
            ex.TryGetCausingException<System.IO.IOException>(out _))
            return SegmentFetch.FetchStatus.Network;

        if (ex.TryGetCausingException<UsenetUnexpectedResponseException>(out _) ||
            ex.TryGetCausingException<UsenetProtocolException>(out _))
            return SegmentFetch.FetchStatus.Protocol;

        return SegmentFetch.FetchStatus.Other;
    }

    private static string NormalizeStorageGroup(string? value) => value?.Trim() ?? "";

    private List<MultiConnectionNntpClient> SelectOrderedProviders(
        NntpOperation operation,
        out MultiConnectionNntpClient? reserved)
    {
        lock (_selectLock)
        {
            var enabled = providers
                .Where(x => x.ProviderType != ProviderType.Disabled)
                .Where(x => !IsOverLimit(x))
                .ToList();

            // Reading state here must not claim the half-open probe slot. IsTripped claims
            // it, so one selection ends up holding a probe it may never dispatch while
            // every other selection treats the provider as tripped.
            var selectionStates =
                new Dictionary<
                    MultiConnectionNntpClient,
                    (ProviderCircuitState CircuitState, int UnreservedConnections)>(
                    enabled.Count);
            foreach (var provider in enabled)
            {
                selectionStates[provider] = (
                    provider.GetCircuitBreakerSnapshot().State,
                    UnreservedConnections: 0);
            }

            var selectable = enabled
                .Where(x => selectionStates[x].CircuitState != ProviderCircuitState.Open)
                .ToList();
            var pool = selectable.Count > 0 ? selectable : enabled;
            foreach (var provider in pool)
            {
                selectionStates[provider] = (
                    selectionStates[provider].CircuitState,
                    provider.UnreservedConnectionsFor(operation));
            }

            // Half-open sorts behind the healthy providers of its own tier and keeps that
            // tier, so a recovering primary is still tried ahead of a backup or block
            // account. A provider that may still be down should not stall a request a
            // healthy peer would serve. The failover walk reaches it and any command it
            // completes resets the breaker.
            var byTier = pool.OrderBy(x => x.ProviderType);
            var byRecovery = byTier.ThenBy(x =>
                selectionStates[x].CircuitState == ProviderCircuitState.HalfOpen ? 1 : 0);
            var cascade = cascadeEnabled?.Invoke() == true;
            var prioritized = cascade
                ? byRecovery.ThenBy(x =>
                    EffectivePriority(x, selectionStates[x].UnreservedConnections))
                : byRecovery;
            var byUsage = prioritized.ThenByDescending(x => GetRemainingBytes(x));
            // Prefer providers with more spare capacity. In cascade mode this is a
            // tie-break after EffectivePriority and uses spare *fraction* so unequal
            // MaxConnections cannot outweigh Priority. In pool mode absolute spare
            // outranks learned speed so a full pool cannot monopolize.
            var capacityBalanced = cascade
                ? byUsage.ThenByDescending(x =>
                    (double)selectionStates[x].UnreservedConnections
                    / Math.Max(1, x.MaxConnections))
                : byUsage.ThenByDescending(x => selectionStates[x].UnreservedConnections);
            var ordered = capacityBalanced
                .ThenBy(EstimatedDeliveryScore)
                .ToList();

            reserved = ordered.Count > 0 ? ordered[0] : null;
            reserved?.ReservePending(operation);
            return ordered;
        }
    }

    internal MultiConnectionNntpClient? SelectProviderForBenchmark(NntpOperation operation)
    {
        var ordered = SelectOrderedProviders(operation, out var reserved);
        reserved?.ReleasePending(operation);
        return ordered.FirstOrDefault();
    }

    /// <summary>
    /// Cascade sort key: configured priority, plus one priority step when at most 25% of
    /// the provider's pool remains unreserved, plus a large demotion when fully
    /// saturated. Absolute spare is not used — that made larger MaxConnections pools
    /// outrank a healthier Priority-0 primary while idle. Thin-spare still lets a
    /// Priority-0 pool with 1/8 free yield to an idle Priority-1 peer (#650).
    /// </summary>
    private static int EffectivePriority(
        MultiConnectionNntpClient provider,
        int unreservedConnections)
    {
        const int saturationDemotion = 1 << 20;
        if (unreservedConnections == 0)
            return provider.Priority + saturationDemotion;

        // At most 25% of the configured pool remains unreserved (integer form of
        // spare/max <= 1/4 so boundary cases like 2/8 do not depend on float rounding).
        var max = Math.Max(1, provider.MaxConnections);
        var thinSpareDemotion = unreservedConnections * 4 <= max ? 1 : 0;
        return provider.Priority + thinSpareDemotion;
    }

    private double EstimatedDeliveryScore(MultiConnectionNntpClient provider)
    {
        var inFlight = provider.ActiveConnections + provider.PendingSelections + 1;
        var bytesPerMs = bytesTracker?.GetBytesPerMs(provider.MetricsKey) ?? 0d;
        return bytesPerMs > 0 ? inFlight / bytesPerMs : inFlight;
    }

    private bool IsOverLimit(MultiConnectionNntpClient client)
    {
        var limit = client.ByteLimit;
        if (bytesTracker == null || !limit.HasValue || limit.Value <= 0) return false;
        var used = bytesTracker.GetLifetime(client.MetricsKey) + client.BytesUsedOffset;
        // Stop at the effective cutoff (95% of cap) so in-flight fetches that
        // already passed this check can't push the actual count past the cap.
        // See ProviderUsageHelper.EffectiveLimitFraction for the rationale.
        var effective = (long)(limit.Value * ProviderUsageHelper.EffectiveLimitFraction);
        return used >= effective;
    }

    private long GetRemainingBytes(MultiConnectionNntpClient client)
    {
        var limit = client.ByteLimit;
        if (bytesTracker == null || !limit.HasValue || limit.Value <= 0) return long.MaxValue;
        var used = bytesTracker.GetLifetime(client.MetricsKey) + client.BytesUsedOffset;
        return Math.Max(0, limit.Value - used);
    }

    private static int ResolveDepth(MultiConnectionNntpClient primary, int fallbackDepth)
    {
        return primary.ConfiguredPipeliningDepth is int d and > 0
            ? Math.Clamp(d, 1, 64)
            : fallbackDepth;
    }

    public override async IAsyncEnumerable<PipelinedStatResult> StatsPipelinedAsync(
        IReadOnlyList<string> segmentIds, int depth,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (segmentIds.Count == 0) yield break;
        var orderedProviders = SelectOrderedProviders(NntpOperation.PipelinedStat, out var reserved);
        using var releasePending = new ScopeReleaser(
            () => reserved?.ReleasePending(NntpOperation.PipelinedStat));
        var primary = orderedProviders.Count > 0 ? orderedProviders[0] : null;
        if (primary == null) yield break;

        // Primary-only sweep: STAT chunk sizing is fixed in BaseNntpClient
        // (UsenetSharp windows internally). Per-provider BODY depth does not apply.
        // Misses are rechecked with per-STAT failover in CheckAllSegmentsPipelinedAsync.
        await foreach (var result in primary.StatsPipelinedAsync(segmentIds, depth, cancellationToken)
                           .WithCancellation(cancellationToken).ConfigureAwait(false))
            yield return result;
    }

    public override async IAsyncEnumerable<PipelinedBodyResult> DecodedBodiesPipelinedAsync(
        IReadOnlyList<string> segmentIds, int depth,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (segmentIds.Count == 0) yield break;

        // Resolve per-provider depth without holding a reservation across the whole
        // enumeration — each DecodedBodiesAsync batch selects providers itself and
        // already records metrics / wraps streams for byte counting.
        int effectiveDepth;
        {
            var orderedProviders = SelectOrderedProviders(NntpOperation.PipelinedBody, out var reserved);
            using var releasePending = new ScopeReleaser(
                () => reserved?.ReleasePending(NntpOperation.PipelinedBody));
            var primary = orderedProviders.Count > 0 ? orderedProviders[0] : null;
            if (primary == null) yield break;
            effectiveDepth = ResolveDepth(primary, depth);
        }

        await foreach (var result in base.DecodedBodiesPipelinedAsync(
                           segmentIds, effectiveDepth, cancellationToken)
                           .WithCancellation(cancellationToken).ConfigureAwait(false))
            yield return result;
    }

    public override void Dispose()
    {
        connectionPoolStats?.Deactivate();
        foreach (var provider in providers)
            provider.Dispose();
        _batchFallbackStartGate.Dispose();
        GC.SuppressFinalize(this);
    }

    internal override void Retire() => connectionPoolStats?.Deactivate();
}
