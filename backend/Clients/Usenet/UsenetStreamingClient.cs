using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models.Metrics;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Extensions;
using NzbWebDAV.Services;
using NzbWebDAV.Services.Metrics;
using NzbWebDAV.Services.StreamTrace;
using NzbWebDAV.Websocket;
using Serilog;

namespace NzbWebDAV.Clients.Usenet;

public class UsenetStreamingClient : WrappingNntpClient
{
    public UsenetStreamingClient(
        ConfigManager configManager,
        WebsocketManager websocketManager,
        ProviderUsageTracker usageTracker,
        MetricsWriter metricsWriter,
        ProviderBytesTracker bytesTracker,
        StreamTraceBuffer streamTrace,
        ActiveReadRegistry activeReadRegistry,
        ArticleMissNegativeCache? articleMissCache = null)
        : base(CreateDownloadingNntpClient(
            configManager, websocketManager, usageTracker, metricsWriter, bytesTracker,
            streamTrace, activeReadRegistry, articleMissCache))
    {
        // when config changes, create a new MultiProviderClient to use instead.
        configManager.OnConfigChanged += (_, configEventArgs) =>
        {
            // if unrelated config changed, do nothing
            if (!configEventArgs.ChangedConfig.ContainsKey(ConfigKeys.UsenetProviders)) return;

            try
            {
                // update the connection-pool according to the new config
                var newUsenetClient = CreateDownloadingNntpClient(
                    configManager, websocketManager, usageTracker, metricsWriter, bytesTracker,
                    streamTrace, activeReadRegistry, articleMissCache);
                ReplaceUnderlyingClient(newUsenetClient);
            }
            catch (Exception e)
            {
                // Keep the previous (working) client and let remaining OnConfigChanged
                // subscribers run — a throw from a multicast handler aborts the rest.
                Log.Error(e, "Failed to rebuild usenet client after provider config change; keeping previous client");
            }
        };
    }

    private static INntpClient CreateDownloadingNntpClient
    (
        ConfigManager configManager,
        WebsocketManager websocketManager,
        ProviderUsageTracker usageTracker,
        MetricsWriter metricsWriter,
        ProviderBytesTracker bytesTracker,
        StreamTraceBuffer streamTrace,
        ActiveReadRegistry activeReadRegistry,
        ArticleMissNegativeCache? articleMissCache
    )
    {
        var multiProviderClient = CreateMultiProviderClient(
            configManager, websocketManager, usageTracker, metricsWriter, bytesTracker,
            streamTrace, activeReadRegistry, articleMissCache);
        var downloadingClient = new DownloadingNntpClient(multiProviderClient, configManager);
        INntpClient inner = downloadingClient;
        if (configManager.IsSegmentCacheEnabled())
        {
            try
            {
                inner = new SegmentCacheNntpClient(
                    downloadingClient,
                    configManager.GetSegmentCachePath(),
                    configManager.GetSegmentCacheMaxBytes(),
                    usageTracker,
                    metricsWriter
                );
            }
            catch (Exception e)
            {
                Log.Warning(e, "Segment cache disabled: failed to initialise at {Path}.",
                    configManager.GetSegmentCachePath());
            }
        }

        // Always wrap with header caching so seek probes reuse immutable yEnc headers
        // even when the optional on-disk segment body cache is disabled.
        return new HeaderCachingNntpClient(inner);
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

    private static MultiProviderNntpClient CreateMultiProviderClient
    (
        ConfigManager configManager,
        WebsocketManager websocketManager,
        ProviderUsageTracker usageTracker,
        MetricsWriter metricsWriter,
        ProviderBytesTracker bytesTracker,
        StreamTraceBuffer streamTrace,
        ActiveReadRegistry activeReadRegistry,
        ArticleMissNegativeCache? articleMissCache
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
        var providerClients = providerConfig.Providers
            .Select((provider, index) => CreateProviderClient(
                provider,
                connectionPoolStats.GetOnConnectionPoolChanged(index),
                idleTimeoutSeconds,
                metricsWriter
            ))
            .ToList();
        return new MultiProviderNntpClient(
            providerClients, usageTracker, metricsWriter, bytesTracker,
            cascadeEnabled: configManager.IsCascadeEnabled,
            retryPrimaryOnMiss: configManager.IsCascadeRetryPrimaryOnMiss,
            streamTrace: streamTrace,
            activeReadRegistry: activeReadRegistry,
            articleMissCache: articleMissCache);
    }

    private static MultiConnectionNntpClient CreateProviderClient
    (
        UsenetProviderConfig.ConnectionDetails connectionDetails,
        EventHandler<ConnectionPoolStats.ConnectionPoolChangedEventArgs> onConnectionPoolChanged,
        int idleTimeoutSeconds,
        MetricsWriter metricsWriter
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

        var connectionPool = CreateNewConnectionPool(
            maxConnections: maxConnections,
            connectionFactory: ct => CreateNewConnection(connectionDetails, ct),
            onConnectionPoolChanged,
            idleTimeoutSeconds
        );
        // Ensure a metrics key even if startup backfill was skipped somehow.
        if (connectionDetails.ProviderId == Guid.Empty)
            connectionDetails.ProviderId = Guid.NewGuid();
        var metricsKey = UsenetProviderIdentity.MetricsKey(connectionDetails);
        var circuitBreaker = new ProviderCircuitBreaker(
            connectionDetails.Host,
            transition => metricsWriter.RecordEvent(new MetricEvent
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
            }));
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
            metricsKey
        );
    }

    private static ConnectionPool<INntpClient> CreateNewConnectionPool
    (
        int maxConnections,
        Func<CancellationToken, ValueTask<INntpClient>> connectionFactory,
        EventHandler<ConnectionPoolStats.ConnectionPoolChangedEventArgs> onConnectionPoolChanged,
        int idleTimeoutSeconds
    )
    {
        var idleTimeout = TimeSpan.FromSeconds(idleTimeoutSeconds);
        Log.Information(
            "Creating NNTP connection pool max={Max} idleTimeout={IdleTimeoutSeconds}s",
            maxConnections, idleTimeoutSeconds);
        var connectionPool = new ConnectionPool<INntpClient>(
            maxConnections, connectionFactory, idleTimeout);
        connectionPool.OnConnectionPoolChanged += onConnectionPoolChanged;
        var args = new ConnectionPoolStats.ConnectionPoolChangedEventArgs(0, 0, maxConnections);
        onConnectionPoolChanged(connectionPool, args);
        return connectionPool;
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
        if (ContainsControlChars(connectionDetails.Host) ||
            ContainsControlChars(connectionDetails.User) ||
            ContainsControlChars(connectionDetails.Pass) ||
            connectionDetails.Host.Contains(' '))
        {
            throw new ArgumentException(
                "Provider host/username/password must not contain whitespace or control characters.");
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
            catch (Exception e) when (e.IsCancellationException() &&
                                      timeoutCts.IsCancellationRequested &&
                                      !ct.IsCancellationRequested)
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
                catch (Exception e) when (e.IsCancellationException() &&
                                          timeoutCts.IsCancellationRequested &&
                                          !ct.IsCancellationRequested)
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
    }
}
