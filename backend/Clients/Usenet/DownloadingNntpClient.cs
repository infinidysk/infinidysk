using NzbWebDAV.Clients.Usenet.Concurrency;
using NzbWebDAV.Clients.Usenet.Contexts;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Clients.Usenet.Throttling;
using NzbWebDAV.Config;
using NzbWebDAV.Extensions;
using NzbWebDAV.Websocket;
using UsenetSharp.Models;

namespace NzbWebDAV.Clients.Usenet;

/// <summary>
/// This client is responsible for limiting download operations (BODY/ARTICLE)
/// to the configured number of maximum download connections, and optionally
/// throttling their combined throughput to a configured bandwidth limit.
/// </summary>
/// <param name="usenetClient"></param>
public class DownloadingNntpClient : WrappingNntpClient
{
    private static readonly TimeSpan BandwidthReportInterval = TimeSpan.FromSeconds(1);

    private readonly ConfigManager _configManager;
    private readonly WebsocketManager _websocketManager;
    private readonly PrioritizedSemaphore _semaphore;
    private TokenBucket? _bandwidthLimiter;
    private Timer? _bandwidthReportTimer;
    private long _lastReportedBytes;

    public DownloadingNntpClient(INntpClient usenetClient, ConfigManager configManager,
        WebsocketManager websocketManager) : base(usenetClient)
    {
        var maxDownloadConnections = configManager.GetMaxDownloadConnections();
        var streamingPriority = configManager.GetStreamingPriority();
        _configManager = configManager;
        _websocketManager = websocketManager;
        _semaphore = new PrioritizedSemaphore(maxDownloadConnections, maxDownloadConnections, streamingPriority);
        _bandwidthLimiter = CreateBandwidthLimiter(
            configManager.GetBandwidthLimitMbps(), configManager.GetBandwidthStreamingReserve());
        UpdateBandwidthReporting();
        configManager.OnConfigChanged += OnConfigChanged;
    }

    private void OnConfigChanged(object? sender, ConfigManager.ConfigEventArgs e)
    {
        if (e.ChangedConfig.ContainsKey("usenet.max-download-connections"))
        {
            var maxDownloadConnections = _configManager.GetMaxDownloadConnections();
            _semaphore.UpdateMaxAllowed(maxDownloadConnections);
        }

        if (e.ChangedConfig.ContainsKey("usenet.streaming-priority"))
        {
            var streamingPriority = _configManager.GetStreamingPriority();
            _semaphore.UpdatePriorityOdds(streamingPriority);
        }

        if (e.ChangedConfig.ContainsKey("usenet.bandwidth-limit-mbps"))
        {
            var mbps = _configManager.GetBandwidthLimitMbps();
            if (mbps > 0 && _bandwidthLimiter is not null)
                _bandwidthLimiter.UpdateRate(MbpsToBytesPerSecond(mbps));
            else
                _bandwidthLimiter = CreateBandwidthLimiter(mbps, _configManager.GetBandwidthStreamingReserve());
            UpdateBandwidthReporting();
        }

        if (e.ChangedConfig.ContainsKey("usenet.bandwidth-streaming-reserve"))
        {
            var reserveOdds = _configManager.GetBandwidthStreamingReserve();
            _bandwidthLimiter?.UpdatePriorityOdds(reserveOdds);
        }
    }

    private static TokenBucket? CreateBandwidthLimiter(double mbps, SemaphorePriorityOdds reserveOdds)
    {
        return mbps > 0 ? new TokenBucket(MbpsToBytesPerSecond(mbps), reserveOdds) : null;
    }

    // decimal Mbit/s -> bytes/s, matching how ISPs advertise line speed
    private static double MbpsToBytesPerSecond(double mbps) => mbps * 1_000_000 / 8;

    private static SemaphorePriority ResolvePriority(CancellationToken cancellationToken)
    {
        var downloadPriorityContext = cancellationToken.GetContext<DownloadPriorityContext>();
        return downloadPriorityContext?.Priority ?? SemaphorePriority.Low;
    }

    /// <summary>
    /// Starts/stops the periodic websocket report of the live download rate, so the
    /// settings UI can show actual usage while the user tunes the bandwidth limit.
    /// Only runs while a limit is configured - there's nothing for the user to tune
    /// against otherwise.
    /// </summary>
    private void UpdateBandwidthReporting()
    {
        if (_bandwidthLimiter is null)
        {
            _bandwidthReportTimer?.Dispose();
            _bandwidthReportTimer = null;
            _websocketManager.SendMessage(WebsocketTopic.BandwidthUsage, "off");
            return;
        }

        if (_bandwidthReportTimer is not null) return;
        _lastReportedBytes = _bandwidthLimiter.TotalBytesConsumed;
        _bandwidthReportTimer = new Timer(ReportBandwidthUsage, null, BandwidthReportInterval, BandwidthReportInterval);
    }

