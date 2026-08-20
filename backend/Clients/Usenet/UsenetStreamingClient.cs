using NzbWebDAV.Clients.Usenet.Concurrency;
using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models.Metrics;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models;
using NzbWebDAV.Services;
using NzbWebDAV.Services.Metrics;
using NzbWebDAV.Services.Repair;
using NzbWebDAV.Services.StreamTrace;
using NzbWebDAV.Websocket;
using Serilog;
using UsenetSharp.Models;

namespace NzbWebDAV.Clients.Usenet;

public class UsenetStreamingClient : WrappingNntpClient
{
    private readonly Lock _configChangeLock = new();
    private readonly RepairPatchStore? _repairPatchStore;

    public UsenetStreamingClient(
        ConfigManager configManager,
        WebsocketManager websocketManager,
        ProviderUsageTracker usageTracker,
        MetricsWriter metricsWriter,
        ProviderBytesTracker bytesTracker,
        StreamTraceBuffer streamTrace,
        ActiveReadRegistry activeReadRegistry,
        ArticleMissNegativeCache? articleMissCache = null,
        ProviderLatencyTracker? latencyTracker = null,
        ConcurrentReadTracker? concurrentReadTracker = null,
        RepairPatchStore? repairPatchStore = null)
#pragma warning disable CA2000 // the client chain transfers to the base class and is disposed with this instance
        : base(CreateDownloadingNntpClient(
#pragma warning restore CA2000
            configManager, websocketManager, usageTracker, metricsWriter, bytesTracker,
            streamTrace, activeReadRegistry, articleMissCache, latencyTracker, concurrentReadTracker,
            repairPatchStore))
    {
        _repairPatchStore = repairPatchStore;
        // when config changes, create a new MultiProviderClient to use instead.
        configManager.OnConfigChanged += (_, configEventArgs) =>
        {
            var providersChanged = configEventArgs.ChangedConfig.ContainsKey(ConfigKeys.UsenetProviders);
            var streamingPriorityChanged =
                configEventArgs.ChangedConfig.ContainsKey(ConfigKeys.UsenetStreamingPriority);

            // if unrelated config changed, do nothing
            if (!providersChanged && !streamingPriorityChanged) return;

            lock (_configChangeLock)
            {
                try
                {
                    if (providersChanged)
                    {
                        // update the connection-pool according to the new config. New pools are
                        // built with the current odds, so a save that changes both needs no
                        // separate update (and must not touch the retired client).
                        var newUsenetClient = CreateDownloadingNntpClient(
                            configManager, websocketManager, usageTracker, metricsWriter, bytesTracker,
                            streamTrace, activeReadRegistry, articleMissCache, latencyTracker,
                            concurrentReadTracker, _repairPatchStore);
                        ReplaceUnderlyingClient(newUsenetClient);
                        return;
                    }

                    // Streaming Priority alone only re-arms the provider gates; rebuilding pools
                    // would drop healthy TLS connections mid-playback.
                    UpdateProviderPriorityOdds(configManager.GetStreamingPriority());
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    // Keep the previous (working) client and let remaining OnConfigChanged
                    // subscribers run — a throw from a multicast handler aborts the rest.
                    Log.Error(e, "Failed to rebuild usenet client after provider config change; keeping previous client");
                }
            }
        };
    }

    /// <summary>
    /// Test hook: wrap a scripted inner client without building provider pools.
    /// </summary>
    internal UsenetStreamingClient(INntpClient inner, RepairPatchStore? repairPatchStore = null)
        : base(inner)
    {
        _repairPatchStore = repairPatchStore;
    }

