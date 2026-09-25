using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using NzbWebDAV.Clients.Usenet.Concurrency;
using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Clients.Usenet.Contexts;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models;
using NzbWebDAV.Services;
using NzbWebDAV.Services.Metrics;
using NzbWebDAV.Services.StreamTrace;
using Serilog;
using UsenetSharp.Models;

namespace NzbWebDAV.Clients.Usenet;

/// <summary>
/// This client is responsible for delegating NNTP commands to a connection pool.
///   * The connection pool enforces a maximum number of allowed connections
///   * When a connection is available, the NNTP command executes immediately
///   * When a connection is not available, the NNTP command waits until a connection becomes available.
///   * When multiple commands are awaiting a connection, admission follows the
///     download-priority context on the caller's token: playback work (WebDAV reads and
///     playback verification) waits in the High lane, while queue and background
///     maintenance work waits in the Low lane. The pool's Streaming Priority odds decide
///     how the two lanes share connections while both are occupied.
/// </summary>
/// <param name="connectionPool"></param>
/// <param name="type"></param>
/// <param name="circuitBreaker"></param>
/// <param name="providerName">NNTP hostname used for connection/logging.</param>
/// <param name="metricsKey">Stable per-account metrics key (ProviderId).</param>
[SuppressMessage("ReSharper", "AccessToDisposedClosure")]
public class MultiConnectionNntpClient(
    ConnectionPool<INntpClient> connectionPool,
    ProviderType type,
    ProviderCircuitBreaker circuitBreaker,
    string providerName,
    long? byteLimit = null,
    long bytesUsedOffset = 0,
    int priority = 0,
    int? pipeliningDepth = null,
    string storageGroup = "",
    string? metricsKey = null,
    ProviderLatencyTracker? latencyTracker = null,
    int? maxTransferConnections = null,
    SemaphorePriorityOdds? priorityOdds = null,
    TimeSpan nntpReadTimeout = default,
    TimeSpan reconnectDelay = default
) : NntpClient
{
    private readonly ProviderConnectionAdmission? _connectionAdmission =
        maxTransferConnections is { } transferLimit
            ? new ProviderConnectionAdmission(
                () => connectionPool.EffectiveMaxConnections,
                transferLimit,
                priorityOdds)
            : null;
    internal Action<ConnectionLock<INntpClient>, Action>? AttachDisposeCallbackForTests
    { get; set; }

    public ProviderType ProviderType { get; } = type;
    public int Priority { get; } = priority;
    public string Host { get; } = providerName;
    /// <summary>
    /// Stable per-account key for bandwidth/usage metrics. Distinct from
    /// <see cref="Host"/> so multiple accounts on the same NNTP host do not share counters.
    /// </summary>
    public string MetricsKey { get; } = string.IsNullOrEmpty(metricsKey) ? providerName : metricsKey;
    public string StorageGroup { get; } = storageGroup;
    internal TimeSpan NntpReadTimeout { get; } = nntpReadTimeout;
    internal TimeSpan ReconnectDelay { get; } = reconnectDelay;

    private static readonly ConcurrentDictionary<string, int> TimeoutCounts = new();
    private static long _lastTimeoutFlushTicks = DateTime.UtcNow.Ticks;

    // Commands that transfer an article body. Only these feed the circuit breaker, so the
    // failure and success paths share one definition rather than each listing commands.
    private static bool IsBodyCommand(string name) => name is "BODY" or "ARTICLE";

    private static void IncrementTimeoutCount(string provider)
    {
        TimeoutCounts.AddOrUpdate(provider, 1, (_, existing) => existing + 1);
        MaybeFlushTimeoutCounts();
    }

    private static void MaybeFlushTimeoutCounts()
    {
        var nowTicks = DateTime.UtcNow.Ticks;
        var last = Interlocked.Read(ref _lastTimeoutFlushTicks);
        if (nowTicks - last < TimeSpan.FromSeconds(60).Ticks)
            return;
        if (Interlocked.CompareExchange(ref _lastTimeoutFlushTicks, nowTicks, last) != last)
            return;

        foreach (var key in TimeoutCounts.Keys)
        {
            if (TimeoutCounts.TryRemove(key, out var count) && count > 0)
                Log.Warning("[{ProviderName}] {Count} NNTP timeouts in the last 60 seconds", key, count);
        }
    }

    public int? ConfiguredPipeliningDepth { get; } = pipeliningDepth;
    // null or non-positive = uncapped. Routing reads these to decide whether
    // this provider should be skipped when it has exhausted its block.
    public long? ByteLimit { get; } = byteLimit;
    public long BytesUsedOffset { get; } = bytesUsedOffset;
    /// <summary>
    /// Claims the half-open probe slot as a side effect of being read. Use
    /// <see cref="GetCircuitBreakerSnapshot"/> to inspect state without altering it.
    /// </summary>
    public bool IsTripped => circuitBreaker.IsTripped;
    public ProviderCircuitBreakerSnapshot GetCircuitBreakerSnapshot() => circuitBreaker.GetSnapshot();
    public int MaxConnections => connectionPool.MaxConnections;
    public int EffectiveMaxConnections => connectionPool.EffectiveMaxConnections;
    internal int WarmConnectionFloor => connectionPool.WarmConnectionFloor;
    internal void RetirePool() => connectionPool.Retire();
    internal int PrewarmConnectionCapacity => Math.Min(
        EffectiveMaxConnections,
        _connectionAdmission?.GetSnapshot().EffectiveTransferLimit ?? EffectiveMaxConnections);
    internal bool IsHandshakeBackoffActive => connectionPool.IsHandshakeBackoffActive;
    public int? LearnedConnectionLimit => connectionPool.LearnedConnectionLimit;
    public int LiveConnections => connectionPool.LiveConnections;
    public int IdleConnections => connectionPool.IdleConnections;
    public int ActiveConnections => connectionPool.ActiveConnections;
    public int AvailableConnections => connectionPool.AvailableConnections;
    public ProviderConnectionAdmissionSnapshot? GetConnectionAdmissionSnapshot() =>
        _connectionAdmission?.GetSnapshot();
    public int InFlightConnections => ActiveConnections + PendingSelections;
    public ConnectionPoolChurn GetConnectionChurn() => connectionPool.GetChurn();

    public override Task PrewarmConnectionsAsync(
        int targetConnections,
        CancellationToken cancellationToken)
    {
        if (targetConnections <= 0
            || circuitBreaker.GetSnapshot().State != ProviderCircuitState.Closed
            || connectionPool.IsHandshakeBackoffActive)
        {
            return Task.CompletedTask;
        }

        return connectionPool.WarmToAsync(
            Math.Min(targetConnections, PrewarmConnectionCapacity),
            cancellationToken);
    }

    /// <summary>
    /// Applies new Streaming Priority odds to this provider's connection gate without
    /// rebuilding the pool.
    /// </summary>
    public void UpdatePriorityOdds(SemaphorePriorityOdds odds)
    {
        connectionPool.UpdatePriorityOdds(odds);
        _connectionAdmission?.UpdatePriorityOdds(odds);
    }

    private int _pendingSelections;
    private int _pendingTransferSelections;
    private int _pendingMetadataSelections;
    private int _retiredPoolWarningLogged;
    private static readonly long WarmFailureWarningIntervalMs =
        (long)TimeSpan.FromMinutes(5).TotalMilliseconds;
    private long _nextWarmFailureWarningAtMs;
    public int PendingSelections => Volatile.Read(ref _pendingSelections);
    internal void ReservePending(NntpOperation operation)
    {
        Interlocked.Increment(ref _pendingSelections);
        Interlocked.Increment(ref PendingSelectionsFor(ClassifyConnectionKind(operation)));
    }

    internal void ReleasePending(NntpOperation operation)
    {
        Interlocked.Decrement(ref PendingSelectionsFor(ClassifyConnectionKind(operation)));
        Interlocked.Decrement(ref _pendingSelections);
    }

    public int UnreservedConnections => Math.Max(0, AvailableConnections - PendingSelections);
    public bool HasSpareConnection => UnreservedConnections > 0;
    /// <summary>
    /// Unreserved capacity as a fraction of the configured pool size. Used by cascade
    /// ranking so unequal MaxConnections do not outweigh configured Priority.
    /// </summary>
    public double SpareFraction =>
        (double)UnreservedConnections / Math.Max(1, MaxConnections);

    internal int UnreservedConnectionsFor(NntpOperation operation)
    {
        var physicalSpare = circuitBreaker.AllowsIdleConnectionReuse
            ? Math.Max(0, connectionPool.IdleConnections - PendingSelections)
            : UnreservedConnections;
        var admission = _connectionAdmission?.GetRoutingState();
        if (admission is null)
            return physicalSpare;

        var routingState = admission.Value;
        var kind = ClassifyConnectionKind(operation);
        var pendingForKind = Volatile.Read(ref PendingSelectionsFor(kind));
        var kindSpare = kind == ProviderConnectionKind.Transfer
            ? routingState.EffectiveTransferLimit
              - routingState.ActiveTransferOperations
              - pendingForKind
            : routingState.MaxMetadataCapacity
              - routingState.ActiveMetadataOperations
              - pendingForKind;
        var combinedSpare = routingState.EffectiveProviderLimit
                            - routingState.ActiveTransferOperations
                            - routingState.ActiveMetadataOperations
                            - PendingSelections;
        return Math.Max(0, Math.Min(physicalSpare, Math.Min(kindSpare, combinedSpare)));
    }

    internal async Task<IDisposable?> AcquireKeepAliveAdmissionAsync(CancellationToken ct)
    {
        if (_connectionAdmission is null)
            return null;

        return await _connectionAdmission.AcquireAsync(
                ProviderConnectionKind.Metadata,
                SemaphorePriority.Low,
                ct)
            .ConfigureAwait(false);
    }

    private ref int PendingSelectionsFor(ProviderConnectionKind kind)
    {
        if (kind == ProviderConnectionKind.Transfer)
            return ref _pendingTransferSelections;
        return ref _pendingMetadataSelections;
    }

    public override Task ConnectAsync(string host, int port, bool useSsl, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("Please connect within the connectionFactory");
    }

    public override Task<UsenetResponse> AuthenticateAsync(string user, string pass,
        CancellationToken cancellationToken)
    {
        throw new NotSupportedException("Please authenticate within the connectionFactory");
    }

    public override Task<UsenetStatResponse> StatAsync(SegmentId segmentId, CancellationToken ct)
    {
        return RunWithConnection(
            "STAT",
            GetDownloadPriority(ct),
            (connection, _, commandCt) => connection.StatAsync(segmentId, commandCt),
            onConnectionReadyAgain: null,
            ct,
            transferAdmission: null
        );
    }

    public override Task<UsenetHeadResponse> HeadAsync(SegmentId segmentId, CancellationToken ct)
    {
        return RunWithConnection(
            "HEAD",
            GetDownloadPriority(ct),
            (connection, _, commandCt) => connection.HeadAsync(segmentId, commandCt),
            onConnectionReadyAgain: null,
            ct,
            transferAdmission: null
        );
    }

    public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(SegmentId segmentId, CancellationToken ct)
    {
        return RunWithConnection(
            "BODY",
            GetDownloadPriority(ct),
            (connection, onDone, commandCt) => connection.DecodedBodyAsync(segmentId, onDone, commandCt),
            onConnectionReadyAgain: null,
            ct,
            transferAdmission: null
        );
    }

    public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync
    (
        SegmentId segmentId,
        CancellationToken ct
    )
    {
        return RunWithConnection(
            "ARTICLE",
            GetDownloadPriority(ct),
            (connection, onDone, commandCt) => connection.DecodedArticleAsync(segmentId, onDone, commandCt),
            onConnectionReadyAgain: null,
            ct,
            transferAdmission: null
        );
    }

    public override Task<UsenetDateResponse> DateAsync(CancellationToken ct)
    {
        return RunWithConnection(
            "DATE",
            SemaphorePriority.Low,
            RequireSuccessfulDateAsync,
            onConnectionReadyAgain: null,
            ct
        );
    }

    private static async Task<UsenetDateResponse> RequireSuccessfulDateAsync(
        INntpClient connection,
        ArticleBodyCompletionHandler _,
        CancellationToken ct)
    {
        var response = await connection.DateAsync(ct).ConfigureAwait(false);
        if (response.ResponseType != UsenetResponseType.DateAndTime)
        {
            throw new RetryableDownloadException(
                $"Unexpected NNTP response to DATE: {response.ResponseMessage}");
        }

        return response;
    }

    public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync
    (
        SegmentId segmentId,
        ArticleBodyCompletionHandler? onConnectionReadyAgain,
        CancellationToken ct
    )
    {
        return RunWithConnection(
            "BODY",
            GetDownloadPriority(ct),
            (connection, onDone, commandCt) => connection.DecodedBodyAsync(segmentId, onDone, commandCt),
            onConnectionReadyAgain,
            ct,
            transferAdmission: null
        );
    }

    internal Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
        SegmentId segmentId,
        TransferAdmissionFailoverContext? transferAdmission,
        CancellationToken cancellationToken) =>
        DecodedBodyAsync(segmentId, null, transferAdmission, cancellationToken);

    internal Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
        SegmentId segmentId,
        TransferAdmissionFailoverContext? transferAdmission,
        CancellationToken cancellationToken) =>
        DecodedArticleAsync(segmentId, null, transferAdmission, cancellationToken);

    public override async Task<UsenetDecodedBodyBatch> DecodedBodiesAsync
    (
        IReadOnlyList<SegmentId> segmentIds,
        ArticleBodyCompletionHandler? onConnectionReadyAgain,
        CancellationToken ct
    ) => await DecodedBodiesCoreAsync(segmentIds, onConnectionReadyAgain, null, ct)
        .ConfigureAwait(false);

    internal Task<UsenetDecodedBodyBatch> DecodedBodiesAsync(
        IReadOnlyList<SegmentId> segmentIds,
        ArticleBodyCompletionHandler? onConnectionReadyAgain,
        TransferAdmissionFailoverContext? transferAdmission,
        CancellationToken cancellationToken) =>
        DecodedBodiesCoreAsync(segmentIds, onConnectionReadyAgain, transferAdmission, cancellationToken);

    private async Task<UsenetDecodedBodyBatch> DecodedBodiesCoreAsync(
        IReadOnlyList<SegmentId> segmentIds,
        ArticleBodyCompletionHandler? onConnectionReadyAgain,
        TransferAdmissionFailoverContext? transferAdmission,
        CancellationToken ct)
    {
        // Streaming reads carry a per-segment deadline so a stalled provider fails over
        // instead of holding a playback stream open for the raw NNTP stalled-read timeout
        // (configurable, 30s default).
        // It applies to issuing the batch, which is the part that waits on the provider;
        // the response streams are drained by the caller afterwards.
        var workload = DownloadWorkloadClassifier.Classify(ct);
        var operation = NntpOperation.Body;
        var streamingTimeout = ct.GetContext<StreamingTimeoutContext>();
        var retryCount = streamingTimeout?.MaxRetries ?? 1;
        var probeLease = CircuitProbeLease.None;
        while (true)
        {
            // Claim circuit admission before touching the pool so a rejected caller
            // never triggers a connect+auth handshake on a cold pool. Rejection is
            // retryable: MultiProviderNntpClient fails over to the next provider.
            if (!TryAdmitBeforeAcquisition(ref probeLease))
            {
                LogException(() => onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved));
                throw new CircuitAdmissionRejectedException();
            }

            ConnectionLock<INntpClient>? connectionLock = null;
            var freshConnection = false;
            var deferredCallback = new DeferredArticleBodyCallback();
            CancellationTokenSource? attemptCts = null;
            try
            {
                connectionLock = await AcquireConnectionLockAsync(
                    GetDownloadPriority(ct), workload, operation, probeLease, ct, transferAdmission)
                    .ConfigureAwait(false);
                freshConnection = !connectionLock.WasReused;

                var batchCt = ct;
                if (streamingTimeout != null)
                {
                    attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    attemptCts.CancelAfter(streamingTimeout.PerSegmentTimeout);
                    batchCt = attemptCts.Token;
                }

                var issuedAt = Stopwatch.GetTimestamp();
                var batch = await connectionLock.Connection.DecodedBodiesAsync(
                    segmentIds, deferredCallback.Invoke, batchCt).ConfigureAwait(false);

                var wrapped = new Task<UsenetDecodedBodyResponse>[batch.Responses.Count];
                for (var i = 0; i < batch.Responses.Count; i++)
                    wrapped[i] = RecordSuccessfulResponseAsync(batch.Responses[i], issuedAt, workload, operation);

                var callbackInvoked = 0;
                var callbackCompletion = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                attemptCts?.CancelAfter(Timeout.InfiniteTimeSpan);
                deferredCallback.Activate(OnConnectionReadyAgain);
                var completion = CompleteBatchLifecycleAsync(
                    batch.Completion, callbackCompletion.Task, attemptCts);
                attemptCts = null;
                return new UsenetDecodedBodyBatch
                {
                    Responses = wrapped,
                    Completion = completion,
                };

                void OnConnectionReadyAgain(ArticleBodyResult result, string? failureReason)
                {
                    if (Interlocked.Exchange(ref callbackInvoked, 1) != 0) return;
                    try
                    {
                        switch (result)
                        {
                            case ArticleBodyResult.Discarded:
                                LogException(() => connectionLock.Replace("pipelined-body-discarded"));
                                circuitBreaker.ReleaseProbe(probeLease);
                                result = ArticleBodyResult.NotRetrieved;
                                break;
                            case ArticleBodyResult.Retrieved:
                                circuitBreaker.RecordSuccess(probe: probeLease, freshConnection: freshConnection);
                                break;
                            case ArticleBodyResult.NotFound:
                                circuitBreaker.RecordArticleNotFound(probeLease, freshConnection);
                                break;
                            case ArticleBodyResult.Cancelled:
                                circuitBreaker.ReleaseProbe(probeLease);
                                break;
                            case ArticleBodyResult.NotRetrieved:
                                // Seek/abort cancels mid-pipeline; UsenetSharp reports NotRetrieved
                                // (socket unsafe to reuse). Replace the connection but do not treat
                                // client cancellation as provider health failure.
                                LogException(() => connectionLock.Replace("pipelined-body-not-retrieved"));
                                if (!ct.IsCancellationRequested)
                                    RecordProviderFailure(failureReason is null
                                        ? $"pipeline-callback-{result}"
                                        : $"pipeline-callback-{result} ({failureReason})", probeLease);
                                else
                                    circuitBreaker.ReleaseProbe(probeLease);
                                break;
                            default:
                                RecordProviderFailure(failureReason is null
                                    ? $"pipeline-callback-{result}"
                                    : $"pipeline-callback-{result} ({failureReason})", probeLease);
                                LogException(() => connectionLock.Replace($"pipelined-body-{result}"));
                                break;
                        }

                        LogException(connectionLock.Dispose);
                        LogException(() => onConnectionReadyAgain?.Invoke(result, failureReason));
                    }
                    finally
                    {
                        callbackCompletion.TrySetResult();
                    }
                }
            }
            catch (Exception e) when (
                streamingTimeout != null
#pragma warning disable CA2016 // CA2016: classify cancellation regardless of the ambient token -- forwarding it would misclassify cancellations from internal timeout/child tokens
                && e.IsCancellationException()
#pragma warning restore CA2016
                && !ct.IsCancellationRequested && e is not OutOfMemoryException)
            {
                // Per-segment deadline fired while the caller is still reading. The
                // connection has an in-flight pipeline → replace it and try again, so a
                // single slow provider does not decide what the stream delivers.
                deferredCallback.Discard();
                LogException(() => connectionLock?.Replace("streaming-timeout-pipelined-BODY"));
                LogException(() => connectionLock?.Dispose());
                if (retryCount > 0)
                {
                    Log.Debug(
                        "Streaming timeout executing pipelined nntp BODY commands after {Timeout}s. Retrying with a new connection ({Retries} left).",
                        streamingTimeout.PerSegmentTimeout.TotalSeconds, retryCount);
                    retryCount--;
                    continue;
                }

                RecordProviderFailure("streaming-timeout-pipelined-BODY", probeLease);
                Log.Warning(
                    "Streaming timeout executing pipelined nntp BODY commands for provider {Provider} after {Timeout}s. No retries left.",
                    Host, streamingTimeout.PerSegmentTimeout.TotalSeconds);
                LogException(() => onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved));
                throw new TimeoutException(
                    "Timeout executing pipelined nntp BODY commands after " +
                    $"{streamingTimeout.MaxRetries + 1} attempts.");
            }
            catch (Exception exception) when (exception is CircuitAdmissionRejectedException
                or ProviderTransferAdmissionTimeoutException)
            {
                circuitBreaker.ReleaseProbe(probeLease);
                LogException(() => onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved));
                throw;
            }
            catch (Exception e) when (e.IsCancellationException(ct) && e is not OutOfMemoryException)
            {
                deferredCallback.Discard();
                // Caller cancellation records no breaker outcome; free the probe slot.
                circuitBreaker.ReleaseProbe(probeLease);
                LogException(() => connectionLock?.Replace("caller-cancelled-pipelined-BODY"));
                LogException(() => connectionLock?.Dispose());
                LogException(() => onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved));
                throw;
            }
            catch (NntpClientRetiredException)
            {
                deferredCallback.Discard();
                circuitBreaker.ReleaseProbe(probeLease);
                // Normally this branch is reached while waiting to acquire and the lock is
                // null. Keep cleanup here for the concurrent-dispose edge where the command
                // itself observes disposal after acquisition.
                LogException(() => connectionLock?.Replace("retired-pipelined-BODY"));
                LogException(() => connectionLock?.Dispose());
                LogException(() => onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved));
                throw;
            }
            catch (Exception e) when (e.TryGetCausingException(out UsenetArticleNotFoundException? _) && e is not OutOfMemoryException)
            {
                // Permanently missing / invalid segment ids are not connection failures.
                deferredCallback.Discard();
                circuitBreaker.RecordArticleNotFound(probeLease, freshConnection);
                LogException(() => connectionLock?.Dispose());
                LogException(() => onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotFound));
                throw;
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                deferredCallback.Discard();
                var wasReused = connectionLock?.WasReused ?? false;
                if (connectionLock is null)
                {
                    RecordConnectionAcquisitionFailure(e, "pipeline-get-connection", ct, probeLease);
                }
                else if (!wasReused)
                {
                    RecordProviderFailure($"pipeline-setup-{e.GetType().Name}", probeLease);
                }
                LogException(() => connectionLock?.Replace("pipelined-BODY-command-failure"));
                LogException(() => connectionLock?.Dispose());

                // A pooled connection may have been closed server-side while idle;
                // its failure says nothing about provider health. Drain and retry.
                if (wasReused)
                {
                    Log.Debug(e,
                        "Pooled connection for provider {Provider} failed pipelined NNTP BODY commands. Retrying with another connection.",
                        Host);
                    continue;
                }

                // A failure that latched the breaker just proved the provider broken;
                // surface the original error instead of retrying into a circuit rejection.
                if (retryCount > 0 && !circuitBreaker.IsLatched)
                {
                    Log.Debug(e,
                        "Error executing pipelined NNTP BODY commands for provider {Provider}. Retrying with a new connection.",
                        Host);
                    retryCount--;
                    continue;
                }

                e.LogWarningKnownOrStack(
                    "Error executing pipelined NNTP BODY commands for provider {Provider}.",
                    Host);
                LogException(() => onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved));
                throw;
            }
            finally
            {
                attemptCts?.Dispose();
            }
        }
    }

    public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync
    (
        SegmentId segmentId,
        ArticleBodyCompletionHandler? onConnectionReadyAgain,
        CancellationToken ct
    )
    {
        return RunWithConnection(
            "ARTICLE",
            GetDownloadPriority(ct),
            (connection, onDone, commandCt) => connection.DecodedArticleAsync(segmentId, onDone, commandCt),
            onConnectionReadyAgain,
            ct,
            transferAdmission: null
        );
    }

    internal Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
        SegmentId segmentId,
        ArticleBodyCompletionHandler? onConnectionReadyAgain,
        TransferAdmissionFailoverContext? transferAdmission,
        CancellationToken cancellationToken) =>
        RunWithConnection(
            "BODY",
            GetDownloadPriority(cancellationToken),
            (connection, onDone, commandToken) =>
                connection.DecodedBodyAsync(segmentId, onDone, commandToken),
            onConnectionReadyAgain,
            cancellationToken,
            transferAdmission: transferAdmission);

    internal Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
        SegmentId segmentId,
        ArticleBodyCompletionHandler? onConnectionReadyAgain,
        TransferAdmissionFailoverContext? transferAdmission,
        CancellationToken cancellationToken) =>
        RunWithConnection(
            "ARTICLE",
            GetDownloadPriority(cancellationToken),
            (connection, onDone, commandToken) =>
                connection.DecodedArticleAsync(segmentId, onDone, commandToken),
            onConnectionReadyAgain,
            cancellationToken,
            transferAdmission: transferAdmission);

    private async Task<T> RunWithConnection<T>
    (
        string name,
        SemaphorePriority priority,
        Func<INntpClient, ArticleBodyCompletionHandler, CancellationToken, Task<T>> command,
        ArticleBodyCompletionHandler? onConnectionReadyAgain,
        CancellationToken ct,
        int retryCount = 1,
        TransferAdmissionFailoverContext? transferAdmission = null
    ) where T : UsenetResponse
    {
        var workload = DownloadWorkloadClassifier.Classify(ct);
        var operation = LatencyNames.FromCommandName(name);
        var streamingTimeout = ct.GetContext<StreamingTimeoutContext>();
        if (streamingTimeout != null)
            retryCount = streamingTimeout.MaxRetries;

        var probeLease = CircuitProbeLease.None;
        while (true)
        {
            // Claim circuit admission before touching the pool so a rejected caller
            // never triggers a connect+auth handshake on a cold pool. Rejection is
            // retryable: MultiProviderNntpClient fails over to the next provider.
            if (!TryAdmitBeforeAcquisition(ref probeLease))
            {
                LogException(() => onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved));
                throw new CircuitAdmissionRejectedException();
            }

            ConnectionLock<INntpClient>? connectionLock = null;
            var freshConnection = false;
            try
            {
                connectionLock = await AcquireConnectionLockAsync(
                    priority, workload, operation, probeLease, ct, transferAdmission)
                    .ConfigureAwait(false);
                if (connectionLock is null)
                    throw new InvalidOperationException("Connection acquisition returned no lock.");
                freshConnection = !connectionLock.WasReused;
            }
            catch (Exception exception) when (exception is CircuitAdmissionRejectedException
                or ProviderTransferAdmissionTimeoutException)
            {
                circuitBreaker.ReleaseProbe(probeLease);
                LogException(() => onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved));
                throw;
            }
            catch (Exception e) when (e.IsCancellationException(ct) && e is not OutOfMemoryException)
            {
                // Caller cancellation records no breaker outcome; free the probe slot.
                circuitBreaker.ReleaseProbe(probeLease);
                LogException(() => connectionLock?.Dispose());
                LogException(() => onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved));
                throw;
            }
            catch (NntpClientRetiredException)
            {
                circuitBreaker.ReleaseProbe(probeLease);
                LogException(() => onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved));
                throw;
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                RecordConnectionAcquisitionFailure(e, "get-connection", ct, probeLease);
                LogException(() => connectionLock?.Replace($"connection-acquisition-failure-{name}"));
                LogException(() => connectionLock?.Dispose());
                // A failure that latched the breaker just proved the provider broken;
                // surface the original error instead of retrying into a circuit rejection.
                if (retryCount > 0 && !circuitBreaker.IsLatched)
                {
                    Log.Debug(e, "Error getting connection-lock for provider {Provider}. Retrying with a new connection.", Host);
                    retryCount--;
                    continue;
                }

                e.LogWarningKnownOrStack("Error getting connection-lock for provider {Provider}.", Host);
                LogException(() => onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved));
                throw;
            }

            // AcquireConnectionLockAsync either throws or returns a lock; this guard
            // only documents that invariant for null-state analysis.
            if (connectionLock is null)
            {
                LogException(() => onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved));
                throw new InvalidOperationException("Connection acquisition returned no lock.");
            }

            T? result;
            var deferredCallback = new DeferredArticleBodyCallback();
            CancellationTokenSource? attemptCts = null;
            CancellationTokenSource? bodyCts = null;
            try
            {
                var commandCt = ct;
                if (streamingTimeout != null)
                {
                    attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    attemptCts.CancelAfter(streamingTimeout.PerSegmentTimeout);
                    commandCt = attemptCts.Token;
                }

                var responseStarted = Stopwatch.GetTimestamp();
                result = await command(connectionLock.Connection, deferredCallback.Invoke, commandCt)
                    .ConfigureAwait(false);
                if (result?.Success ?? false)
                {
                    latencyTracker?.Record(
                        MetricsKey,
                        LatencyPhase.Response,
                        workload,
                        operation,
                        Stopwatch.GetElapsedTime(responseStarted));
                    if (IsBodyCommand(name) && attemptCts is not null)
                    {
                        attemptCts.CancelAfter(Timeout.InfiniteTimeSpan);
                        bodyCts = attemptCts;
                        attemptCts = null;
                    }
                }
            }
            catch (Exception e) when (
                streamingTimeout != null
                && e.IsCancellationException()
                && !ct.IsCancellationRequested && e is not OutOfMemoryException)
            {
                // Per-segment CancelAfter fired while the caller is still alive.
                // The connection has an in-flight command → NotRetrieved (replace).
                // Do not invoke onConnectionReadyAgain on retry: the outer download
                // permit stays held across attempts (same pattern as other retries).
                deferredCallback.Discard();
                LogException(() => connectionLock?.Replace($"streaming-timeout-{name}"));
                LogException(() => connectionLock?.Dispose());
                if (retryCount > 0)
                {
                    Log.Debug(
                        "Streaming timeout executing nntp {Command} command after {Timeout}s. Retrying with a new connection ({Retries} left).",
                        name, streamingTimeout.PerSegmentTimeout.TotalSeconds, retryCount);
                    retryCount--;
                    continue;
                }

                // Exhausted the streaming-timeout retry budget — count toward the
                // breaker once per segment (not per attempt) so chronically-slow
                // providers still trip without over-counting a single segment.
                RecordProviderFailure($"streaming-timeout-{name}", probeLease);
                Log.Warning(
                    "Streaming timeout executing nntp {Command} command for provider {Provider} after {Timeout}s. No retries left.",
                    name, Host, streamingTimeout.PerSegmentTimeout.TotalSeconds);
                LogException(() => onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved));
                throw new TimeoutException(
                    $"Timeout executing nntp {name} command after {streamingTimeout.MaxRetries + 1} attempts.");
            }
            catch (Exception e) when (e.IsCancellationException(ct) && e is not OutOfMemoryException)
            {
                deferredCallback.Discard();
                // Caller cancellation records no breaker outcome; free the probe slot.
                circuitBreaker.ReleaseProbe(probeLease);
                LogException(() => connectionLock?.Replace($"caller-cancelled-{name}"));
                LogException(() => connectionLock?.Dispose());
                LogException(() => onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved));
                throw;
            }
            catch (Exception e) when (e.TryGetCausingException(out UsenetArticleNotFoundException? _) && e is not OutOfMemoryException)
            {
                deferredCallback.Discard();
                circuitBreaker.RecordArticleNotFound(probeLease, freshConnection);
                LogException(() => connectionLock?.Dispose());
                LogException(() => onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotFound));
                throw;
            }
            catch (Exception e) when (IsBodyCommand(name) && e.TryGetCausingException(out TimeoutException? _) && e is not OutOfMemoryException)
            {
                // Read-timeout on BODY/ARTICLE means the provider stopped responding
                // mid-command. A fresh socket to the same provider is unlikely to fare
                // any better, and burning another timeout retrying here just doubles
                // the wait before MultiProviderNntpClient can fall over to the next
                // provider. Replace the socket (the read may have left partial bytes
                // on the wire) and propagate so the outer provider loop moves on.
                IncrementTimeoutCount(Host);
                deferredCallback.Discard();
                RecordProviderFailure($"read-timeout-{name}", probeLease);
                LogException(() => connectionLock?.Replace($"read-timeout-{name}"));
                LogException(() => connectionLock?.Dispose());
                LogException(() => onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved));
                throw;
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                deferredCallback.Discard();
                var wasReused = connectionLock.WasReused;
                // STAT, HEAD, and DATE failures do not feed a closed circuit because their
                // successes intentionally do not reset its BODY failure sampling window.
                if (!wasReused && IsBodyCommand(name))
                    RecordProviderFailure($"cmd-setup-{name}-{e.GetType().Name}", probeLease);
                LogException(() => connectionLock?.Replace($"command-failure-{name}"));
                LogException(() => connectionLock?.Dispose());

                // A pooled connection may have been closed server-side while idle;
                // its failure says nothing about provider health. Drain and retry.
                if (wasReused)
                {
                    Log.Debug(e, "Pooled connection for provider {Provider} failed nntp {Command} command. Retrying with another connection.", Host, name);
                    continue;
                }

                // A failure that latched the breaker just proved the provider broken;
                // surface the original error instead of retrying into a circuit rejection.
                if (retryCount > 0 && !circuitBreaker.IsLatched)
                {
                    Log.Debug(e, "Error executing nntp {Command} command for provider {Provider}. Retrying with a new connection.", name, Host);
                    retryCount--;
                    continue;
                }

                // A non-BODY command selected while the provider was half-open is a
                // recovery probe. Once its retries are exhausted, release the probe slot
                // and reopen the circuit instead of leaving it claimed until timeout.
                if (!IsBodyCommand(name) && circuitBreaker.IsLatched)
                    RecordProviderFailure($"cmd-{name}-{e.GetType().Name}", probeLease);

                e.LogWarningKnownOrStack(
                    "Error executing nntp {Command} command for provider {Provider}.", name, Host);
                LogException(() => onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved));
                throw;
            }
            finally
            {
                attemptCts?.Dispose();
            }

            // stat, head, and date — do not feed the circuit breaker.
            // STAT/HEAD/DATE successes were resetting BODY failure streaks and
            // preventing trips under mixed traffic (STAT-ok/BODY-fail providers).
            if (!IsBodyCommand(name))
            {
                // Once latched the breaker is no longer tracking a streak, it is waiting
                // for proof the provider answers at all. A provider that sees little body
                // traffic would otherwise stay latched with nothing able to close it.
                // Reachability is not proof the download path works, so the cooldown
                // ladder survives the close; only a BODY success resets it. Constant
                // health-check STATs must not pin a BODY-broken provider at 60s forever.
                if (circuitBreaker.IsLatched)
                    circuitBreaker.RecordSuccess(
                        resetsCooldownLadder: false,
                        probe: probeLease,
                        freshConnection: freshConnection);
                deferredCallback.Discard();
                LogException(() => connectionLock?.Dispose());
            }

            // body and article
            else if (!(result?.Success ?? false))
            {
                deferredCallback.Discard();
                if (result is not null && UsenetArticleAvailability.IsDefinitiveMissing(result))
                {
                    // 430/451: the article is gone from this provider — a clean miss,
                    // not a provider failure. The connection is safe to reuse.
                    circuitBreaker.RecordArticleNotFound(probeLease, freshConnection);
                    LogException(() => connectionLock?.Dispose());
                    LogException(() => onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotFound));
                }
                else
                {
                    // Any other non-success (e.g. 500) is a provider-side failure, not a
                    // missing article: replace the socket (the wire state is unknown) and
                    // report NotRetrieved. MultiProviderNntpClient records the unexpected
                    // response and the next attempt re-selects a provider.
                    RecordProviderFailure($"cmd-{name}-unexpected-response", probeLease);
                    LogException(() => connectionLock?.Replace($"unexpected-response-{name}"));
                    LogException(() => connectionLock?.Dispose());
                    LogException(() => onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved));
                }
            }
            else
            {
                var callbackInvoked = 0;
                deferredCallback.Activate((articleBodyResult, failureReason) =>
                {
                    if (Interlocked.Exchange(ref callbackInvoked, 1) != 0) return;
                    using var completedBodyCts = bodyCts;

                    if (articleBodyResult == ArticleBodyResult.Discarded)
                    {
                        LogException(() => connectionLock?.Replace($"body-callback-{name}-discarded"));
                        circuitBreaker.ReleaseProbe(probeLease);
                        articleBodyResult = ArticleBodyResult.NotRetrieved;
                    }
                    else if (articleBodyResult == ArticleBodyResult.NotRetrieved)
                    {
                        LogException(() => connectionLock?.Replace($"body-callback-{name}-NotRetrieved"));
                        // Client abort (seek) must not trip the provider circuit breaker.
                        if (!ct.IsCancellationRequested)
                            RecordProviderFailure(failureReason is null
                                ? $"body-callback-{name}-NotRetrieved"
                                : $"body-callback-{name}-NotRetrieved ({failureReason})", probeLease);
                        else
                            circuitBreaker.ReleaseProbe(probeLease);
                    }
                    else if (articleBodyResult == ArticleBodyResult.Retrieved)
                    {
                        circuitBreaker.RecordSuccess(probe: probeLease, freshConnection: freshConnection);
                    }
                    else if (articleBodyResult == ArticleBodyResult.NotFound)
                    {
                        circuitBreaker.RecordArticleNotFound(probeLease, freshConnection);
                    }
                    else if (articleBodyResult == ArticleBodyResult.Cancelled)
                    {
                        circuitBreaker.ReleaseProbe(probeLease);
                    }

                    LogException(() => connectionLock?.Dispose());
                    LogException(() => onConnectionReadyAgain?.Invoke(articleBodyResult, failureReason));
                });
            }

            return result!;
        }
    }

    public override IAsyncEnumerable<PipelinedStatResult> StatsPipelinedAsync(
        IReadOnlyList<string> segmentIds, int depth, CancellationToken cancellationToken)
        => RunPipelinedStatAsync(
            c => c.StatsPipelinedAsync(segmentIds, depth, cancellationToken),
            cancellationToken);

    public override IAsyncEnumerable<PipelinedBodyResult> DecodedBodiesPipelinedAsync(
        IReadOnlyList<string> segmentIds, int depth, CancellationToken cancellationToken)
        => RunPipelinedAsync(
            c => c.DecodedBodiesPipelinedAsync(segmentIds, depth, cancellationToken),
            NntpOperation.PipelinedBody,
            cancellationToken);

    public override IAsyncEnumerable<PipelinedArticleResult> DecodedArticlesPipelinedAsync(
        IReadOnlyList<string> segmentIds, int depth, CancellationToken cancellationToken)
        => RunPipelinedAsync(
            c => c.DecodedArticlesPipelinedAsync(segmentIds, depth, cancellationToken),
            NntpOperation.PipelinedArticle,
            cancellationToken);

    /// <summary>
    /// STAT pipeline lease: inherits download priority from the caller's token (High for
    /// playback verification, Low for hosted health), no circuit-breaker updates for STAT
    /// outcomes (matching single StatAsync) — connection-establishment failures still trip
    /// because they are command-agnostic. Still replaces the connection on hard failure
    /// because UsenetSharp poisons it mid-batch. Acquisition goes through the shared helper
    /// so the pool wait is instrumented and arbitrated like every other command.
    /// </summary>
    private async IAsyncEnumerable<PipelinedStatResult> RunPipelinedStatAsync(
        Func<INntpClient, IAsyncEnumerable<PipelinedStatResult>> batchFactory,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var workload = DownloadWorkloadClassifier.Classify(cancellationToken);
        var operation = NntpOperation.PipelinedStat;
        var probeLease = CircuitProbeLease.None;
        if (!TryAdmitBeforeAcquisition(ref probeLease))
            throw new CircuitAdmissionRejectedException();
        var connectionLock = await AcquireConnectionLockRecordingFailureAsync(
            GetDownloadPriority(cancellationToken), workload, operation, probeLease, cancellationToken)
            .ConfigureAwait(false);
        var completed = false;
        try
        {
            await using var enumerator = batchFactory(connectionLock.Connection)
                .GetAsyncEnumerator(cancellationToken);
            while (true)
            {
                PipelinedStatResult current;
                try
                {
                    var moveStarted = Stopwatch.GetTimestamp();
                    if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                    {
                        completed = true;
                        break;
                    }

                    current = enumerator.Current;
                    latencyTracker?.Record(
                        MetricsKey,
                        LatencyPhase.Response,
                        workload,
                        operation,
                        Stopwatch.GetElapsedTime(moveStarted));
                }
#pragma warning disable CA2016 // CA2016: classify cancellation regardless of the ambient token -- forwarding it would misclassify cancellations from internal timeout/child tokens
                catch (Exception e) when (!e.IsCancellationException() && e is not OutOfMemoryException)
#pragma warning restore CA2016
                {
                    // Do not RecordFailure — STAT must not feed the breaker — but the
                    // connection is poisoned and must not return to the pool.
                    connectionLock.Replace("pipelined-STAT-failure");
                    throw;
                }

                yield return current;
            }
        }
        finally
        {
            if (!completed) connectionLock.Replace("pipelined-STAT-incomplete");
            circuitBreaker.ReleaseProbe(probeLease);
            connectionLock.Dispose();
        }
    }

    private async IAsyncEnumerable<T> RunPipelinedAsync<T>(
        Func<INntpClient, IAsyncEnumerable<T>> batchFactory,
        NntpOperation operation,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var workload = DownloadWorkloadClassifier.Classify(cancellationToken);
        var priority = GetDownloadPriority(cancellationToken);
        // Claim circuit admission before touching the pool so a rejected caller never
        // triggers a connect+auth handshake on a cold pool. Rejection is retryable:
        // MultiProviderNntpClient fails over to the next provider.
        var probeLease = CircuitProbeLease.None;
        if (!TryAdmitBeforeAcquisition(ref probeLease))
            throw new CircuitAdmissionRejectedException();
        ConnectionLock<INntpClient> connectionLock;
        try
        {
            connectionLock = await AcquireConnectionLockRecordingFailureAsync(
                    priority, workload, operation, probeLease, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception e) when (e.IsCancellationException(cancellationToken) || e is NntpClientRetiredException)
        {
            // Cancellation and pool retirement record no breaker outcome; free the
            // probe slot so the next caller can probe immediately.
            circuitBreaker.ReleaseProbe(probeLease);
            throw;
        }

        var completed = false;
        var freshConnection = !connectionLock.WasReused;
        try
        {
            await using var enumerator = batchFactory(connectionLock.Connection)
                .GetAsyncEnumerator(cancellationToken);
            while (true)
            {
                T current;
                try
                {
                    var moveStarted = Stopwatch.GetTimestamp();
                    if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                    {
                        completed = true;
                        break;
                    }

                    current = enumerator.Current;
                    latencyTracker?.Record(
                        MetricsKey,
                        LatencyPhase.Response,
                        workload,
                        operation,
                        Stopwatch.GetElapsedTime(moveStarted));
                }
#pragma warning disable CA2016 // CA2016: classify cancellation regardless of the ambient token -- forwarding it would misclassify cancellations from internal timeout/child tokens
                catch (Exception e) when (!e.IsCancellationException() && e is not OutOfMemoryException)
#pragma warning restore CA2016
                {
                    RecordProviderFailure($"pipelined-enum-{e.GetType().Name}", probeLease);
                    connectionLock.Replace($"{operation}-failure");
                    throw;
                }

                RecordPipelinedItemOutcome(current, probeLease, freshConnection);
                yield return current;
            }
        }
        finally
        {
            if (!completed) connectionLock.Replace($"{operation}-incomplete");
            // A no-op once an item outcome resolved the probe; frees the slot when the
            // enumeration ended or was abandoned before any outcome was recorded.
            circuitBreaker.ReleaseProbe(probeLease);
            connectionLock.Dispose();
        }
    }

    private static SemaphorePriority GetDownloadPriority(CancellationToken ct)
    {
        if (ct.GetContext<DownloadPriorityContext>() is { } downloadPriority)
            return downloadPriority.Priority;

        return ct.GetContext<HealthCheckAdmissionContext>()?.Priority
            == HealthCheckAdmissionPriority.Queue
                ? SemaphorePriority.High
                : SemaphorePriority.Low;
    }

    /// <summary>
    /// Borrows a pooled connection, attributing the wait to latency histograms and (when
    /// active) the current stream-trace range so pool saturation is distinguishable from
    /// provider response time.
    /// </summary>
    private async Task<ConnectionLock<INntpClient>> AcquireConnectionLockAsync(
        SemaphorePriority priority,
        DownloadWorkload workload,
        NntpOperation operation,
        CircuitProbeLease probeLease,
        CancellationToken ct,
        TransferAdmissionFailoverContext? explicitTransferAdmission = null)
    {
        var traceRange = MultiProviderNntpClient.CurrentStreamTraceRange;
        var started = Stopwatch.GetTimestamp();
        ConnectionLock<INntpClient>? connectionLock = null;
        OperationLeaseGroup? operationLeases = null;
        CancellationTokenSource? acquisitionWait = null;
        TimeSpan? waitTimeout = null;
        var acquisition = circuitBreaker.BeginAcquisition(
            probeLease, allowIdleReuse: connectionPool.HasIdleConnections);
        var waitPhase = "ProviderAdmission";
        var returnConnectionLock = false;
        var latencyRecorded = false;
        try
        {
            var admissionKind = ClassifyConnectionKind(operation);
            var failoverContext = explicitTransferAdmission
                ?? ct.GetContext<TransferAdmissionFailoverContext>();
            waitTimeout = admissionKind == ProviderConnectionKind.Transfer
                && failoverContext is not null
                && (failoverContext.RequireBoundedWait || failoverContext.HasAlternativeCapacity())
                    ? failoverContext.WaitTimeout
                    : null;
            CancellationToken GetOrCreateWaitToken(string phase)
            {
                waitPhase = phase;
                acquisition.ThrowIfRejected();
                if (acquisitionWait is not null)
                    return acquisitionWait.Token;

                acquisitionWait = CancellationTokenSource.CreateLinkedTokenSource(
                    ct, acquisition.CircuitCancellationToken);
                if (waitTimeout is not { } timeout)
                    return acquisitionWait.Token;

                var remaining = timeout - Stopwatch.GetElapsedTime(started);
                if (remaining <= TimeSpan.Zero)
                    throw new ProviderTransferAdmissionTimeoutException(Host, timeout, waitPhase);
                acquisitionWait.CancelAfter(remaining);
                return acquisitionWait.Token;
            }

            if (ct.GetContext<HealthCheckAdmissionContext>() is { } healthCheckContext)
            {
                waitPhase = "HealthAdmission";
                acquisition.ThrowIfRejected();
                operationLeases = new OperationLeaseGroup();
                operationLeases.Add(
                    await healthCheckContext.Gate
                        .AcquireAsync(healthCheckContext.Priority, GetOrCreateWaitToken(waitPhase))
                        .ConfigureAwait(false));
                acquisition.ThrowIfRejected();
            }

            if (_connectionAdmission is not null)
            {
                operationLeases ??= new OperationLeaseGroup();
                waitPhase = "ProviderAdmission";
                acquisition.ThrowIfRejected();
#pragma warning disable CA2000 // OperationLeaseGroup takes ownership immediately below.
                var immediateLease = _connectionAdmission.TryAcquire(admissionKind);
                if (immediateLease is not null)
                {
                    operationLeases.Add(immediateLease);
                    immediateLease = null;
                }
                else if (acquisition.IdleOnly)
                {
                    throw new CircuitAdmissionRejectedException();
                }
                else
                {
                    operationLeases.Add(
                        await _connectionAdmission.AcquireAsync(
                            admissionKind, priority, GetOrCreateWaitToken(waitPhase))
                            .ConfigureAwait(false));
                }
#pragma warning restore CA2000
                acquisition.ThrowIfRejected();
            }

            connectionLock = circuitBreaker.RequiresFreshConnectionProbe
                && circuitBreaker.OwnsAdmittedProbe(probeLease)
                ? await connectionPool.GetFreshConnectionLockAsync(
                    priority, ct, acquisitionWait?.Token, started, waitTimeout, acquisition)
                    .ConfigureAwait(false)
                : await connectionPool.GetConnectionLockAsync(
                    priority, ct, acquisitionWait?.Token, started, waitTimeout, acquisition)
                    .ConfigureAwait(false);
            if (operationLeases is not null)
            {
                if (AttachDisposeCallbackForTests is { } attachForTests)
                    attachForTests(connectionLock, operationLeases.Dispose);
                else
                    connectionLock.AttachDisposeCallback(operationLeases.Dispose);
                operationLeases = null;
            }

            var elapsed = Stopwatch.GetElapsedTime(started);
            latencyTracker?.Record(MetricsKey, LatencyPhase.PoolWait, workload, operation, elapsed);
            latencyRecorded = true;
            StreamTrace.TryConnectionAcquired(traceRange, elapsed, connectionLock.WasReused);
            returnConnectionLock = true;
            return connectionLock;
        }
        catch (OperationCanceledException) when (
            acquisitionWait?.IsCancellationRequested == true
            && !ct.IsCancellationRequested
            && !connectionPool.IsDisposed
            && _connectionAdmission?.IsDisposed != true)
        {
            acquisition.ThrowIfRejected();
            if (waitTimeout is not { } timeout)
                throw;
            throw new ProviderTransferAdmissionTimeoutException(
                Host,
                timeout,
                waitPhase);
        }
        catch (Exception e) when (IsRetiredPoolAcquisitionFailure(e) && e is not OutOfMemoryException)
        {
            throw CreateRetiredPoolException(e);
        }
        finally
        {
            try
            {
                if (!latencyRecorded)
                    latencyTracker?.Record(
                        MetricsKey,
                        LatencyPhase.PoolWait,
                        workload,
                        operation,
                        Stopwatch.GetElapsedTime(started));
                if (!returnConnectionLock)
                    connectionLock?.Dispose();
            }
            finally
            {
                acquisitionWait?.Dispose();
                operationLeases?.Dispose();
            }
        }
    }

    /// <summary>
    /// Owns operation gates in acquisition order and releases them in reverse order.
    /// Disposal is idempotent so a failed callback attachment can safely fall back to
    /// local cleanup.
    /// </summary>
    internal sealed class OperationLeaseGroup : IDisposable
    {
        private IDisposable? _first;
        private IDisposable? _second;
        private int _disposed;

        public void Add(IDisposable lease)
        {
            ArgumentNullException.ThrowIfNull(lease);
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
            if (_first is null)
                _first = lease;
            else if (_second is null)
                _second = lease;
            else
                throw new InvalidOperationException(
                    "A connection operation cannot own more than two admission leases.");
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
                return;

            var second = Interlocked.Exchange(ref _second, null);
            var first = Interlocked.Exchange(ref _first, null);
            LogException(() => second?.Dispose());
            LogException(() => first?.Dispose());
        }
    }

    internal static ProviderConnectionKind ClassifyConnectionKind(NntpOperation operation) =>
        operation is NntpOperation.Body
            or NntpOperation.Article
            or NntpOperation.PipelinedBody
            or NntpOperation.PipelinedArticle
            ? ProviderConnectionKind.Transfer
            : ProviderConnectionKind.Metadata;

    /// <summary>
    /// Acquisition wrapper for the pipelined enumerable paths, which have no retry loop
    /// of their own: a connection-establishment failure trips the breaker immediately
    /// (mirrors RunWithConnection) so an unreachable provider fails over instead of
    /// burning full connect timeouts on every batch. Caller cancellation and pool
    /// retirement are not provider-health failures and pass through untouched.
    /// </summary>
    private async Task<ConnectionLock<INntpClient>> AcquireConnectionLockRecordingFailureAsync(
        SemaphorePriority priority,
        DownloadWorkload workload,
        NntpOperation operation,
        CircuitProbeLease probeLease,
        CancellationToken ct)
    {
        try
        {
            return await AcquireConnectionLockAsync(priority, workload, operation, probeLease, ct)
                .ConfigureAwait(false);
        }
        catch (NntpClientRetiredException)
        {
            circuitBreaker.ReleaseProbe(probeLease);
            throw;
        }
        catch (Exception exception) when (exception is CircuitAdmissionRejectedException
            or ProviderTransferAdmissionTimeoutException)
        {
            circuitBreaker.ReleaseProbe(probeLease);
            throw;
        }
        catch (OperationCanceledException)
        {
            circuitBreaker.ReleaseProbe(probeLease);
            throw;
        }
#pragma warning disable CA2016 // CA2016: classify cancellation regardless of the ambient token -- forwarding it would misclassify cancellations from internal timeout/child tokens
        catch (Exception e) when (!e.IsCancellationException() && e is not OutOfMemoryException)
#pragma warning restore CA2016
        {
            RecordConnectionAcquisitionFailure(e, "pipelined-get-connection", ct, probeLease);
            throw;
        }
    }

    /// <summary>
    /// Records a failed pool expansion. A cold pool cannot serve traffic, so it is
    /// still benched immediately. Once a provider has established sockets, one failed
    /// replacement does not prove the provider is unreachable; corroborate it through
    /// the normal failure window instead. A latched breaker always re-trips immediately
    /// so its half-open probe cannot return a still-failing provider to rotation.
    /// </summary>
    private void RecordConnectionAcquisitionFailure(
        Exception exception,
        string operation,
        CancellationToken ct,
        CircuitProbeLease probeLease = default)
    {
        // A client abort (seek/stop) mid-connect can surface as an IOException or
        // SocketException rather than a cancellation exception; it must not affect
        // provider health.
        if (ct.IsCancellationRequested)
            return;

        if (exception is CircuitAdmissionRejectedException or ProviderTransferAdmissionTimeoutException)
        {
            circuitBreaker.ReleaseProbe(probeLease);
            return;
        }

        if (exception is ConnectionOpenTimeoutException { FactoryStarted: false })
        {
            circuitBreaker.ReleaseProbe(probeLease);
            return;
        }

        var reason = $"{operation}-{exception.GetType().Name}";
        if (exception.TryGetKnownErrorMessage(out var knownReason))
            reason = $"{reason}: {knownReason}";

        if (exception is ConnectionOpenTimeoutException { FactoryStarted: true } openTimeout)
        {
            RecordProviderConnectionFailure(
                $"{reason}-phase-{openTimeout.Phase}",
                probeLease,
                requiresFreshConnectionProbe: true);
            return;
        }

        if (circuitBreaker.IsLatched || connectionPool.LiveConnections == 0)
            RecordProviderConnectionFailure(reason, probeLease);
        else
            RecordProviderFailure(reason);
    }

    /// <summary>
    /// Classifies a failed warm-up open (warm-floor refill or read-start warm-up). Warm-up has no
    /// waiting caller, so while the provider still has an established connection the failure is
    /// left to the pool's handshake backoff and only logged. A cold pool keeps the immediate trip
    /// so an unreachable provider is benched before the first request needs it.
    /// </summary>
    internal void RecordWarmConnectionFailure(Exception exception, bool factoryStarted)
    {
        if (exception is CircuitAdmissionRejectedException or ProviderTransferAdmissionTimeoutException)
            return;

        // The budget ran out before TCP/TLS/AUTHINFO even started; that says nothing about the provider.
        if (exception is ConnectionOpenTimeoutException && !factoryStarted)
            return;

        if (connectionPool.LiveConnections > 0)
        {
            LogWarmConnectionFailureOnLivePool(exception);
            return;
        }

        if (exception is ConnectionOpenTimeoutException timeout)
        {
            RecordProviderConnectionFailure(
                $"warm-open-timeout-phase-{timeout.Phase}: {timeout.Message}",
                requiresFreshConnectionProbe: true);
            return;
        }

        RecordProviderFailure($"warm-connection-{exception.GetType().Name}");
    }

    private void LogWarmConnectionFailureOnLivePool(Exception exception)
    {
        var reason = exception.TryGetKnownErrorMessage(out var known) ? known : exception.Message;
        var now = Environment.TickCount64;
        var next = Volatile.Read(ref _nextWarmFailureWarningAtMs);
        var warn = now >= next
                   && Interlocked.CompareExchange(
                       ref _nextWarmFailureWarningAtMs, now + WarmFailureWarningIntervalMs, next) == next;
        if (warn)
        {
            Log.Warning(
                "Warm-up could not open a new connection to {Provider}; established connections keep serving and warm-up retries after the pool's handshake backoff. Reason: {Reason}",
                Host, reason);
            return;
        }

        Log.Debug(
            "Warm-up open failure for {Provider} (warning throttled). Reason: {Reason}",
            Host, reason);
    }

    private void RecordProviderConnectionFailure(
        string reason,
        CircuitProbeLease? probe = null,
        bool requiresFreshConnectionProbe = false) =>
        circuitBreaker.RecordConnectionFailure(
            reason,
            GetPoolDiagnostics(),
            probe,
            requiresFreshConnectionProbe);

    private void RecordProviderFailure(string reason, CircuitProbeLease? probe = null) =>
        circuitBreaker.RecordFailure(reason, GetPoolDiagnostics(), probe);

    private ProviderCircuitPoolDiagnostics GetPoolDiagnostics() =>
        new(connectionPool.LiveConnections, connectionPool.IdleConnections, connectionPool.ActiveConnections);

    /// <summary>
    /// Pool disposal (client retirement / shutdown) is not a provider-health failure.
    /// Stale requests must abandon without retrying the same dead pool or feeding the breaker.
    /// </summary>
    private bool IsRetiredPoolAcquisitionFailure(Exception e) =>
        (connectionPool.IsDisposed || _connectionAdmission?.IsDisposed == true)
        && e is ObjectDisposedException or OperationCanceledException;

    private NntpClientRetiredException CreateRetiredPoolException(Exception inner)
    {
        if (Interlocked.Exchange(ref _retiredPoolWarningLogged, 1) == 0)
        {
            Log.Warning(
                "Connection pool for provider {Provider} retired while requests were waiting. " +
                "Abandoning stale requests without retrying or penalizing provider health.",
                Host);
        }
        return new NntpClientRetiredException(
            $"Connection pool for provider '{Host}' retired while the request was waiting.",
            inner);
    }

    private static async Task CompleteBatchLifecycleAsync(
        Task transportCompletion,
        Task callbackCompletion,
        CancellationTokenSource? attemptCts)
    {
        using var _ = attemptCts;
        await BatchLifecycle.ObserveAllAsync(transportCompletion, callbackCompletion).ConfigureAwait(false);
    }

    private async Task<UsenetDecodedBodyResponse> RecordSuccessfulResponseAsync(
        Task<UsenetDecodedBodyResponse> responseTask,
        long issuedAt,
        DownloadWorkload workload,
        NntpOperation operation)
    {
        var response = await responseTask.ConfigureAwait(false);
        if (response.Success)
        {
            latencyTracker?.Record(
                MetricsKey,
                LatencyPhase.Response,
                workload,
                operation,
                Stopwatch.GetElapsedTime(issuedAt));
        }
        return response;
    }

    /// <summary>
    /// Claims circuit admission before any physical connection acquisition, so a
    /// rejected caller never triggers a connect+auth handshake on a cold pool.
    /// A closed-circuit admission (<see cref="CircuitProbeLease.None"/>) is not
    /// exclusive, so it is re-verified on every attempt and a circuit that tripped
    /// between attempts still rejects the retry. A half-open probe lease continues
    /// only while it still owns the probe slot.
    /// </summary>
    internal bool CanReuseIdleConnection =>
        circuitBreaker.AllowsIdleConnectionReuse && connectionPool.HasIdleConnections;

    private bool TryAdmitBeforeAcquisition(ref CircuitProbeLease probeLease)
    {
        if (probeLease.IsNone)
        {
            if (circuitBreaker.TryAdmit(out probeLease))
                return true;
            return CanReuseIdleConnection;
        }

        return circuitBreaker.OwnsAdmittedProbe(probeLease);
    }

    private void RecordPipelinedItemOutcome<T>(
        T current,
        CircuitProbeLease probeLease,
        bool freshConnection)
    {
        switch (current)
        {
            // A definitive miss (430/451) is a clean answer, not provider damage.
            case PipelinedBodyResult { Found: false, DefinitivelyMissing: true }
                or PipelinedArticleResult { Found: false, DefinitivelyMissing: true }:
                circuitBreaker.RecordArticleNotFound(probeLease, freshConnection);
                break;
            // A non-definitive not-found (e.g. a segment-id mismatch) is a protocol
            // failure: it must not close a half-open circuit as if the article were
            // absent, nor count as success.
            case PipelinedBodyResult { Found: false } or PipelinedArticleResult { Found: false }:
                RecordProviderFailure("pipelined-item-unexpected-miss", probeLease);
                break;
            default:
                circuitBreaker.RecordSuccess(probe: probeLease, freshConnection: freshConnection);
                break;
        }
    }

    private static void LogException(Action? action)
    {
        try
        {
            action?.Invoke();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Log.Warning(e, "Unhandled exception");
        }
    }

    /// <summary>
    /// Retryable so <see cref="MultiProviderNntpClient"/> failovers. Distinct from a
    /// command/connect failure so local retry loops do not replace a healthy socket
    /// or count this as provider-health damage.
    /// </summary>

    public override void Dispose()
    {
        _connectionAdmission?.Dispose();
        connectionPool.Dispose();
        GC.SuppressFinalize(this);
    }
}
