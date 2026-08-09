using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Database.Models.Metrics;
using NzbWebDAV.Services;
using NzbWebDAV.Services.Metrics;
using NzbWebDAV.Streams;

namespace NzbWebDAV.Api.Controllers.GetOverviewStats;

[ApiController]
[Route("api/get-overview-stats")]
public class GetOverviewStatsController(
    DavDatabaseClient davDb,
    ActiveReadRegistry registry,
    LiveStatsBroadcaster liveStats,
    MetricsWriter metricsWriter,
    ConfigManager configManager,
    IndexerHitTracker hitTracker,
    UsenetStreamingClient usenetStreamingClient,
    InFlightArticleBudget inFlightArticleBudget,
    ProviderBytesTracker providerBytesTracker
) : BaseApiController
{
    private const long OneMinute = 60_000;
    private const long OneHour = 60 * OneMinute;
    private const long OneDay = 24 * OneHour;
    private static readonly TimeSpan LiveSpeedMaxAge = TimeSpan.FromMinutes(1);

    // Log-scale latency buckets in milliseconds. Last bucket is a catch-all up to int.MaxValue.
    private static readonly int[] LatencyBucketEdges =
    {
        0, 10, 25, 50, 100, 200, 400, 800, 1500, 3000, 6000, 12000, 30000, int.MaxValue
    };

    protected override async Task<IActionResult> HandleRequest()
    {
        var request = new GetOverviewStatsRequest(HttpContext);
        var response = await BuildAsync(request).ConfigureAwait(false);
        return Ok(response);
    }

    private async Task<GetOverviewStatsResponse> BuildAsync(GetOverviewStatsRequest request)
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var window = request.Window;
        var sections = request.Sections;
        var (windowMs, bucketSize, label) = ResolveWindow(window, nowMs);
        var windowStart = window == GetOverviewStatsRequest.OverviewWindow.AllTime
            ? 0
            : nowMs - windowMs;
        var wantWindow = sections.HasFlag(GetOverviewStatsRequest.OverviewSections.Window);
        var wantDetail = sections.HasFlag(GetOverviewStatsRequest.OverviewSections.Detail);
        var wantStatic = sections.HasFlag(GetOverviewStatsRequest.OverviewSections.Static);
        var useRollups =
            window == GetOverviewStatsRequest.OverviewWindow.Last7Days ||
            window == GetOverviewStatsRequest.OverviewWindow.Last30Days ||
            window == GetOverviewStatsRequest.OverviewWindow.AllTime;

        var labelsByMetricsKey = ProviderUsageHelper
            .BuildLabelsByMetricsKey(configManager.GetUsenetProviderConfig().Providers);

        var included = new List<string>();
        var tiles = new GetOverviewStatsResponse.LiveTiles();
        var throughput = new List<GetOverviewStatsResponse.ThroughputPoint>();
        long throughputBucketSizeMs = 0;
        var providers = new List<GetOverviewStatsResponse.ProviderRow>();
        var sessionsBlock = new GetOverviewStatsResponse.SessionsBlock();
        var heatmap = new GetOverviewStatsResponse.HeatmapBlock();
        var failover = new GetOverviewStatsResponse.FailoverBlock();
        var latency = new GetOverviewStatsResponse.LatencyBlock();
        var errors = new List<GetOverviewStatsResponse.ErrorSlice>();
        var catalogue = new GetOverviewStatsResponse.CatalogueBlock();
        var indexers = new List<GetOverviewStatsResponse.IndexerRow>();
        var indexerApiUsage = new List<GetOverviewStatsResponse.IndexerApiUsageRow>();
        var lifetime = new GetOverviewStatsResponse.LifetimeBlock();
        var records = new GetOverviewStatsResponse.RecordsBlock();
        long totalArticles = 0, totalMisses = 0, totalErrors = 0, totalBytesFetched = 0;

        Task<WindowSectionResult>? windowTask = null;
        Task<DetailSectionResult>? detailTask = null;
        Task<StaticSectionResult>? staticTask = null;

        if (wantWindow)
            windowTask = BuildWindowSectionAsync(window, windowStart, windowMs, bucketSize, nowMs, useRollups, labelsByMetricsKey);
        if (wantDetail && !useRollups)
            detailTask = BuildDetailSectionAsync(windowStart);
        if (wantStatic)
            staticTask = BuildStaticSectionAsync();

        var tasks = new List<Task>();
        if (windowTask is not null) tasks.Add(windowTask);
        if (detailTask is not null) tasks.Add(detailTask);
        if (staticTask is not null) tasks.Add(staticTask);
        if (tasks.Count > 0)
            await Task.WhenAll(tasks).ConfigureAwait(false);

        if (windowTask is not null)
        {
            var w = await windowTask.ConfigureAwait(false);
            included.Add("window");
            tiles = w.Tiles;
            throughput = w.Throughput;
            throughputBucketSizeMs = w.ThroughputBucketSizeMs;
            providers = w.Providers;
            sessionsBlock = w.Sessions;
            heatmap = w.Heatmap;
            failover = w.Failover;
            totalArticles = w.TotalArticles;
            totalMisses = w.TotalMisses;
            totalErrors = w.TotalErrors;
            totalBytesFetched = w.TotalBytesFetched;
        }

        if (detailTask is not null)
        {
            var d = await detailTask.ConfigureAwait(false);
            included.Add("detail");
            latency = d.Latency;
            errors = d.Errors;
        }
        else if (wantDetail && useRollups)
        {
            // Long windows hide latency/errors in the UI; acknowledge the section as empty.
            included.Add("detail");
        }

        if (staticTask is not null)
        {
            var s = await staticTask.ConfigureAwait(false);
            included.Add("static");
            catalogue = s.Catalogue;
            indexers = s.Indexers;
            indexerApiUsage = s.IndexerApiUsage;
            lifetime = s.Lifetime;
            records = s.Records;
        }

        return new GetOverviewStatsResponse
        {
            Window = label,
            IncludedSections = included,
            Tiles = tiles,
            Throughput = throughput,
            ThroughputBucketSizeMs = throughputBucketSizeMs,
            TotalArticles = totalArticles,
            TotalMisses = totalMisses,
            TotalErrors = totalErrors,
            TotalBytesFetched = totalBytesFetched,
            Providers = providers,
            Catalogue = catalogue,
            Sessions = sessionsBlock,
            Heatmap = heatmap,
            Latency = latency,
            Errors = errors,
            Indexers = indexers,
            IndexerApiUsage = indexerApiUsage,
            Lifetime = lifetime,
            Records = records,
            Failover = failover,
            MetricsHealth = wantWindow || wantStatic ? BuildMetricsHealth() : new GetOverviewStatsResponse.MetricsHealthBlock(),
        };
    }

    private sealed record WindowSectionResult(
        GetOverviewStatsResponse.LiveTiles Tiles,
        List<GetOverviewStatsResponse.ThroughputPoint> Throughput,
        long ThroughputBucketSizeMs,
        List<GetOverviewStatsResponse.ProviderRow> Providers,
        GetOverviewStatsResponse.SessionsBlock Sessions,
        GetOverviewStatsResponse.HeatmapBlock Heatmap,
        GetOverviewStatsResponse.FailoverBlock Failover,
        long TotalArticles,
        long TotalMisses,
        long TotalErrors,
        long TotalBytesFetched);

    private sealed record DetailSectionResult(
        GetOverviewStatsResponse.LatencyBlock Latency,
        List<GetOverviewStatsResponse.ErrorSlice> Errors);

    private sealed record StaticSectionResult(
        GetOverviewStatsResponse.CatalogueBlock Catalogue,
        List<GetOverviewStatsResponse.IndexerRow> Indexers,
        List<GetOverviewStatsResponse.IndexerApiUsageRow> IndexerApiUsage,
        GetOverviewStatsResponse.LifetimeBlock Lifetime,
        GetOverviewStatsResponse.RecordsBlock Records);

    private async Task<WindowSectionResult> BuildWindowSectionAsync(
        GetOverviewStatsRequest.OverviewWindow window,
        long windowStart,
        long windowMs,
        long bucketSize,
        long nowMs,
        bool useRollups,
        IReadOnlyDictionary<string, string?> labelsByMetricsKey)
    {
        await using var metricsSessions = new MetricsDbContext();
        await using var metricsHeatmap = new MetricsDbContext();
        await using var metricsPrev = new MetricsDbContext();
        await using var metricsA = new MetricsDbContext();
        await using var metricsB = new MetricsDbContext();
        await using var metricsLive = new MetricsDbContext();
        await using var metricsEvents = new MetricsDbContext();

        var sessionsTask = metricsSessions.ReadSessions
            .Where(x => x.EndedAt >= windowStart)
            .Select(x => new { x.StartedAt, x.EndedAt, x.DurationMs, x.BytesServed, x.FailoverSaves })
            .ToListAsync();
        var heatmapTask = BuildHeatmapAsync(metricsHeatmap, window, nowMs);
        var previousSavesTask = LoadPreviousFailoverSavesAsync(metricsPrev, window, windowStart, windowMs);
        var sinceMinute = nowMs - OneMinute;
        var liveCountsTask = metricsLive.SegmentFetches
            .Where(x => x.At >= sinceMinute)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Articles = g.Count(),
                // Hard failures only — Missing (expected provider misses) is excluded.
                Errors = g.Count(x =>
                    x.Status != SegmentFetch.FetchStatus.Ok
                    && x.Status != SegmentFetch.FetchStatus.Missing),
            })
            .FirstOrDefaultAsync();
        var circuitEventsTask = metricsEvents.MetricEvents
            .Where(item =>
                item.Kind == "circuit" &&
                item.Tag1 != null &&
                item.Tag2 != null &&
                item.At >= Math.Max(0, windowStart - OneDay))
            .Select(item => new
            {
                item.At,
                Provider = item.Tag1!,
                State = item.Tag2!,
                CooldownMs = item.Num,
            })
            .ToListAsync();

        List<(long At, string Provider, long Saves)> rescues;
        List<(string From, SegmentFetch.FetchStatus Reason, long Count)> misses;
        List<GetOverviewStatsResponse.ThroughputPoint> throughput;
        List<GetOverviewStatsResponse.ProviderRow> providers;
        long totalArticles, totalMisses, totalErrors, totalBytesFetched;

        if (useRollups)
        {
            var hoursTask = metricsA.ProviderHourly
                .Where(h => h.Hour >= windowStart)
                .Select(h => new { h.Hour, h.Provider, h.Articles, h.BytesFetched, h.Misses, h.Errors, h.Retries, h.FailoverSaves, h.SumDurationMs })
                .ToListAsync();
            var failoverEdgesTask = metricsB.FailoverHourly
                .Where(f => f.Hour >= windowStart)
                .Select(f => new { f.FromProvider, f.Reason, f.Count })
                .ToListAsync();

            await Task.WhenAll(sessionsTask, heatmapTask, previousSavesTask, liveCountsTask, hoursTask, failoverEdgesTask)
                .ConfigureAwait(false);

            var hours = await hoursTask.ConfigureAwait(false);
            var sessions = await sessionsTask.ConfigureAwait(false);
            var failoverEdges = await failoverEdgesTask.ConfigureAwait(false);

            throughput = BuildThroughputFromHourly(
                hours.Select(h => (h.Hour, h.Articles, h.Misses, h.Errors, h.BytesFetched)),
                sessions.Select(s => (s.EndedAt, s.BytesServed)),
                bucketSize);
            providers = BuildProvidersFromHourly(
                hours.Select(h => (h.Hour, h.Provider, h.Articles, h.BytesFetched, h.Misses, h.Errors, h.Retries, h.SumDurationMs)),
                windowStart,
                bucketSize,
                nowMs,
                labelsByMetricsKey);
            totalArticles = hours.Sum(h => h.Articles);
            totalMisses = hours.Sum(h => h.Misses);
            totalErrors = hours.Sum(h => h.Errors);
            totalBytesFetched = hours.Sum(h => h.BytesFetched);
            rescues = hours.Where(h => h.FailoverSaves > 0)
                .Select(h => (h.Hour, h.Provider, h.FailoverSaves))
                .ToList();
            misses = failoverEdges.Select(e => (e.FromProvider, e.Reason, e.Count)).ToList();
        }
        else
        {
            // 24h: use minute rollups — do not materialize every SegmentFetch row.
            await using var metricsC = new MetricsDbContext();
            var minutesTask = metricsA.ProviderMinutes
                .Where(p => p.Minute >= windowStart)
                .Select(p => new
                {
                    p.Minute,
                    p.Provider,
                    p.Articles,
                    p.BytesFetched,
                    p.Misses,
                    p.Errors,
                    p.Retries,
                    p.FailoverSaves,
                    p.SumDurationMs
                })
                .ToListAsync();
            var throughputMinutesTask = metricsB.ThroughputMinutes
                .Where(t => t.Minute >= windowStart)
                .Select(t => new { t.Minute, t.Articles, t.Misses, t.Errors, t.BytesServed, t.BytesFetched })
                .ToListAsync();
            var failoverMissesTask = metricsC.FailoverMisses
                .Where(f => f.At >= windowStart)
                .Select(f => new { f.FromProvider, f.Reason })
                .ToListAsync();

            await Task.WhenAll(
                    sessionsTask, heatmapTask, previousSavesTask, liveCountsTask,
                    minutesTask, throughputMinutesTask, failoverMissesTask)
                .ConfigureAwait(false);

            var minutes = await minutesTask.ConfigureAwait(false);
            var throughputMinutes = await throughputMinutesTask.ConfigureAwait(false);
            var failoverMisses = await failoverMissesTask.ConfigureAwait(false);

            throughput = BuildThroughputFromMinutes(
                throughputMinutes.Select(t => (t.Minute, t.Articles, t.Misses, t.Errors, t.BytesServed, t.BytesFetched)),
                bucketSize);
            providers = BuildProvidersFromMinutes(
                minutes.Select(m => (m.Minute, m.Provider, m.Articles, m.BytesFetched, m.Misses, m.Errors, m.Retries, m.SumDurationMs)),
                windowStart, window, labelsByMetricsKey);
            totalArticles = minutes.Sum(m => m.Articles);
            totalMisses = minutes.Sum(m => m.Misses);
            totalErrors = minutes.Sum(m => m.Errors);
            totalBytesFetched = minutes.Sum(m => m.BytesFetched);
            rescues = minutes.Where(m => m.FailoverSaves > 0)
                .Select(m => (m.Minute, m.Provider, m.FailoverSaves))
                .ToList();
            misses = failoverMisses.Select(e => (e.FromProvider, e.Reason, 1L)).ToList();
        }

        var sessionsRows = await sessionsTask.ConfigureAwait(false);
        var heatmap = await heatmapTask.ConfigureAwait(false);
        var previousSaves = await previousSavesTask.ConfigureAwait(false);
        var liveCounts = await liveCountsTask.ConfigureAwait(false);

        var readsSaved = sessionsRows.LongCount(s => s.FailoverSaves > 0);
        var failover = BuildFailover(
            rescues,
            misses,
            totalArticles,
            sessionsRows.Count,
            readsSaved,
            previousSaves,
            ResolveFailoverBucket(window),
            labelsByMetricsKey);

        var tiles = BuildLiveTiles(liveCounts?.Articles ?? 0, liveCounts?.Errors ?? 0);
        var sessionsBlock = BuildSessionsBlock(sessionsRows.Select(s => (s.DurationMs, s.BytesServed)));
        providers = ProviderCircuitOverviewEnricher.EnrichProviders(
            providers,
            usenetStreamingClient.GetProviderCircuitSnapshots(),
            labelsByMetricsKey);
        ApplyLiveSpeedFallback(providers);
        var circuitEvents = await circuitEventsTask.ConfigureAwait(false);
        ApplyOutageSparks(
            providers,
            circuitEvents.Select(item => new CircuitOutageSparkBuilder.Event(
                item.At,
                item.Provider,
                item.State,
                item.CooldownMs)),
            windowStart,
            window,
            nowMs,
            useRollups);

        return new WindowSectionResult(
            tiles, throughput, bucketSize, providers, sessionsBlock, heatmap, failover,
            totalArticles, totalMisses, totalErrors, totalBytesFetched);
    }

    private static void ApplyOutageSparks(
        List<GetOverviewStatsResponse.ProviderRow> providers,
        IEnumerable<CircuitOutageSparkBuilder.Event> events,
        long windowStart,
        GetOverviewStatsRequest.OverviewWindow window,
        long nowMs,
        bool useRollups)
    {
        var (bucketCount, bucketSize, sparkStart) = ResolveProviderSparkGeometry(
            windowStart, window, nowMs, useRollups);
        var sparks = CircuitOutageSparkBuilder.Build(
            events,
            providers.Select(provider => provider.Provider),
            sparkStart,
            bucketSize,
            bucketCount,
            nowMs);
        foreach (var provider in providers)
            provider.OutageSpark = sparks.GetValueOrDefault(provider.Provider) ?? [];
    }

    private static (int BucketCount, long BucketSize, long SparkStart) ResolveProviderSparkGeometry(
        long windowStart,
        GetOverviewStatsRequest.OverviewWindow window,
        long nowMs,
        bool useRollups)
    {
        if (useRollups)
        {
            const long sparkSize = OneDay;
            var totalSpan = nowMs - windowStart;
            var sparkBuckets = Math.Max(1, (int)Math.Min(60, totalSpan / sparkSize + 1));
            return (sparkBuckets, sparkSize, windowStart - windowStart % sparkSize);
        }

        var (bucketCount, bucketSize) = window switch
        {
            GetOverviewStatsRequest.OverviewWindow.Last1Hour => (60, OneMinute),
            GetOverviewStatsRequest.OverviewWindow.Last7Days => (168, OneHour),
            _ => (24, OneHour),
        };
        return (bucketCount, bucketSize, windowStart - windowStart % bucketSize);
    }

    private static async Task<long?> LoadPreviousFailoverSavesAsync(
        MetricsDbContext metrics,
        GetOverviewStatsRequest.OverviewWindow window,
        long windowStart,
        long windowMs)
    {
        if (window == GetOverviewStatsRequest.OverviewWindow.AllTime)
            return null;
        return await metrics.ProviderHourly
            .Where(h => h.Hour >= windowStart - windowMs && h.Hour < windowStart)
            .SumAsync(h => (long?)h.FailoverSaves)
            .ConfigureAwait(false) ?? 0L;
    }

    private async Task<DetailSectionResult> BuildDetailSectionAsync(long windowStart)
    {
        await using var metricsLatency = new MetricsDbContext();
        await using var metricsErrors = new MetricsDbContext();

        var durationsTask = metricsLatency.SegmentFetches
            .Where(x => x.At >= windowStart && x.Status == SegmentFetch.FetchStatus.Ok)
            .Select(x => x.DurationMs)
            .ToListAsync();
        var errorGroupsTask = metricsErrors.SegmentFetches
            .Where(x => x.At >= windowStart && x.Status != SegmentFetch.FetchStatus.Ok)
            .GroupBy(x => x.Status)
            .Select(g => new { Status = g.Key, Count = (long)g.Count() })
            .ToListAsync();

        await Task.WhenAll(durationsTask, errorGroupsTask).ConfigureAwait(false);

        var durations = await durationsTask.ConfigureAwait(false);
        var errorGroups = await errorGroupsTask.ConfigureAwait(false);

        return new DetailSectionResult(
            BuildLatency(durations),
            errorGroups
                .Select(e => new GetOverviewStatsResponse.ErrorSlice
                {
                    Status = e.Status.ToString(),
                    Count = e.Count,
                })
                .OrderByDescending(s => s.Count)
                .ToList());
    }

    private async Task<StaticSectionResult> BuildStaticSectionAsync()
    {
        await using var metricsLifetime = new MetricsDbContext();
        await using var metricsRecords = new MetricsDbContext();
        await using var davIndexers = new DavDatabaseContext();

        var catalogueTask = BuildCatalogueAsync(davDb.Ctx);
        var indexersTask = BuildIndexersAsync(davIndexers);
        var apiUsageTask = BuildIndexerApiUsageAsync();
        var lifetimeTask = BuildLifetimeAsync(metricsLifetime);
        var recordsTask = BuildRecordsAsync(metricsRecords);

        await Task.WhenAll(catalogueTask, indexersTask, apiUsageTask, lifetimeTask, recordsTask)
            .ConfigureAwait(false);

        return new StaticSectionResult(
            await catalogueTask.ConfigureAwait(false),
            await indexersTask.ConfigureAwait(false),
            await apiUsageTask.ConfigureAwait(false),
            await lifetimeTask.ConfigureAwait(false),
            await recordsTask.ConfigureAwait(false));
    }

    private GetOverviewStatsResponse.MetricsHealthBlock BuildMetricsHealth()
    {
        var stats = metricsWriter.Stats;
        return new GetOverviewStatsResponse.MetricsHealthBlock
        {
            Queued = stats.QueuedFetches + stats.QueuedEvents +
                     stats.QueuedSessions + stats.QueuedFailoverMisses,
            Dropped = stats.DroppedFetches + stats.DroppedEvents +
                      stats.DroppedSessions + stats.DroppedFailoverMisses,
            LastSuccessfulFlushAtMs = stats.LastSuccessfulFlushAtMs,
            LastFlushError = stats.LastFlushError,
        };
    }

    private async Task<List<GetOverviewStatsResponse.IndexerApiUsageRow>> BuildIndexerApiUsageAsync()
    {
        var configured = configManager.GetIndexerConfig().Indexers
            .Where(x => x.Enabled)
            .Select(x => (x.Name, x.HitLimit, x.DownloadLimit, ResetHourUtc: x.HitLimitResetTime))
            .ToList();
        if (configured.Count == 0) return new List<GetOverviewStatsResponse.IndexerApiUsageRow>();

        var snapshots = await hitTracker.GetUsageAsync(configured, HttpContext.RequestAborted).ConfigureAwait(false);
        var resetHourByName = configured.ToDictionary(x => x.Name, x => x.ResetHourUtc);

        return snapshots
            .Select(s => new GetOverviewStatsResponse.IndexerApiUsageRow
            {
                Name = s.IndexerName,
                ApiHits = s.ApiHits,
                ApiHitLimit = s.ApiHitLimit,
                DownloadHits = s.DownloadHits,
                DownloadHitLimit = s.DownloadHitLimit,
                ResetAtMs = s.ResetAt.ToUnixTimeMilliseconds(),
                ResetHourUtc = resetHourByName.GetValueOrDefault(s.IndexerName),
            })
            .OrderByDescending(r => r.ApiHits + r.DownloadHits)
            .ThenBy(r => r.Name)
            .ToList();
    }

    private static (long WindowMs, long BucketSize, string Label) ResolveWindow(
        GetOverviewStatsRequest.OverviewWindow window, long nowMs) => window switch
        {
            GetOverviewStatsRequest.OverviewWindow.Last1Hour => (OneHour, OneMinute, "1h"),
            GetOverviewStatsRequest.OverviewWindow.Last24Hours => (OneDay, OneMinute, "24h"),
            GetOverviewStatsRequest.OverviewWindow.Last7Days => (7 * OneDay, OneHour, "7d"),
            GetOverviewStatsRequest.OverviewWindow.Last30Days => (30 * OneDay, OneHour, "30d"),
            GetOverviewStatsRequest.OverviewWindow.AllTime => (nowMs, OneDay, "all"),
            _ => (OneDay, OneMinute, "24h"),
        };

    private static long ResolveFailoverBucket(GetOverviewStatsRequest.OverviewWindow window) => window switch
    {
        GetOverviewStatsRequest.OverviewWindow.Last1Hour => OneMinute,
        GetOverviewStatsRequest.OverviewWindow.Last24Hours => OneHour,
        GetOverviewStatsRequest.OverviewWindow.Last7Days => OneDay,
        GetOverviewStatsRequest.OverviewWindow.Last30Days => OneDay,
        GetOverviewStatsRequest.OverviewWindow.AllTime => 7 * OneDay,
        _ => OneHour,
    };

    internal static GetOverviewStatsResponse.FailoverBlock BuildFailover(
        IEnumerable<(long At, string Provider, long Saves)> rescues,
        IEnumerable<(string From, SegmentFetch.FetchStatus Reason, long Count)> misses,
        long totalArticles,
        long readSessions,
        long readsSaved,
        long? previousSaves,
        long chartBucketSize,
        IReadOnlyDictionary<string, string?> labelsByMetricsKey)
    {
        var totalsByProvider = new Dictionary<string, long>();
        var byBucket = new SortedDictionary<long, Dictionary<string, long>>();
        foreach (var (at, provider, saves) in rescues)
        {
            if (saves <= 0) continue;
            totalsByProvider.TryGetValue(provider, out var t);
            totalsByProvider[provider] = t + saves;

            var bucket = at - (at % chartBucketSize);
            if (!byBucket.TryGetValue(bucket, out var perProvider))
                byBucket[bucket] = perProvider = new Dictionary<string, long>();
            perProvider.TryGetValue(provider, out var c);
            perProvider[provider] = c + saves;
        }

        // Chart/list series only include currently configured providers; aggregate
        // totals below still count rescues from deleted providers.
        var orderedProviders = totalsByProvider
            .Where(kv => IsConfiguredMetricsKey(kv.Key, labelsByMetricsKey))
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => kv.Key)
            .ToList();
        var indexOf = orderedProviders
            .Select((p, i) => (p, i))
            .ToDictionary(x => x.p, x => x.i);

        var missesByProvider = new Dictionary<string, long>();
        var missesByReason = new Dictionary<SegmentFetch.FetchStatus, long>();
        long segmentsCovered = 0;
        foreach (var (from, reason, count) in misses)
        {
            if (count <= 0) continue;
            segmentsCovered += count;
            missesByProvider.TryGetValue(from, out var m);
            missesByProvider[from] = m + count;
            missesByReason.TryGetValue(reason, out var r);
            missesByReason[reason] = r + count;
        }

        return new GetOverviewStatsResponse.FailoverBlock
        {
            ArticlesRecovered = totalsByProvider.Values.Sum(),
            PreviousArticlesRecovered = previousSaves,
            SegmentsCovered = segmentsCovered,
            ReadsSaved = readsSaved,
            ReadSessions = readSessions,
            TotalArticles = totalArticles,
            BucketSizeMs = chartBucketSize,
            RescuedBy = orderedProviders
                .Select(p => new GetOverviewStatsResponse.FailoverProvider
                {
                    Provider = p,
                    Nickname = labelsByMetricsKey.GetValueOrDefault(p),
                    Saves = totalsByProvider[p],
                })
                .ToList(),
            RescuedFrom = missesByProvider
                .Where(kv => IsConfiguredMetricsKey(kv.Key, labelsByMetricsKey))
                .OrderByDescending(kv => kv.Value)
                .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .Select(kv => new GetOverviewStatsResponse.FailoverFrom
                {
                    Provider = kv.Key,
                    Nickname = labelsByMetricsKey.GetValueOrDefault(kv.Key),
                    Misses = kv.Value,
                })
                .ToList(),
            Reasons = missesByReason
                .OrderByDescending(kv => kv.Value)
                .Select(kv => new GetOverviewStatsResponse.FailoverReason
                {
                    Status = kv.Key.ToString(),
                    Count = kv.Value,
                })
                .ToList(),
            Buckets = byBucket
                .Select(kv =>
                {
                    var counts = new long[orderedProviders.Count];
                    foreach (var (provider, c) in kv.Value)
                    {
                        if (!indexOf.TryGetValue(provider, out var i)) continue;
                        counts[i] = c;
                    }
                    return new GetOverviewStatsResponse.FailoverBucket
                    {
                        Bucket = kv.Key,
                        Counts = counts.ToList(),
                    };
                })
                .ToList(),
        };
    }

    /// <summary>
    /// Metrics keys present in the current config label map. Deleted providers leave
    /// orphaned rollup rows; those must not appear as named overview rows (GUID fallback).
    /// </summary>
    internal static bool IsConfiguredMetricsKey(
        string metricsKey,
        IReadOnlyDictionary<string, string?> labelsByMetricsKey)
        => labelsByMetricsKey.ContainsKey(metricsKey);

    private GetOverviewStatsResponse.LiveTiles BuildLiveTiles(long articlesLastMinute, long errorsLastMinute)
    {
        return new GetOverviewStatsResponse.LiveTiles
        {
            ActiveReads = registry.Count,
            ArticlesPerMinute = articlesLastMinute,
            ErrorsPerMinute = errorsLastMinute,
            BytesServedPerMinute = liveStats.BytesServedLastMinute,
            InFlightArticleBytes = inFlightArticleBudget.LeasedBytes,
            InFlightArticleBudgetBytes = inFlightArticleBudget.CapBytes,
            InFlightArticleThrottleEvents = inFlightArticleBudget.ThrottleEvents,
        };
    }

    internal static List<GetOverviewStatsResponse.ThroughputPoint> BuildThroughputFromMinutes(
        IEnumerable<(long Minute, long Articles, long Misses, long Errors, long BytesServed, long BytesFetched)> minutes,
        long bucketSize)
    {
        var byBucket = new Dictionary<long, (long Articles, long Misses, long Errors, long BytesServed, long BytesFetched)>();
        foreach (var m in minutes)
        {
            var b = m.Minute - (m.Minute % bucketSize);
            byBucket.TryGetValue(b, out var cur);
            byBucket[b] = (
                cur.Articles + m.Articles,
                cur.Misses + m.Misses,
                cur.Errors + m.Errors,
                cur.BytesServed + m.BytesServed,
                cur.BytesFetched + m.BytesFetched);
        }

        return byBucket
            .OrderBy(kv => kv.Key)
            .Select(kv => new GetOverviewStatsResponse.ThroughputPoint
            {
                Bucket = kv.Key,
                Articles = kv.Value.Articles,
                Misses = kv.Value.Misses,
                Errors = kv.Value.Errors,
                BytesServed = kv.Value.BytesServed,
                BytesFetched = kv.Value.BytesFetched,
            })
            .ToList();
    }

    internal static List<GetOverviewStatsResponse.ThroughputPoint> BuildThroughputFromHourly(
        IEnumerable<(long Hour, long Articles, long Misses, long Errors, long BytesFetched)> hours,
        IEnumerable<(long EndedAt, long BytesServed)> sessions,
        long bucketSize)
    {
        var byBucket = new Dictionary<long, (long Articles, long Misses, long Errors, long BytesServed, long BytesFetched)>();
        foreach (var h in hours)
        {
            var b = h.Hour - (h.Hour % bucketSize);
            byBucket.TryGetValue(b, out var cur);
            byBucket[b] = (
                cur.Articles + h.Articles,
                cur.Misses + h.Misses,
                cur.Errors + h.Errors,
                cur.BytesServed,
                cur.BytesFetched + h.BytesFetched);
        }
        foreach (var (endedAt, bytes) in sessions)
        {
            var b = endedAt - (endedAt % bucketSize);
            byBucket.TryGetValue(b, out var cur);
            byBucket[b] = (cur.Articles, cur.Misses, cur.Errors, cur.BytesServed + bytes, cur.BytesFetched);
        }

        return byBucket
            .OrderBy(kv => kv.Key)
            .Select(kv => new GetOverviewStatsResponse.ThroughputPoint
            {
                Bucket = kv.Key,
                Articles = kv.Value.Articles,
                Misses = kv.Value.Misses,
                Errors = kv.Value.Errors,
                BytesServed = kv.Value.BytesServed,
                BytesFetched = kv.Value.BytesFetched,
            })
            .ToList();
    }

    internal static List<GetOverviewStatsResponse.ProviderRow> BuildProvidersFromMinutes(
        IEnumerable<(long Minute, string Provider, long Articles, long BytesFetched, long Misses, long Errors, long Retries, long SumDurationMs)> minutes,
        long windowStart,
        GetOverviewStatsRequest.OverviewWindow window,
        IReadOnlyDictionary<string, string?> labelsByMetricsKey)
    {
        var (sparkBuckets, sparkSize) = window switch
        {
            GetOverviewStatsRequest.OverviewWindow.Last1Hour => (60, OneMinute),
            GetOverviewStatsRequest.OverviewWindow.Last7Days => (168, OneHour),
            _ => (24, OneHour),
        };
        var sparkStart = windowStart - (windowStart % sparkSize);

        var byProvider = new Dictionary<string, ProviderAccumulator>();
        foreach (var m in minutes)
        {
            if (!byProvider.TryGetValue(m.Provider, out var acc))
                acc = new ProviderAccumulator(sparkBuckets);
            acc.Articles += m.Articles;
            acc.Misses += m.Misses;
            acc.Errors += m.Errors;
            acc.Retries += m.Retries;
            acc.SumDurationMs += m.SumDurationMs;
            acc.Bytes += m.BytesFetched;
            var idx = (int)((m.Minute - sparkStart) / sparkSize);
            if (idx >= 0 && idx < sparkBuckets)
            {
                acc.Spark[idx] += m.Articles;
                acc.ErrorSpark[idx] += m.Errors;
                acc.RetrySpark[idx] += m.Retries;
                acc.BytesSpark[idx] += m.BytesFetched;
                acc.DurationSpark[idx] += m.SumDurationMs;
            }
            byProvider[m.Provider] = acc;
        }

        return byProvider
            .Where(kv => IsConfiguredMetricsKey(kv.Key, labelsByMetricsKey))
            .Select(kv =>
            {
                var okArticles = Math.Max(0, kv.Value.Articles - kv.Value.Misses - kv.Value.Errors);
                return new GetOverviewStatsResponse.ProviderRow
                {
                    Provider = kv.Key,
                    Nickname = labelsByMetricsKey.GetValueOrDefault(kv.Key),
                    Articles = kv.Value.Articles,
                    BytesFetched = kv.Value.Bytes,
                    Errors = kv.Value.Errors,
                    Retries = kv.Value.Retries,
                    SpeedMbPerSec = CalculateSpeedMbPerSec(kv.Value.Bytes, kv.Value.SumDurationMs),
                    SpeedSpark = BuildSpeedSpark(kv.Value.BytesSpark, kv.Value.DurationSpark),
                    // SumDurationMs is Ok-only from rollup; divide by Ok count, not all attempts.
                    AvgDurationMs = okArticles > 0 ? (double)kv.Value.SumDurationMs / okArticles : 0,
                    ErrorRate = kv.Value.Articles > 0 ? (double)kv.Value.Errors / kv.Value.Articles : 0,
                    Spark = kv.Value.Spark.ToList(),
                    ErrorSpark = kv.Value.ErrorSpark.ToList(),
                    RetrySpark = kv.Value.RetrySpark.ToList(),
                };
            })
            .OrderByDescending(r => r.Articles)
            .ToList();
    }

    internal static List<GetOverviewStatsResponse.ProviderRow> BuildProvidersFromHourly(
        IEnumerable<(long Hour, string Provider, long Articles, long BytesFetched, long Misses, long Errors, long Retries, long SumDurationMs)> hours,
        long windowStart,
        long bucketSize,
        long nowMs,
        IReadOnlyDictionary<string, string?> labelsByMetricsKey)
    {
        var totalSpan = nowMs - windowStart;
        var sparkSize = OneDay;
        var sparkBuckets = Math.Max(1, (int)Math.Min(60, totalSpan / sparkSize + 1));
        var sparkStart = windowStart - (windowStart % sparkSize);

        var byProvider = new Dictionary<string, ProviderAccumulator>();
        foreach (var h in hours)
        {
            var host = h.Provider;
            if (!byProvider.TryGetValue(host, out var acc))
                acc = new ProviderAccumulator(sparkBuckets);
            acc.Articles += h.Articles;
            acc.Misses += h.Misses;
            acc.Errors += h.Errors;
            acc.Retries += h.Retries;
            acc.SumDurationMs += h.SumDurationMs;
            acc.Bytes += h.BytesFetched;
            var idx = (int)((h.Hour - sparkStart) / sparkSize);
            if (idx >= 0 && idx < sparkBuckets)
            {
                acc.Spark[idx] += h.Articles;
                acc.ErrorSpark[idx] += h.Errors;
                acc.RetrySpark[idx] += h.Retries;
                acc.BytesSpark[idx] += h.BytesFetched;
                acc.DurationSpark[idx] += h.SumDurationMs;
            }
            byProvider[host] = acc;
        }

        return byProvider
            .Where(kv => IsConfiguredMetricsKey(kv.Key, labelsByMetricsKey))
            .Select(kv =>
            {
                var okArticles = Math.Max(0, kv.Value.Articles - kv.Value.Misses - kv.Value.Errors);
                return new GetOverviewStatsResponse.ProviderRow
                {
                    Provider = kv.Key,
                    Nickname = labelsByMetricsKey.GetValueOrDefault(kv.Key),
                    Articles = kv.Value.Articles,
                    BytesFetched = kv.Value.Bytes,
                    Errors = kv.Value.Errors,
                    Retries = kv.Value.Retries,
                    SpeedMbPerSec = CalculateSpeedMbPerSec(kv.Value.Bytes, kv.Value.SumDurationMs),
                    SpeedSpark = BuildSpeedSpark(kv.Value.BytesSpark, kv.Value.DurationSpark),
                    AvgDurationMs = okArticles > 0 ? (double)kv.Value.SumDurationMs / okArticles : 0,
                    ErrorRate = kv.Value.Articles > 0 ? (double)kv.Value.Errors / kv.Value.Articles : 0,
                    Spark = kv.Value.Spark.ToList(),
                    ErrorSpark = kv.Value.ErrorSpark.ToList(),
                    RetrySpark = kv.Value.RetrySpark.ToList(),
                };
            })
            .OrderByDescending(r => r.Articles)
            .ToList();
    }

    private void ApplyLiveSpeedFallback(IEnumerable<GetOverviewStatsResponse.ProviderRow> providers)
    {
        foreach (var provider in providers.Where(provider => provider.SpeedMbPerSec is null))
        {
            var bytesPerMs = providerBytesTracker.GetRecentBytesPerMs(provider.Provider, LiveSpeedMaxAge);
            if (bytesPerMs > 0)
                provider.SpeedMbPerSec = bytesPerMs * 1000 / 1_000_000;
        }
    }

    private static double? CalculateSpeedMbPerSec(long bytes, long durationMs) =>
        bytes > 0 && durationMs > 0
            ? bytes * 1000d / durationMs / 1_000_000d
            : null;

    private static List<double> BuildSpeedSpark(
        IReadOnlyList<long> bytes,
        long[] durations) =>
        bytes.Select((value, index) =>
                CalculateSpeedMbPerSec(value, durations[index]) ?? 0)
            .ToList();

    private sealed class ProviderAccumulator
    {
        public long Articles, Misses, Errors, Retries, SumDurationMs, Bytes;
        public readonly long[] Spark;
        public readonly long[] ErrorSpark;
        public readonly long[] RetrySpark;
        public readonly long[] BytesSpark;
        public readonly long[] DurationSpark;
        public ProviderAccumulator(int n)
        {
            Spark = new long[n];
            ErrorSpark = new long[n];
            RetrySpark = new long[n];
            BytesSpark = new long[n];
            DurationSpark = new long[n];
        }
    }

    private static async Task<GetOverviewStatsResponse.HeatmapBlock> BuildHeatmapAsync(
        MetricsDbContext metrics,
        GetOverviewStatsRequest.OverviewWindow window,
        long nowMs)
    {
        var (mode, bucketSize, windowStart, windowEnd) = ResolveHeatmapWindow(window, nowMs);

        var hourly = await metrics.ProviderHourly
            .Where(h => h.Hour >= windowStart)
            .GroupBy(h => h.Hour)
            .Select(g => new { Hour = g.Key, Articles = g.Sum(x => x.Articles) })
            .ToListAsync().ConfigureAwait(false);

        var byBucket = new Dictionary<long, long>();
        long max = 0;
        foreach (var h in hourly)
        {
            var bucket = h.Hour - (h.Hour % bucketSize);
            byBucket.TryGetValue(bucket, out var c);
            c += h.Articles;
            byBucket[bucket] = c;
            if (c > max) max = c;
        }

        return new GetOverviewStatsResponse.HeatmapBlock
        {
            MaxCell = max,
            Mode = mode,
            WindowStartMs = windowStart,
            WindowEndMs = windowEnd,
            BucketSizeMs = bucketSize,
            Cells = byBucket
                .Select(kv => new GetOverviewStatsResponse.HeatmapCell
                {
                    Bucket = kv.Key,
                    Count = kv.Value,
                })
                .OrderBy(c => c.Bucket)
                .ToList(),
        };
    }

    private static (string Mode, long BucketSize, long WindowStart, long WindowEnd) ResolveHeatmapWindow(
        GetOverviewStatsRequest.OverviewWindow window, long nowMs)
    {
        var hourEnd = nowMs - (nowMs % OneHour);
        var dayEnd = nowMs - (nowMs % OneDay);

        return window switch
        {
            // Heatmap is hourly; for 1h reuse the day strip so the widget stays useful.
            GetOverviewStatsRequest.OverviewWindow.Last1Hour
                => ("day", OneHour, hourEnd - 23 * OneHour, hourEnd),

            GetOverviewStatsRequest.OverviewWindow.Last24Hours
                => ("day", OneHour, hourEnd - 23 * OneHour, hourEnd),

            GetOverviewStatsRequest.OverviewWindow.Last7Days
                => ("week", OneHour, dayEnd - 6 * OneDay, hourEnd),

            GetOverviewStatsRequest.OverviewWindow.Last30Days
                => ("month", OneHour, dayEnd - 29 * OneDay, hourEnd),

            GetOverviewStatsRequest.OverviewWindow.AllTime
                => ("year", OneDay, AlignYearStart(dayEnd), dayEnd),

            _ => ("week", OneHour, dayEnd - 6 * OneDay, hourEnd),
        };
    }

    private static long AlignYearStart(long todayDayStart)
    {
        var todayDow = ((int)DateTimeOffset.FromUnixTimeMilliseconds(todayDayStart).UtcDateTime.DayOfWeek + 6) % 7;
        var thisWeekMonday = todayDayStart - todayDow * OneDay;
        return thisWeekMonday - 52 * 7 * OneDay;
    }

    private static GetOverviewStatsResponse.LatencyBlock BuildLatency(IEnumerable<int> okDurationsMs)
    {
        var samples = okDurationsMs.ToList();
        if (samples.Count == 0) return new GetOverviewStatsResponse.LatencyBlock();

        samples.Sort();
        int Pct(double p)
        {
            var idx = (int)Math.Ceiling(p * samples.Count) - 1;
            return samples[Math.Clamp(idx, 0, samples.Count - 1)];
        }

        var buckets = new List<GetOverviewStatsResponse.LatencyBucket>();
        for (var i = 0; i < LatencyBucketEdges.Length - 1; i++)
        {
            var lo = LatencyBucketEdges[i];
            var hi = LatencyBucketEdges[i + 1];
            var count = samples.Count(d => d >= lo && d < hi);
            if (count == 0 && lo > 0) continue;
            buckets.Add(new GetOverviewStatsResponse.LatencyBucket { LoMs = lo, HiMs = hi, Count = count });
        }

        return new GetOverviewStatsResponse.LatencyBlock
        {
            P50Ms = Pct(0.50),
            P95Ms = Pct(0.95),
            P99Ms = Pct(0.99),
            Samples = samples.Count,
            Buckets = buckets,
        };
    }

    private static async Task<GetOverviewStatsResponse.CatalogueBlock> BuildCatalogueAsync(DavDatabaseContext ctx)
    {
        var sevenDaysAgo = DateTime.UtcNow.AddDays(-7);

        var files = ctx.Items.Where(i => i.Type == DavItem.ItemType.UsenetFile);
        var fileCount = await files.CountAsync().ConfigureAwait(false);
        var totalBytes = await files.SumAsync(i => (long?)i.FileSize).ConfigureAwait(false) ?? 0L;
        var largest = await files.MaxAsync(i => (long?)i.FileSize).ConfigureAwait(false) ?? 0L;
        var addedRecently = await files
            .Where(i => i.CreatedAt >= sevenDaysAgo)
            .CountAsync().ConfigureAwait(false);

        return new GetOverviewStatsResponse.CatalogueBlock
        {
            FileCount = fileCount,
            TotalBytes = totalBytes,
            LargestFileBytes = largest,
            AddedLast7Days = addedRecently,
        };
    }

    private static GetOverviewStatsResponse.SessionsBlock BuildSessionsBlock(
        IEnumerable<(int DurationMs, long BytesServed)> sessions)
    {
        var list = sessions.ToList();
        if (list.Count == 0) return new GetOverviewStatsResponse.SessionsBlock();

        return new GetOverviewStatsResponse.SessionsBlock
        {
            Count = list.Count,
            TotalBytesServed = list.Sum(x => x.BytesServed),
            AvgDurationMs = (long)list.Average(x => (double)x.DurationMs),
            LongestDurationMs = list.Max(x => x.DurationMs),
            BiggestReadBytes = list.Max(x => x.BytesServed),
        };
    }

    private static async Task<List<GetOverviewStatsResponse.IndexerRow>> BuildIndexersAsync(DavDatabaseContext ctx)
    {
        var cutoff = DateTime.UtcNow.AddDays(-30);
        var rows = await ctx.HistoryItems
            .Where(h => h.CreatedAt >= cutoff && h.IndexerName != null)
            .GroupBy(h => h.IndexerName!)
            .Select(g => new
            {
                Name = g.Key,
                Completed = (long)g.Count(x => x.DownloadStatus == HistoryItem.DownloadStatusOption.Completed),
                Failed = (long)g.Count(x => x.DownloadStatus == HistoryItem.DownloadStatusOption.Failed),
                BytesCompleted = g
                    .Where(x => x.DownloadStatus == HistoryItem.DownloadStatusOption.Completed)
                    .Sum(x => (long?)x.TotalSegmentBytes) ?? 0L,
                AvgSecondsRaw = g
                    .Where(x => x.DownloadStatus == HistoryItem.DownloadStatusOption.Completed)
                    .Average(x => (double?)x.DownloadTimeSeconds),
            })
            .ToListAsync().ConfigureAwait(false);

        return rows
            .Select(r => new GetOverviewStatsResponse.IndexerRow
            {
                Name = r.Name,
                Completed = r.Completed,
                Failed = r.Failed,
                BytesCompleted = r.BytesCompleted,
                AvgSeconds = (int)(r.AvgSecondsRaw ?? 0),
                SuccessRate = r.Completed + r.Failed > 0 ? (double)r.Completed / (r.Completed + r.Failed) : 0,
            })
            .OrderByDescending(r => r.Completed + r.Failed)
            .ToList();
    }

    private static async Task<GetOverviewStatsResponse.LifetimeBlock> BuildLifetimeAsync(MetricsDbContext metrics)
    {
        var bytesFetched = await metrics.ProviderHourly
            .SumAsync(x => (long?)x.BytesFetched).ConfigureAwait(false) ?? 0L;
        var articles = await metrics.ProviderHourly
            .SumAsync(x => (long?)x.Articles).ConfigureAwait(false) ?? 0L;
        var firstHour = await metrics.ProviderHourly
            .OrderBy(x => x.Hour)
            .Select(x => (long?)x.Hour)
            .FirstOrDefaultAsync().ConfigureAwait(false);

        var sessionCount = await metrics.ReadSessions.CountAsync().ConfigureAwait(false);
        var bytesRead = await metrics.ReadSessions
            .SumAsync(x => (long?)x.BytesServed).ConfigureAwait(false) ?? 0L;
        var readMs = await metrics.ReadSessions
            .SumAsync(x => (long?)x.DurationMs).ConfigureAwait(false) ?? 0L;

        return new GetOverviewStatsResponse.LifetimeBlock
        {
            BytesFetched = bytesFetched,
            BytesRead = bytesRead,
            Articles = articles,
            ReadSessions = sessionCount,
            ReadSeconds = readMs / 1000,
            FirstSeenAt = firstHour,
        };
    }

    private static async Task<GetOverviewStatsResponse.RecordsBlock> BuildRecordsAsync(MetricsDbContext metrics)
    {
        var dayRow = await metrics.ProviderHourly
            .GroupBy(x => x.Hour / OneDay)
            .Select(g => new { DayBucket = g.Key, Bytes = g.Sum(x => x.BytesFetched) })
            .OrderByDescending(x => x.Bytes)
            .FirstOrDefaultAsync().ConfigureAwait(false);

        var hourRow = await metrics.ProviderHourly
            .GroupBy(x => x.Hour)
            .Select(g => new { Hour = g.Key, Bytes = g.Sum(x => x.BytesFetched) })
            .OrderByDescending(x => x.Bytes)
            .FirstOrDefaultAsync().ConfigureAwait(false);

        return new GetOverviewStatsResponse.RecordsBlock
        {
            BestDayBytes = dayRow?.Bytes ?? 0,
            BestDayAt = dayRow != null ? dayRow.DayBucket * OneDay : null,
            BestHourBytes = hourRow?.Bytes ?? 0,
            BestHourAt = hourRow?.Hour,
        };
    }
}