    private void ReportBandwidthUsage(object? state)
    {
        var limiter = _bandwidthLimiter;
        if (limiter is null) return;
        var totalBytes = limiter.TotalBytesConsumed;
        var currentBytesPerSecond = (totalBytes - _lastReportedBytes) / BandwidthReportInterval.TotalSeconds;
        _lastReportedBytes = totalBytes;
        var limitMbps = _configManager.GetBandwidthLimitMbps();
        var currentMbps = currentBytesPerSecond * 8 / 1_000_000;
        _websocketManager.SendMessage(WebsocketTopic.BandwidthUsage, $"{currentMbps:F2}|{limitMbps:F2}");
    }

    private UsenetDecodedBodyResponse ApplyThrottle(UsenetDecodedBodyResponse response, CancellationToken cancellationToken)
    {
        return _bandwidthLimiter is null
            ? response
            : response with
            {
                Stream = new ThrottledYencStream(response.Stream, _bandwidthLimiter, ResolvePriority(cancellationToken))
            };
    }

    private UsenetDecodedArticleResponse ApplyThrottle(UsenetDecodedArticleResponse response, CancellationToken cancellationToken)
    {
        return _bandwidthLimiter is null
            ? response
            : response with
            {
                Stream = new ThrottledYencStream(response.Stream, _bandwidthLimiter, ResolvePriority(cancellationToken))
            };
    }

    public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(SegmentId segmentId,
        CancellationToken cancellationToken)
    {
        return DecodedBodyAsync(segmentId, onConnectionReadyAgain: null, cancellationToken);
    }

    public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(SegmentId segmentId,
        CancellationToken cancellationToken)
    {
        return DecodedArticleAsync(segmentId, onConnectionReadyAgain: null, cancellationToken);
    }

    public override async Task<UsenetDecodedBodyResponse> DecodedBodyAsync(SegmentId segmentId,
        Action<ArticleBodyResult>? onConnectionReadyAgain, CancellationToken cancellationToken)
    {
        await AcquireExclusiveConnectionAsync(onConnectionReadyAgain, cancellationToken).ConfigureAwait(false);
        var response = await base.DecodedBodyAsync(segmentId, OnConnectionReadyAgain, cancellationToken)
            .ConfigureAwait(false);
        return ApplyThrottle(response, cancellationToken);

        void OnConnectionReadyAgain(ArticleBodyResult articleBodyResult)
        {
            _semaphore.Release();
            onConnectionReadyAgain?.Invoke(articleBodyResult);
        }
    }

    public override async Task<UsenetDecodedArticleResponse> DecodedArticleAsync(SegmentId segmentId,
        Action<ArticleBodyResult>? onConnectionReadyAgain, CancellationToken cancellationToken)
    {
        await AcquireExclusiveConnectionAsync(onConnectionReadyAgain, cancellationToken).ConfigureAwait(false);
        var response = await base.DecodedArticleAsync(segmentId, OnConnectionReadyAgain, cancellationToken)
            .ConfigureAwait(false);
        return ApplyThrottle(response, cancellationToken);

        void OnConnectionReadyAgain(ArticleBodyResult articleBodyResult)
        {
            _semaphore.Release();
            onConnectionReadyAgain?.Invoke(articleBodyResult);
        }
    }

    private async Task AcquireExclusiveConnectionAsync(Action<ArticleBodyResult>? onConnectionReadyAgain,
        CancellationToken cancellationToken)
    {
        try
        {
            await AcquireExclusiveConnectionAsync(cancellationToken);
        }
        catch
        {
            onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved);
            throw;
        }
    }

    private Task AcquireExclusiveConnectionAsync(CancellationToken cancellationToken)
    {
        return _semaphore.WaitAsync(ResolvePriority(cancellationToken), cancellationToken);
    }

    public override async Task<UsenetExclusiveConnection> AcquireExclusiveConnectionAsync
    (
        string segmentId,
        CancellationToken cancellationToken
    )
    {
        await AcquireExclusiveConnectionAsync(cancellationToken).ConfigureAwait(false);
        return new UsenetExclusiveConnection(_ => _semaphore.Release());
    }

    public override async Task<UsenetDecodedBodyResponse> DecodedBodyAsync(SegmentId segmentId,
        UsenetExclusiveConnection exclusiveConnection, CancellationToken cancellationToken)
    {
        var onConnectionReadyAgain = exclusiveConnection.OnConnectionReadyAgain;
        var response = await base.DecodedBodyAsync(segmentId, onConnectionReadyAgain, cancellationToken)
            .ConfigureAwait(false);
        return ApplyThrottle(response, cancellationToken);
    }

    public override async Task<UsenetDecodedArticleResponse> DecodedArticleAsync(SegmentId segmentId,
        UsenetExclusiveConnection exclusiveConnection, CancellationToken cancellationToken)
    {
        var onConnectionReadyAgain = exclusiveConnection.OnConnectionReadyAgain;
        var response = await base.DecodedArticleAsync(segmentId, onConnectionReadyAgain, cancellationToken)
            .ConfigureAwait(false);
        return ApplyThrottle(response, cancellationToken);
    }

    public override void Dispose()
    {
        _configManager.OnConfigChanged -= OnConfigChanged;
        _bandwidthReportTimer?.Dispose();
        base.Dispose();
        GC.SuppressFinalize(this);
    }
}