using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Database.Models.Metrics;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models;
using NzbWebDAV.Services;
using NzbWebDAV.Services.Metrics;
using NzbWebDAV.Services.StreamTrace;
using NzbWebDAV.Streams;
using Serilog;
using Serilog.Context;
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
    ActiveReadRegistry? activeReadRegistry = null
) : NntpClient, INntpConnectionStats
{
    /// <summary>
    /// Max concurrent batch-failover BODY starts. Admission stays strictly ordered;
    /// this only bounds how many fallback walks may be in flight at once so sequential
    /// consumers cannot deadlock on an unbounded fan-out (see AGENTS.md).
    /// </summary>
    private const int MaxConcurrentFallbackStarts = 4;
    private readonly SemaphoreSlim _batchFallbackStartGate = new(MaxConcurrentFallbackStarts);
    public int InFlightConnections => providers.Sum(p => p.InFlightConnections);

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
    private readonly ProviderUsageTracker _usageTracker = usageTracker ?? new ProviderUsageTracker();
    private static readonly AsyncLocal<Guid?> ReadSessionScope = new();
    internal static Guid? CurrentReadSessionId => ReadSessionScope.Value;

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

    private sealed class ScopeReleaser(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
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
        return RunFromPoolWithBackup(x => x.StatAsync(segmentId, cancellationToken), cancellationToken);
    }

    public override Task<UsenetHeadResponse> HeadAsync(SegmentId segmentId, CancellationToken cancellationToken)
    {
        return RunFromPoolWithBackup(x => x.HeadAsync(segmentId, cancellationToken), cancellationToken);
    }

    public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync
    (
        SegmentId segmentId,
        CancellationToken cancellationToken
    )
    {
        return RunFromPoolWithBackup(x => x.DecodedBodyAsync(segmentId, cancellationToken), cancellationToken);
    }

    public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync
    (
        SegmentId segmentId,
        CancellationToken cancellationToken
    )
    {
        return RunFromPoolWithBackup(x => x.DecodedArticleAsync(segmentId, cancellationToken), cancellationToken);
    }

    public override Task<UsenetDateResponse> DateAsync(CancellationToken cancellationToken)
    {
        return RunFromPoolWithBackup(x => x.DateAsync(cancellationToken), cancellationToken);
    }

    public override async Task<UsenetDecodedBodyResponse> DecodedBodyAsync
    (
        SegmentId segmentId,
        Action<ArticleBodyResult>? onConnectionReadyAgain,
        CancellationToken cancellationToken
    )
    {
        return await RunStreamingFromPoolWithBackup(
            (provider, callback) =>
                provider.DecodedBodyAsync(segmentId, callback, cancellationToken),
            UsenetResponseType.ArticleRetrievedBodyFollows,
            onConnectionReadyAgain,
            cancellationToken).ConfigureAwait(false);
    }

    public override async Task<UsenetDecodedBodyBatch> DecodedBodiesAsync
    (
        IReadOnlyList<SegmentId> segmentIds,
        Action<ArticleBodyResult>? onConnectionReadyAgain,
        CancellationToken cancellationToken
    )
    {
        ExceptionDispatchInfo? lastException = null;
        var orderedProviders = SelectOrderedProviders(out var reserved);
        using var releasePending = new ScopeReleaser(() => reserved?.ReleasePending());
        for (var providerIndex = 0; providerIndex < orderedProviders.Count; providerIndex++)
        {
            var provider = orderedProviders[providerIndex];
            var deferredCallback = new DeferredArticleBodyCallback();
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var primaryBatch = await provider.DecodedBodiesAsync(
                    segmentIds, deferredCallback.Invoke, cancellationToken).ConfigureAwait(false);
                var coordinator = new BatchCallbackCoordinator(
                    primaryBatch.Responses.Count, onConnectionReadyAgain);
                deferredCallback.Activate(coordinator.CompleteTransfer);
                var fallbackProviders = orderedProviders
                    .Skip(providerIndex + 1)
                    .ToArray();
                var responses =
                    new Task<UsenetDecodedBodyResponse>[primaryBatch.Responses.Count];
                // Admission (start-order) is separate from transfer completion so segment
                // N+1 can begin its fallback walk after N has admitted/started, without
                // waiting for N's body stream to finish. Concurrent starts are bounded by
                // _batchFallbackStartGate until each transfer's body callback fires.
                Task previousFallbackAdmission = Task.CompletedTask;
                for (var index = 0; index < responses.Length; index++)
                {
                    var fallbackAdmission = new TaskCompletionSource(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    responses[index] = ResolveBatchResponseAsync(
                        primaryBatch.Responses[index],
                        segmentIds[index],
                        provider,
                        fallbackProviders,
                        previousFallbackAdmission,
                        fallbackAdmission,
                        coordinator,
                        cancellationToken);
                    previousFallbackAdmission = fallbackAdmission.Task;
                }
                return new UsenetDecodedBodyBatch { Responses = responses };
            }
            catch (Exception e) when (e.TryGetCausingException(out UsenetArticleNotFoundException? _))
            {
                // Invalid / permanently missing segment ids are invalid on every provider.
                deferredCallback.Discard();
                InvokeCompletionCallback(
                    onConnectionReadyAgain, ArticleBodyResult.NotRetrieved);
                throw;
            }
            catch (Exception e) when (!e.IsCancellationException(cancellationToken))
            {
                deferredCallback.Discard();
                lastException = ExceptionDispatchInfo.Capture(e);
            }
            catch
            {
                deferredCallback.Discard();
                InvokeCompletionCallback(
                    onConnectionReadyAgain, ArticleBodyResult.NotRetrieved);
                throw;
            }
        }

        InvokeCompletionCallback(onConnectionReadyAgain, ArticleBodyResult.NotRetrieved);
        lastException?.Throw();
        throw new Exception("There are no usenet providers configured.");
    }

    private async Task<UsenetDecodedBodyResponse> ResolveBatchResponseAsync(
        Task<UsenetDecodedBodyResponse> primaryResponse,
        SegmentId segmentId,
        MultiConnectionNntpClient primaryProvider,
        IReadOnlyList<MultiConnectionNntpClient> fallbackProviders,
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

        var primaryStopwatch = Stopwatch.StartNew();
        List<(string Host, SegmentFetch.FetchStatus Reason)>? priorMisses = null;
        // Fresh per article resolution. When primary re-probe is enabled, do not mark the
        // primary's storage group on the initial batch 430 so that re-probe is not skipped
        // by its own miss. Cross-request negative cache (#639) may skip re-probe separately.
        var missingGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            UsenetDecodedBodyResponse? response = null;
            ExceptionDispatchInfo? lastException = null;
            try
            {
                response = await primaryResponse.ConfigureAwait(false);
            }
            catch (Exception e) when (!e.IsCancellationException(cancellationToken))
            {
                primaryStopwatch.Stop();
                var reason = ClassifyException(e);
                RecordFetch(primaryProvider.MetricsKey, reason, primaryStopwatch.ElapsedMilliseconds, 0);
                (priorMisses ??= []).Add((primaryProvider.MetricsKey, reason));
                lastException = ExceptionDispatchInfo.Capture(e);
            }

            if (response?.ResponseType == UsenetResponseType.ArticleRetrievedBodyFollows)
            {
                primaryStopwatch.Stop();
                _usageTracker.RecordSuccess(primaryProvider.MetricsKey);
                RecordFetch(primaryProvider.MetricsKey, SegmentFetch.FetchStatus.Ok,
                    primaryStopwatch.ElapsedMilliseconds, 0);
                return WrapProviderResponse(response, primaryProvider.MetricsKey);
            }

            var definitiveMiss = response != null &&
                UsenetArticleAvailability.IsDefinitiveMissing(response);
            if (definitiveMiss)
            {
                primaryStopwatch.Stop();
                RecordFetch(primaryProvider.MetricsKey, SegmentFetch.FetchStatus.Missing,
                    primaryStopwatch.ElapsedMilliseconds, 0);
                (priorMisses ??= []).Add((primaryProvider.MetricsKey, SegmentFetch.FetchStatus.Missing));
            }

            // Re-probe primary once on a definitive miss when enabled (default). Multi-node
            // spool routing can return a transient 430/451 on one connection. Operators may
            // disable via usenet.cascade.retry-primary-on-miss; connection-level failures
            // always re-try the primary. When #639 negative cache lands, a cache hit should
            // skip this re-probe even if the setting is on.
            var reprobePrimary = !definitiveMiss || retryPrimaryOnMiss?.Invoke() != false;
            IReadOnlyList<MultiConnectionNntpClient> retryProviders = reprobePrimary
                ? [primaryProvider, .. fallbackProviders]
                : fallbackProviders;
            if (definitiveMiss && !reprobePrimary)
            {
                var primaryGroup = NormalizeStorageGroup(primaryProvider.StorageGroup);
                if (primaryGroup.Length > 0)
                    missingGroups.Add(primaryGroup);
            }

            if (response == null || !definitiveMiss)
            {
                if (response != null)
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
                        Log.Debug(
                            "Skipping provider `{Host}` on storage group `{Group}` — " +
                            "a sibling provider already reported the article missing.",
                            provider.Host, group);
                        continue;
                    }

                    coordinator.AddTransfer();
                    var deferredCallback = new DeferredArticleBodyCallback();
                    var stopwatch = Stopwatch.StartNew();
                    try
                    {
                        response = await provider.DecodedBodyAsync(
                            segmentId, deferredCallback.Invoke, cancellationToken).ConfigureAwait(false);
                        stopwatch.Stop();
                        var responseType = response.ResponseType;
                        if (responseType == UsenetResponseType.ArticleRetrievedBodyFollows)
                        {
                            _usageTracker.RecordSuccess(provider.MetricsKey);
                            RecordFetch(provider.MetricsKey, SegmentFetch.FetchStatus.Ok,
                                stopwatch.ElapsedMilliseconds, priorMisses?.Count ?? 0);
                            if (priorMisses is { Count: > 0 })
                            {
                                _usageTracker.RecordFailoverSave();
                                RecordFailoverMisses(priorMisses, provider.MetricsKey);
                            }
                            response = WrapProviderResponse(response, provider.MetricsKey);
                            gateOwnedByTransfer = true;
                            deferredCallback.Activate(result =>
                            {
                                try
                                {
                                    coordinator.CompleteTransfer(result);
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
                                stopwatch.ElapsedMilliseconds, priorMisses?.Count ?? 0);
                            (priorMisses ??= []).Add((provider.MetricsKey, SegmentFetch.FetchStatus.Missing));
                            if (UsenetArticleAvailability.IsDefinitiveMissing(response) &&
                                group.Length > 0)
                            {
                                missingGroups.Add(group);
                            }
                            deferredCallback.Discard();
                            coordinator.CompleteAttempt();
                        }

                        lastException = null;
                    }
                    catch (Exception e) when (!e.IsCancellationException(cancellationToken))
                    {
                        stopwatch.Stop();
                        var reason = ClassifyException(e);
                        RecordFetch(provider.MetricsKey, reason, stopwatch.ElapsedMilliseconds,
                            priorMisses?.Count ?? 0);
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

    private sealed class BatchCallbackCoordinator(
        int responseCount,
        Action<ArticleBodyResult>? callback)
    {
        private int _remaining = responseCount + 1;
        private int _transportFailed;
        private int _resolutionFailed;
        private int _callbackInvoked;

        public void AddTransfer()
        {
            Interlocked.Increment(ref _remaining);
        }

        public void CompleteTransfer(ArticleBodyResult result)
        {
            if (result == ArticleBodyResult.NotRetrieved)
            {
                Volatile.Write(ref _transportFailed, 1);
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
            if (Interlocked.Decrement(ref _remaining) != 0 ||
                Interlocked.Exchange(ref _callbackInvoked, 1) != 0)
            {
                return;
            }

            InvokeCompletionCallback(
                callback,
                Volatile.Read(ref _transportFailed) == 0 &&
                Volatile.Read(ref _resolutionFailed) == 0
                    ? ArticleBodyResult.Retrieved
                    : ArticleBodyResult.NotRetrieved);
        }
    }

    public override async Task<UsenetDecodedArticleResponse> DecodedArticleAsync
    (
        SegmentId segmentId,
        Action<ArticleBodyResult>? onConnectionReadyAgain,
        CancellationToken cancellationToken
    )
    {
        return await RunStreamingFromPoolWithBackup(
            (provider, callback) =>
                provider.DecodedArticleAsync(segmentId, callback, cancellationToken),
            UsenetResponseType.ArticleRetrievedHeadAndBodyFollow,
            onConnectionReadyAgain,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<T> RunStreamingFromPoolWithBackup<T>(
        Func<INntpClient, Action<ArticleBodyResult>, Task<T>> task,
        UsenetResponseType successResponseType,
        Action<ArticleBodyResult>? onConnectionReadyAgain,
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
        var orderedProviders = SelectOrderedProviders(out var reserved);
        using var releasePending = new ScopeReleaser(() => reserved?.ReleasePending());
        var attemptIndex = 0;
        foreach (var provider in orderedProviders)
        {
            var group = NormalizeStorageGroup(provider.StorageGroup);
            if (group.Length > 0 && missingGroups.Contains(group))
            {
                Log.Debug(
                    "Skipping provider `{Host}` on storage group `{Group}` — " +
                    "a sibling provider already reported the article missing.",
                    provider.Host, group);
                continue;
            }

            var deferredCallback = new DeferredArticleBodyCallback();
            var stopwatch = Stopwatch.StartNew();
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = await task(provider, deferredCallback.Invoke)
                    .ConfigureAwait(false);
                stopwatch.Stop();
                if (result.ResponseType == successResponseType)
                {
                    if (attribution != null) attribution.Host = provider.Host;
                    _usageTracker.RecordSuccess(provider.MetricsKey);
                    RecordFetch(provider.MetricsKey, SegmentFetch.FetchStatus.Ok,
                        stopwatch.ElapsedMilliseconds, attemptIndex);
                    if (attemptIndex > 0)
                    {
                        _usageTracker.RecordFailoverSave();
                        RecordFailoverMisses(priorMisses, provider.MetricsKey);
                    }
                    result = WrapProviderResponse(result, provider.MetricsKey);
                    deferredCallback.Activate(onConnectionReadyAgain ?? (_ => { }));
                    return result;
                }

                deferredCallback.Discard();
                if (UsenetArticleAvailability.IsDefinitiveMissing(result))
                {
                    RecordFetch(provider.MetricsKey, SegmentFetch.FetchStatus.Missing,
                        stopwatch.ElapsedMilliseconds, attemptIndex);
                    (priorMisses ??= []).Add((provider.MetricsKey, SegmentFetch.FetchStatus.Missing));
                    lastNoArticleResult = result;
                    lastOutcomeWasException = false;
                    if (group.Length > 0) missingGroups.Add(group);
                    attemptIndex++;
                    continue;
                }

                RecordFetch(provider.MetricsKey, SegmentFetch.FetchStatus.Missing,
                    stopwatch.ElapsedMilliseconds, attemptIndex);
                InvokeCompletionCallback(
                    onConnectionReadyAgain, ArticleBodyResult.NotRetrieved);
                return result;
            }
            catch (Exception e) when (!e.IsCancellationException(cancellationToken))
            {
                stopwatch.Stop();
                var reason = ClassifyException(e);
                RecordFetch(provider.MetricsKey, reason, stopwatch.ElapsedMilliseconds, attemptIndex);
                (priorMisses ??= []).Add((provider.MetricsKey, reason));
                deferredCallback.Discard();
                lastException = ExceptionDispatchInfo.Capture(e);
                lastOutcomeWasException = true;
                attemptIndex++;
            }
            catch
            {
                deferredCallback.Discard();
                InvokeCompletionCallback(
                    onConnectionReadyAgain, ArticleBodyResult.NotRetrieved);
                throw;
            }
        }

        // Terminal 430 after skips/exhaustion must fire the completion callback exactly once.
        InvokeCompletionCallback(onConnectionReadyAgain, ArticleBodyResult.NotRetrieved);
        if (lastOutcomeWasException) lastException!.Throw();
        if (lastNoArticleResult is not null) return lastNoArticleResult;
        throw new Exception("There are no usenet providers configured.");
    }

    private async Task<T> RunFromPoolWithBackup<T>
    (
        Func<INntpClient, Task<T>> task,
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
        var orderedProviders = SelectOrderedProviders(out var reserved);
        using var releasePending = new ScopeReleaser(() => reserved?.ReleasePending());
        var attemptIndex = 0;
        foreach (var provider in orderedProviders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var group = NormalizeStorageGroup(provider.StorageGroup);
            if (group.Length > 0 && missingGroups.Contains(group))
            {
                Log.Debug(
                    "Skipping provider `{Host}` on storage group `{Group}` — " +
                    "a sibling provider already reported the article missing.",
                    provider.Host, group);
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
            var stopwatch = Stopwatch.StartNew();
            try
            {
                var result = await task.Invoke(provider).ConfigureAwait(false);
                stopwatch.Stop();

                // if no article with that message-id is found, try again with the next provider.
                // Only a definitive miss (430 / provider 451) marks the storage group missing —
                // never a connection error.
                if (UsenetArticleAvailability.IsDefinitiveMissing(result))
                {
                    RecordFetch(provider.MetricsKey, SegmentFetch.FetchStatus.Missing, stopwatch.ElapsedMilliseconds, attemptIndex);
                    (priorMisses ??= new()).Add((provider.MetricsKey, SegmentFetch.FetchStatus.Missing));
                    lastNoArticleResult = result;
                    lastOutcomeWasException = false;
                    if (group.Length > 0) missingGroups.Add(group);
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
                    RecordFetch(provider.MetricsKey, SegmentFetch.FetchStatus.Ok, stopwatch.ElapsedMilliseconds, attemptIndex);
                    if (attemptIndex > 0)
                    {
                        _usageTracker.RecordFailoverSave();
                        RecordFailoverMisses(priorMisses, rescuer: provider.MetricsKey);
                    }
                    result = WrapProviderResponse(result, provider.MetricsKey);
                }
                else if (result is UsenetDecodedBodyResponse or UsenetDecodedArticleResponse)
                {
                    // BODY/ARTICLE response with an unexpected (non-success, non-430) response type.
                    RecordFetch(provider.MetricsKey, SegmentFetch.FetchStatus.Missing, stopwatch.ElapsedMilliseconds, attemptIndex);
                }
                // STAT/HEAD/DATE successes: intentionally no SegmentFetch row (not a segment transfer;
                // matches StatsPipelinedAsync which records nothing).

                return result;
            }
            catch (Exception e) when (!e.IsCancellationException(cancellationToken))
            {
                stopwatch.Stop();
                var reason = ClassifyException(e);
                RecordFetch(provider.MetricsKey, reason, stopwatch.ElapsedMilliseconds, attemptIndex);
                (priorMisses ??= new()).Add((provider.MetricsKey, reason));
                lastException = ExceptionDispatchInfo.Capture(e);
                lastOutcomeWasException = true;
                attemptIndex++;
            }
        }

        // Whichever terminal outcome occurred on the last attempted provider wins,
        // matching the original fallback precedence (a later connection error beats
        // an earlier 430, and a later 430 beats an earlier error).
        if (lastOutcomeWasException)
        {
            Log.Warning(
                "All providers exhausted. Last error from {Provider}: {ErrorMessage}",
                lastAttemptedProvider?.Host ?? "unknown",
                lastException!.SourceException.Message);
            lastException.Throw();
        }
        if (lastNoArticleResult is not null) return lastNoArticleResult;
        throw new Exception("There are no usenet providers configured.");
    }

    private void RecordFetch(string metricsKey, SegmentFetch.FetchStatus status, long durationMs, int retries)
    {
        if (ReadSessionScope.Value is { } sessionId)
        {
            streamTrace?.Segment(sessionId, metricsKey, status, (int)Math.Min(int.MaxValue, durationMs), retries);
        }

        if (metricsWriter == null) return;
        metricsWriter.RecordFetch(new SegmentFetch
        {
            At = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Provider = metricsKey,
            ReadSessionId = ReadSessionScope.Value,
            Bytes = 0, // bytes flow lazily through CountingYencStream → ProviderBytesTracker
            DurationMs = (int)Math.Min(int.MaxValue, durationMs),
            Status = status,
            Retries = retries,
        });
    }

    private void RecordFailoverMisses(
        List<(string Host, SegmentFetch.FetchStatus Reason)>? priorMisses,
        string rescuer)
    {
        if (priorMisses != null && ReadSessionScope.Value is { } sessionId)
        {
            foreach (var (from, reason) in priorMisses)
                streamTrace?.Failover(sessionId, from, rescuer, reason.ToString());
        }

        if (metricsWriter == null || priorMisses == null) return;
        var at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (var (from, reason) in priorMisses)
        {
            metricsWriter.RecordFailoverMiss(new FailoverMiss
            {
                At = at,
                FromProvider = from,
                ToProvider = rescuer,
                Reason = reason,
            });
        }
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

    private static SegmentFetch.FetchStatus ClassifyException(Exception ex)
    {
        if (ex is TimeoutException) return SegmentFetch.FetchStatus.Timeout;
        if (ex is UnauthorizedAccessException) return SegmentFetch.FetchStatus.Auth;
        if (ex is System.IO.IOException || ex is System.Net.Sockets.SocketException) return SegmentFetch.FetchStatus.Network;
        return SegmentFetch.FetchStatus.Other;
    }

    private static string NormalizeStorageGroup(string? value) => value?.Trim() ?? "";

    private List<MultiConnectionNntpClient> SelectOrderedProviders(out MultiConnectionNntpClient? reserved)
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
            var circuitStates = new Dictionary<MultiConnectionNntpClient, ProviderCircuitState>(enabled.Count);
            foreach (var provider in enabled)
                circuitStates[provider] = provider.GetCircuitBreakerSnapshot().State;

            var selectable = enabled
                .Where(x => circuitStates[x] != ProviderCircuitState.Open)
                .ToList();
            var pool = selectable.Count > 0 ? selectable : enabled;

            // Half-open sorts behind the healthy providers of its own tier and keeps that
            // tier, so a recovering primary is still tried ahead of a backup or block
            // account. A provider that may still be down should not stall a request a
            // healthy peer would serve. The failover walk reaches it and any command it
            // completes resets the breaker.
            var byTier = pool.OrderBy(x => x.ProviderType);
            var byRecovery = byTier.ThenBy(x =>
                circuitStates[x] == ProviderCircuitState.HalfOpen ? 1 : 0);
            var cascade = cascadeEnabled?.Invoke() == true;
            var prioritized = cascade
                ? byRecovery.ThenBy(EffectivePriority)
                : byRecovery;
            var byUsage = prioritized.ThenByDescending(x => GetRemainingBytes(x));
            // Prefer providers with more spare capacity. In cascade mode this is a
            // tie-break after EffectivePriority (which already soft-scores contention);
            // in pool mode it outranks learned speed so a full pool cannot monopolize.
            var capacityBalanced = byUsage.ThenByDescending(x => x.UnreservedConnections);
            var ordered = capacityBalanced
                .ThenBy(EstimatedDeliveryScore)
                .ToList();

            reserved = ordered.Count > 0 ? ordered[0] : null;
            reserved?.ReservePending();
            return ordered;
        }
    }

    /// <summary>
    /// Cascade sort key: configured priority, plus a soft contention score while spare
    /// capacity remains, plus a large demotion when fully saturated. Among providers that
    /// still have spare connections, a thinly-spared primary can lose to a same-tier peer
    /// with meaningfully more idle capacity without waiting for hard saturation.
    /// </summary>
    private static int EffectivePriority(MultiConnectionNntpClient provider)
    {
        const int saturationDemotion = 1 << 20;
        if (!provider.HasSpareConnection)
            return provider.Priority + saturationDemotion;

        // Smaller UnreservedConnections => slightly worse (still << saturationDemotion).
        var spare = Math.Clamp(provider.UnreservedConnections, 0, 1024);
        var contention = 1024 - spare;
        return provider.Priority + contention;
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
        var orderedProviders = SelectOrderedProviders(out var reserved);
        using var releasePending = new ScopeReleaser(() => reserved?.ReleasePending());
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
            var orderedProviders = SelectOrderedProviders(out var reserved);
            using var releasePending = new ScopeReleaser(() => reserved?.ReleasePending());
            var primary = orderedProviders.Count > 0 ? orderedProviders[0] : null;
            if (primary == null) yield break;
            effectiveDepth = ResolveDepth(primary, depth);
        }

        await foreach (var result in base.DecodedBodiesPipelinedAsync(
                           segmentIds, effectiveDepth, cancellationToken)
                           .WithCancellation(cancellationToken).ConfigureAwait(false))
            yield return result;
    }

    private static void InvokeCompletionCallback(
        Action<ArticleBodyResult>? callback,
        ArticleBodyResult result)
    {
        try
        {
            callback?.Invoke(result);
        }
        catch (Exception e)
        {
            Log.Warning(e, "NNTP completion callback failed");
        }
    }

    public override void Dispose()
    {
        foreach (var provider in providers)
            provider.Dispose();
        GC.SuppressFinalize(this);
    }
}