    private static HeaderCachingNntpClient CreateDownloadingNntpClient
    (
        ConfigManager configManager,
        WebsocketManager websocketManager,
        ProviderUsageTracker usageTracker,
        MetricsWriter metricsWriter,
        ProviderBytesTracker bytesTracker,
        StreamTraceBuffer streamTrace,
        ActiveReadRegistry activeReadRegistry,
        ArticleMissNegativeCache? articleMissCache,
        ProviderLatencyTracker? latencyTracker,
        ConcurrentReadTracker? concurrentReadTracker,
        RepairPatchStore? repairPatchStore
    )
    {
#pragma warning disable CA2000 // wrapped by DownloadingNntpClient below; the returned client chain is disposed with this instance
        var multiProviderClient = CreateMultiProviderClient(
#pragma warning restore CA2000
            configManager, websocketManager, usageTracker, metricsWriter, bytesTracker,
            streamTrace, activeReadRegistry, articleMissCache, latencyTracker, concurrentReadTracker);
#pragma warning disable CA2000 // ownership transfers to the wrapping/returned client chain, disposed with this instance
        var downloadingClient = new DownloadingNntpClient(multiProviderClient, configManager, latencyTracker);
#pragma warning restore CA2000
        INntpClient inner = downloadingClient;
        if (configManager.IsSegmentCacheEnabled())
        {
            try
            {
#pragma warning disable CA2000 // on construction failure the inner chain is returned unwrapped; the returned chain is disposed with this instance
                inner = new SegmentCacheNntpClient(
#pragma warning restore CA2000
                    downloadingClient,
                    configManager.GetSegmentCachePath(),
                    configManager.GetSegmentCacheMaxBytes(),
                    usageTracker,
                    metricsWriter
                );
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
            {
                Log.Warning(e, "Segment cache disabled: failed to initialise at {Path}.",
                    configManager.GetSegmentCachePath());
            }
        }

        if (repairPatchStore != null)
        {
#pragma warning disable CA2000 // ownership transfers to the wrapping/returned client chain, disposed with this instance
            inner = new RepairedSegmentNntpClient(inner, repairPatchStore);
#pragma warning restore CA2000
        }

        // Always wrap with header caching so seek probes reuse immutable yEnc headers
        // even when the optional on-disk segment body cache is disabled.
        return new HeaderCachingNntpClient(inner);
    }

    internal void UpdateProviderPriorityOdds(SemaphorePriorityOdds odds)
    {
        if (WrappingNntpClient.Unwrap(InnerClient) is MultiProviderNntpClient multi)
            multi.UpdateConnectionPriorityOdds(odds);
    }

    public IReadOnlyList<ProviderCircuitRuntimeSnapshot> GetProviderCircuitSnapshots()
    {
        return WrappingNntpClient.Unwrap(InnerClient) is MultiProviderNntpClient multi
            ? multi.GetProviderCircuitSnapshots()
            : Array.Empty<ProviderCircuitRuntimeSnapshot>();
    }

    public IReadOnlyList<ProviderConnectionSnapshot> GetProviderConnectionSnapshots()
    {
        return WrappingNntpClient.Unwrap(InnerClient) is MultiProviderNntpClient multi
            ? multi.GetProviderConnectionSnapshots()
            : Array.Empty<ProviderConnectionSnapshot>();
    }

    public Task ProbeLatchedProvidersAsync(CancellationToken cancellationToken)
    {
        return WrappingNntpClient.Unwrap(InnerClient) is MultiProviderNntpClient multi
            ? multi.ProbeLatchedProvidersAsync(cancellationToken)
            : Task.CompletedTask;
    }

    private static MultiProviderNntpClient CreateMultiProviderClient
    (
        ConfigManager configManager,
        WebsocketManager websocketManager,
        ProviderUsageTracker usageTracker,
        MetricsWriter metricsWriter,
        ProviderBytesTracker bytesTracker,
        StreamTraceBuffer streamTrace,
        ActiveReadRegistry activeReadRegistry,
        ArticleMissNegativeCache? articleMissCache,
        ProviderLatencyTracker? latencyTracker,
        ConcurrentReadTracker? concurrentReadTracker
    )
    {
        var providerConfig = configManager.GetUsenetProviderConfig();
        // Seed the tracker from the persisted metrics rollup so the limit gate
        // is accurate before the first article fetch. Fire-and-forget — the
        // helper logs and swallows DB errors so a metrics outage can't keep
        // the streaming client from coming up. Limit enforcement degrades
        // gracefully to "uncapped until seed completes".
        _ = ProviderUsageHelper.SeedTrackerAsync(bytesTracker, providerConfig);

        var connectionPoolStats = new ConnectionPoolStats(providerConfig, websocketManager);
        var idleTimeoutSeconds = configManager.GetIdleConnectionTimeoutSeconds();
        var streamingPriority = configManager.GetStreamingPriority();
        var tripDetector = new CorrelatedTripDetector();
        var providerClients = providerConfig.Providers
            .Select((provider, index) => CreateProviderClient(
                provider,
                connectionPoolStats.GetOnConnectionPoolChanged(index),
                idleTimeoutSeconds,
                configManager.IsWarmConnectionsEnabled()
                    ? configManager.GetWarmConnectionsFloor(provider.MaxConnections)
                    : 0,
                metricsWriter,
                streamingPriority,
                latencyTracker,
                tripDetector
            ))
            .ToList();
        return new MultiProviderNntpClient(
            providerClients, usageTracker, metricsWriter, bytesTracker,
            cascadeEnabled: configManager.IsCascadeEnabled,
            retryPrimaryOnMiss: configManager.IsCascadeRetryPrimaryOnMiss,
            streamTrace: streamTrace,
            activeReadRegistry: activeReadRegistry,
            articleMissCache: articleMissCache,
            connectionPoolStats: connectionPoolStats,
            concurrentReadTracker: concurrentReadTracker);
    }

    private static MultiConnectionNntpClient CreateProviderClient
    (
        UsenetProviderConfig.ConnectionDetails connectionDetails,
        EventHandler<ConnectionPoolStats.ConnectionPoolChangedEventArgs> onConnectionPoolChanged,
        int idleTimeoutSeconds,
        int warmConnectionFloor,
        MetricsWriter metricsWriter,
        SemaphorePriorityOdds? streamingPriority = null,
        ProviderLatencyTracker? latencyTracker = null,
        CorrelatedTripDetector? tripDetector = null
    )
    {
        var maxConnections = connectionDetails.MaxConnections;
        if (maxConnections < 1)
        {
            Log.Warning(
                "Provider '{Provider}' has MaxConnections={MaxConnections}; clamping to 1 so the connection pool can start",
                string.IsNullOrWhiteSpace(connectionDetails.Nickname)
                    ? connectionDetails.Host
                    : connectionDetails.Nickname,
                maxConnections);
            maxConnections = 1;
        }

        if (ShouldWarnCleartextCredentials(connectionDetails.UseSsl, connectionDetails.User))
        {
            var label = string.IsNullOrWhiteSpace(connectionDetails.Nickname)
                ? connectionDetails.Host
                : connectionDetails.Nickname;
            Log.Warning(
                "Provider '{Provider}' uses a cleartext connection (no TLS) with credentials; the password is sent unencrypted. Prefer port 563 with SSL.",
                label);
        }

        if (connectionDetails.UseSsl && connectionDetails.SkipTlsVerification)
        {
            var label = string.IsNullOrWhiteSpace(connectionDetails.Nickname)
                ? connectionDetails.Host
                : connectionDetails.Nickname;
            Log.Warning(
                "Provider '{Provider}' skips TLS certificate verification. The connection is encrypted but vulnerable to server impersonation.",
                label);
        }

#pragma warning disable CA2000 // the pool is owned by the provider's MultiConnectionNntpClient and disposed on provider config change
        var connectionPool = CreateNewConnectionPool(
#pragma warning restore CA2000
            maxConnections: maxConnections,
            connectionFactory: ct => CreateNewConnection(connectionDetails, ct),
            onConnectionPoolChanged,
            idleTimeoutSeconds,
            warmConnectionFloor,
            streamingPriority,
            connectionLimitDetector: ex =>
                UsenetConnectionLimitDetector.TryLearn(ex, out var learned) ? learned : null,
            onConnectionLimitLearned: (learned, effective) =>
            {
                var label = string.IsNullOrWhiteSpace(connectionDetails.Nickname)
                    ? connectionDetails.Host
                    : connectionDetails.Nickname;
                Log.Warning(
                    "Provider '{Provider}' reported a server-side connection limit of {Learned} " +
                    "(configured MaxConnections={Configured}). Pool width reduced to {Effective} until restart. " +
                    "Lower MaxConnections in settings to make this permanent.",
                    label, learned, maxConnections, effective);
            }
        );
        // Ensure a metrics key even if startup backfill was skipped somehow.
        if (connectionDetails.ProviderId == Guid.Empty)
            connectionDetails.ProviderId = Guid.NewGuid();
        var metricsKey = UsenetProviderIdentity.MetricsKey(connectionDetails);
        var circuitBreaker = new ProviderCircuitBreaker(
            connectionDetails.Host,
            transition =>
            {
                metricsWriter.RecordEvent(new MetricEvent
                {
                    At = transition.AtUnixMilliseconds,
                    Kind = "circuit",
                    Tag1 = metricsKey,
                    Tag2 = transition.State == ProviderCircuitTransitionState.Open
                        ? "open"
                        : "closed",
                    Num = transition.Cooldown is { } cooldown
                        ? (long)cooldown.TotalMilliseconds
                        : null,
                });
                tripDetector?.OnTransition(metricsKey, transition);
            },
            coalesceFailureBursts: true);
        // Only providers that can carry traffic participate in correlation; a Disabled
        // provider never trips and would wedge the "all tripped" condition forever.
        if (connectionDetails.Type != ProviderType.Disabled)
        {
            tripDetector?.Register(
                metricsKey,
                connectionDetails.Host,
                () => circuitBreaker.CapCooldown(TimeSpan.FromSeconds(10)));
        }
        return new MultiConnectionNntpClient(
            connectionPool,
            connectionDetails.Type,
            circuitBreaker,
            connectionDetails.Host,
            connectionDetails.ByteLimit,
            connectionDetails.BytesUsedOffset,
            connectionDetails.Priority,
            connectionDetails.PipeliningDepth,
            connectionDetails.StorageGroup,
            metricsKey,
            latencyTracker
        );
    }

    private static ConnectionPool<INntpClient> CreateNewConnectionPool
    (
        int maxConnections,
        Func<CancellationToken, ValueTask<INntpClient>> connectionFactory,
        EventHandler<ConnectionPoolStats.ConnectionPoolChangedEventArgs> onConnectionPoolChanged,
        int idleTimeoutSeconds,
        int warmConnectionFloor,
        SemaphorePriorityOdds? streamingPriority = null,
        Func<Exception, int?>? connectionLimitDetector = null,
        Action<int, int>? onConnectionLimitLearned = null
    )
    {
        var idleTimeout = TimeSpan.FromSeconds(idleTimeoutSeconds);
        Log.Information(
            "Creating NNTP connection pool max={Max} idleTimeout={IdleTimeoutSeconds}s warmFloor={WarmFloor} streamingPriority={StreamingPriority}",
            maxConnections, idleTimeoutSeconds, warmConnectionFloor, streamingPriority?.HighPriorityOdds);
        var connectionPool = new ConnectionPool<INntpClient>(
            maxConnections, connectionFactory, idleTimeout, streamingPriority,
            connectionLimitDetector, onConnectionLimitLearned, warmConnectionFloor,
            KeepAliveAsync);
        connectionPool.OnConnectionPoolChanged += onConnectionPoolChanged;
        var args = new ConnectionPoolStats.ConnectionPoolChangedEventArgs(0, 0, maxConnections);
        onConnectionPoolChanged(connectionPool, args);
        return connectionPool;
    }

    private static async Task KeepAliveAsync(INntpClient connection, CancellationToken cancellationToken)
    {
        var response = await connection.DateAsync(cancellationToken).ConfigureAwait(false);
        if (response.ResponseType != UsenetResponseType.DateAndTime)
        {
            throw new RetryableDownloadException(
                $"Unexpected NNTP response to idle DATE keepalive: {response.ResponseMessage}");
        }
    }

    // Hard ceiling for TCP/TLS connect + AUTHINFO. Long enough for slow providers,
    // short enough that three stuck handshakes cannot pin the pool forever.
    // Settable for tests so timeout coverage does not wait a full 15s.
    internal static TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(15);

    internal static bool ShouldWarnCleartextCredentials(bool useSsl, string? user) =>
        !useSsl && !string.IsNullOrEmpty(user);

    public static ValueTask<INntpClient> CreateNewConnection
    (
        UsenetProviderConfig.ConnectionDetails connectionDetails,
        CancellationToken ct
    ) => CreateNewConnection(
        connectionDetails,
        () => new BaseNntpClient(connectionDetails.UseSsl && connectionDetails.SkipTlsVerification),
        ct);

    internal static async ValueTask<INntpClient> CreateNewConnection
    (
        UsenetProviderConfig.ConnectionDetails connectionDetails,
        Func<INntpClient> connectionFactory,
        CancellationToken ct
    )
    {
        if (ContainsControlCharsOrSpace(connectionDetails.Host) ||
            ContainsControlChars(connectionDetails.User) ||
            ContainsControlChars(connectionDetails.Pass))
        {
            throw new ArgumentException(
                "Provider host must not contain whitespace or control characters; " +
                "username/password must not contain control characters.");
        }

        var connection = connectionFactory();
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(ConnectTimeout);
            try
            {
                await connection.ConnectAsync(
                    connectionDetails.Host, connectionDetails.Port, connectionDetails.UseSsl,
                    timeoutCts.Token).ConfigureAwait(false);
            }
#pragma warning disable CA2016 // CA2016: classify cancellation regardless of the ambient token -- forwarding it would misclassify cancellations from internal timeout/child tokens
            catch (Exception e) when (e.IsCancellationException() &&
#pragma warning restore CA2016
                                      timeoutCts.IsCancellationRequested &&
                                      !ct.IsCancellationRequested && e is not OutOfMemoryException)
            {
                // Only the CancelAfter deadline — not an unrelated internal cancel, and
                // not caller abort. Typed so Test Connection / middleware / breaker paths
                // see a connect failure rather than bare OCE.
                throw new CouldNotConnectToUsenetException(
                    $"Connection to {connectionDetails.Host}:{connectionDetails.Port} " +
                    $"timed out after {ConnectTimeout.TotalSeconds:F0}s.",
                    e);
            }

            if (!string.IsNullOrEmpty(connectionDetails.User) ||
                !string.IsNullOrEmpty(connectionDetails.Pass))
            {
                try
                {
                    await connection.AuthenticateAsync(
                        connectionDetails.User, connectionDetails.Pass,
                        timeoutCts.Token).ConfigureAwait(false);
                }
#pragma warning disable CA2016 // CA2016: classify cancellation regardless of the ambient token -- forwarding it would misclassify cancellations from internal timeout/child tokens
                catch (Exception e) when (e.IsCancellationException() &&
#pragma warning restore CA2016
                                          timeoutCts.IsCancellationRequested &&
                                          !ct.IsCancellationRequested && e is not OutOfMemoryException)
                {
                    throw new CouldNotLoginToUsenetException(
                        $"Authentication to {connectionDetails.Host}:{connectionDetails.Port} " +
                        $"timed out after {ConnectTimeout.TotalSeconds:F0}s.",
                        e);
                }
            }

            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }

        static bool ContainsControlChars(string? s) =>
            !string.IsNullOrEmpty(s) && s.Any(c => c < 0x20 || c == 0x7F);

        static bool ContainsControlCharsOrSpace(string? s) =>
            !string.IsNullOrEmpty(s) && s.Any(c => c <= 0x20 || c == 0x7F);
    }
}
