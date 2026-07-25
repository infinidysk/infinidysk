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
    StreamTraceBuffer? streamTrace = null,
    ActiveReadRegistry? activeReadRegistry = null,
    ArticleMissNegativeCache? articleMissCache = null
) : NntpClient, INntpConnectionStats
{
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
        return RunFromPoolWithBackup(
            x => x.StatAsync(segmentId, cancellationToken), segmentId, cancellationToken);
    }

    public override Task<UsenetHeadResponse> HeadAsync(SegmentId segmentId, CancellationToken cancellationToken)
    {
        return RunFromPoolWithBackup(
            x => x.HeadAsync(segmentId, cancellationToken), segmentId, cancellationToken);
    }

    public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync
    (
        SegmentId segmentId,
        CancellationToken cancellationToken
    )
    {
        return RunFromPoolWithBackup(
            x => x.DecodedBodyAsync(segmentId, cancellationToken), segmentId, cancellationToken);
    }

    public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync
    (
        SegmentId segmentId,
        CancellationToken cancellationToken
    )
    {
        return RunFromPoolWithBackup(
            x => x.DecodedArticleAsync(segmentId, cancellationToken), segmentId, cancellationToken);
    }

    public override Task<UsenetDateResponse> DateAsync(CancellationToken cancellationToken)
    {
        return RunFromPoolWithBackup(x => x.DateAsync(cancellationToken), articleId: null, cancellationToken);
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
            segmentId,
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
                Task previousFallbackCompletion = Task.CompletedTask;
                for (var index = 0; index < responses.Length; index++)
                {
                    var fallbackCompletion = new TaskCompletionSource(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    responses[index] = ResolveBatchResponseAsync(
                        primaryBatch.Responses[index],
                        segmentIds[index],
                        provider,
                        fallbackProviders,
                        previousFallbackCompletion,
                        fallbackCompletion,
                        coordinator,
                        cancellationToken);
                    previousFallbackCompletion = fallbackCompletion.Task;
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
        Task previousFallbackCompletion,
        TaskCompletionSource fallbackCompletion,
        BatchCallbackCoordinator coordinator,
        CancellationToken cancellationToken)
    {
        var fallbackCompletionOwnedByTransfer = false;
        var primaryStopwatch = Stopwatch.StartNew();
        List<(string Host, SegmentFetch.FetchStatus Reason)>? priorMisses = null;
        // Fresh per article resolution. Do not mark on the initial batch 430 so the
        // intentional primary retry below is never skipped by its own miss.
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
                var reason = ClassifyAndRecordFailure(
                    primaryProvider.MetricsKey, e, primaryStopwatch.ElapsedMilliseconds, 0);
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

            if (response != null && UsenetArticleAvailability.IsDefinitiveMissing(response))
            {
                primaryStopwatch.Stop();
                RecordFetch(primaryProvider.MetricsKey, SegmentFetch.FetchStatus.Missing,
                    primaryStopwatch.ElapsedMilliseconds, 0);
                (priorMisses ??= []).Add((primaryProvider.MetricsKey, SegmentFetch.FetchStatus.Missing));
            }

            // Retry the primary provider once before falling back. Even a definitive miss
            // (430 / provider 451) can be transient when a provider routes across spool nodes.
            // Anything else (a faulted response task, or a stale connection's buffered
            // goodbye line such as "400 idle timeout") remains a connection-level failure.
            //
            // Coherence with ArticleMissNegativeCache (#639 / #637):
            // - Never MarkMissing the primary on this initial batch 430 — that would prime
            //   the cache and cause the intentional re-probe below to skip itself.
            // - If a prior request already cached the primary/group miss, skip the re-probe
            //   and walk fallbacks immediately (that is the point of cross-request caching).
            // - MarkMissing only from definitive misses inside the retry/fallback loop below.
            IReadOnlyList<MultiConnectionNntpClient> retryProviders;
            var primaryCachedMiss = IsCachedMissing(segmentId, primaryProvider);
            if (response != null &&
                UsenetArticleAvailability.IsDefinitiveMissing(response) &&
                primaryCachedMiss)
            {
                var primaryGroup = NormalizeStorageGroup(primaryProvider.StorageGroup);
                if (primaryGroup.Length > 0) missingGroups.Add(primaryGroup);
                retryProviders = fallbackProviders;
            }
            else
            {
                retryProviders = [primaryProvider, .. fallbackProviders];
                if (response == null || !UsenetArticleAvailability.IsDefinitiveMissing(response))
                {
                    if (response != null)
                    {
                        lastException = ExceptionDispatchInfo.Capture(
                            new UsenetUnexpectedResponseException(segmentId, response.ResponseMessage));
                    }
                }
            }

            await previousFallbackCompletion.WaitAsync(cancellationToken)
                .ConfigureAwait(false);
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

                if (IsCachedMissing(segmentId, provider))
                {
                    Log.Debug(
                        "Skipping provider `{Host}` for article `{SegmentId}` — " +
                        "cached as missing. Reason: article-miss-cache",
                        provider.Host, segmentId);
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
                        fallbackCompletionOwnedByTransfer = true;
                        deferredCallback.Activate(result =>
                        {
                            try
                            {
                                coordinator.CompleteTransfer(result);
                            }
                            finally
                            {
                                fallbackCompletion.TrySetResult();
                            }
                        });
                    }
                    else
                    {
                        RecordFetch(provider.MetricsKey, SegmentFetch.FetchStatus.Missing,
                            stopwatch.ElapsedMilliseconds, priorMisses?.Count ?? 0);
                        (priorMisses ??= []).Add((provider.MetricsKey, SegmentFetch.FetchStatus.Missing));
                        if (UsenetArticleAvailability.IsDefinitiveMissing(response))
                        {
                            if (group.Length > 0) missingGroups.Add(group);
                            MarkCachedMissing(segmentId, provider);
                        }
                        deferredCallback.Discard();
                        coordinator.CompleteAttempt();
                    }

                    lastException = null;
                }
                catch (Exception e) when (!e.IsCancellationException(cancellationToken))
                {
                    stopwatch.Stop();
                    var reason = ClassifyAndRecordFailure(
                        provider.MetricsKey, e, stopwatch.ElapsedMilliseconds,
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
            if (!fallbackCompletionOwnedByTransfer)
            {
                fallbackCompletion.TrySetResult();
            }

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
            segmentId,
            onConnectionReadyAgain,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<T> RunStreamingFromPoolWithBackup<T>(
        Func<INntpClient, Action<ArticleBodyResult>, Task<T>> task,
        UsenetResponseType successResponseType,
        SegmentId segmentId,
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

            if (IsCachedMissing(segmentId, provider))
            {
                Log.Debug(
                    "Skipping provider `{Host}` for article `{SegmentId}` — " +
                    "cached as missing. Reason: article-miss-cache",
                    provider.Host, segmentId);
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
                    MarkCachedMissing(segmentId, provider);
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
                var reason = ClassifyAndRecordFailure(
                    provider.MetricsKey, e, stopwatch.ElapsedMilliseconds, attemptIndex);
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
        if (orderedProviders.Count == 0)
            throw new Exception("There are no usenet providers configured.");
        // All providers were skipped (negative cache / storage-group) without a probe.
        throw new UsenetArticleNotFoundException(segmentId.ToString()!);
    }

    private async Task<T> RunFromPoolWithBackup<T>
    (
        Func<INntpClient, Task<T>> task,
        SegmentId? articleId,
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

            if (articleId is { } segmentId && IsCachedMissing(segmentId, provider))
            {
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
                var reason = ClassifyAndRecordFailure(
                    provider.MetricsKey, e, stopwatch.ElapsedMilliseconds, attemptIndex);
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
            LogExhaustedProviders(lastAttemptedProvider?.Host, lastException!.SourceException);
            lastException.Throw();
        }
        if (lastNoArticleResult is not null) return lastNoArticleResult;
        if (orderedProviders.Count == 0)
            throw new Exception("There are no usenet providers configured.");
        // All providers were skipped (negative cache / storage-group) without a probe.
        if (articleId is { } exhaustedId)
            throw new UsenetArticleNotFoundException(exhaustedId.ToString()!);
        throw new Exception("There are no usenet providers configured.");
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

    private static string CacheKey(SegmentId segmentId, MultiConnectionNntpClient provider) =>
        ArticleMissNegativeCache.BuildKey(segmentId.ToString()!, provider.MetricsKey, provider.StorageGroup);

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

    /// <summary>
    /// Classifies <paramref name="exception"/>, records the SegmentFetch row, and for residual
    /// <see cref="SegmentFetch.FetchStatus.Other"/> logs the concrete exception type so support
    /// packs stay diagnosable without a schema change.
    /// </summary>
    private SegmentFetch.FetchStatus ClassifyAndRecordFailure(
        string metricsKey, Exception exception, long durationMs, int retries)
    {
        var status = ClassifyException(exception);
        RecordFetch(metricsKey, status, durationMs, retries);
        if (status == SegmentFetch.FetchStatus.Other)
        {
            Log.Debug(
                exception,
                "Unclassified Usenet segment fetch failure. Host={Host} ExceptionType={ExceptionType}",
                metricsKey, exception.GetType().FullName);
        }
        return status;
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
        if (ex.TryGetCausingException<TimeoutException>(out _))
            return SegmentFetch.FetchStatus.Timeout;

        if (ex.TryGetCausingException<UsenetCorruptArticleException>(out _))
            return SegmentFetch.FetchStatus.Corrupt;

        if (ex.TryGetCausingException<CouldNotLoginToUsenetException>(out _) ||
            ex.TryGetCausingException<UnauthorizedAccessException>(out _))
            return SegmentFetch.FetchStatus.Auth;

        if (ex.TryGetCausingException<CouldNotConnectToUsenetException>(out _) ||
            ex.TryGetCausingException<System.Net.Sockets.SocketException>(out _) ||
            ex.TryGetCausingException<System.IO.IOException>(out _))
            return SegmentFetch.FetchStatus.Network;

        if (ex.TryGetCausingException<UsenetUnexpectedResponseException>(out _))
            return SegmentFetch.FetchStatus.Protocol;

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
            // In pool mode, capacity must outrank learned speed. Otherwise a warmed
            // provider can keep winning the speed tie-break after its pool is full,
            // leaving requests queued there while peer providers remain idle.
            var capacityBalanced = cascade
                ? byUsage
                : byUsage.ThenByDescending(x => x.UnreservedConnections);
            var ordered = capacityBalanced
                .ThenBy(EstimatedDeliveryScore)
                .ToList();

            reserved = ordered.Count > 0 ? ordered[0] : null;
            reserved?.ReservePending();
            return ordered;
        }
    }

    private static int EffectivePriority(MultiConnectionNntpClient provider)
    {
        const int saturationDemotion = 1 << 20;
        return provider.Priority + (provider.HasSpareConnection ? 0 : saturationDemotion);
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
