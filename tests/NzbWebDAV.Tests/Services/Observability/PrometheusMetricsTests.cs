using System.Text;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Models;
using NzbWebDAV.Services;
using NzbWebDAV.Services.Observability;
using Prometheus;

namespace NzbWebDAV.Tests.Services.Observability;

public sealed class PrometheusMetricsTests
{
    private static readonly string[] SegmentCacheMetricNames =
    [
        "nzbdav_segment_cache_enabled",
        "nzbdav_segment_cache_catalog_ready",
        "nzbdav_segment_cache_catalog_load_duration_seconds",
        "nzbdav_segment_cache_entries",
        "nzbdav_segment_cache_bytes",
        "nzbdav_segment_cache_max_bytes",
        "nzbdav_segment_cache_hits_total",
        "nzbdav_segment_cache_misses_total",
        "nzbdav_segment_cache_lookup_unavailable_total",
        "nzbdav_segment_cache_bytes_served_total",
        "nzbdav_segment_cache_batch_bypass_requests_total",
        "nzbdav_segment_cache_batch_bypass_articles_total",
        "nzbdav_segment_cache_write_attempts_total",
        "nzbdav_segment_cache_write_commits_total",
        "nzbdav_segment_cache_write_skipped_total",
        "nzbdav_segment_cache_write_failures_total",
        "nzbdav_segment_cache_read_failures_total",
        "nzbdav_segment_cache_evictions_total",
        "nzbdav_segment_cache_evicted_bytes_total",
        "nzbdav_segment_cache_temporary_files_cleaned_total",
    ];

    [Fact]
    public async Task RecordsOnlyBoundedSeekAndFetchLabels()
    {
        var registry = new CollectorRegistry();
        var metrics = new PrometheusMetrics(registry);

        metrics.RecordSeek("warm", TimeSpan.FromMilliseconds(12));
        metrics.RecordSegmentFetch("provider-a", "ok", TimeSpan.FromMilliseconds(20));

        await using var stream = new MemoryStream();
        await registry.CollectAndExportAsTextAsync(stream);
        var exposition = Encoding.UTF8.GetString(stream.ToArray());

        Assert.Contains("nzbdav_seek_total", exposition);
        Assert.Contains("kind=\"warm\"", exposition);
        Assert.Contains("nzbdav_segment_fetches_total", exposition);
        Assert.Contains("provider_key=\"provider-a\"", exposition);
        Assert.DoesNotContain("path=", exposition);
        Assert.DoesNotContain("filename=", exposition);
    }

    [Fact]
    public async Task RegistersSharedStreamRetentionGauges()
    {
        var registry = new CollectorRegistry();
        _ = new PrometheusMetrics(registry);

        await using var stream = new MemoryStream();
        await registry.CollectAndExportAsTextAsync(stream);
        var exposition = Encoding.UTF8.GetString(stream.ToArray());

        Assert.Contains("nzbdav_shared_stream_ring_retained_bytes", exposition);
        Assert.Contains("nzbdav_shared_stream_ring_retained_bytes_peak", exposition);
        Assert.Contains("nzbdav_shared_stream_ring_logical_bytes", exposition);
        Assert.Contains("nzbdav_shared_stream_pump_scratch_bytes", exposition);
        Assert.Contains("nzbdav_shared_stream_live_entries", exposition);
        Assert.Contains("nzbdav_shared_stream_ready_entries", exposition);
        Assert.Contains("nzbdav_shared_stream_draining_entries", exposition);
        Assert.Contains("nzbdav_shared_stream_lagging_readers", exposition);
        Assert.Contains("nzbdav_shared_stream_pressure_detaches_total", exposition);
        Assert.Contains("nzbdav_shared_stream_pressure_reaps_total", exposition);
    }

    [Fact]
    public async Task ProviderPoolMetricsExposeAndRemoveAdmissionState()
    {
        var registry = new CollectorRegistry();
        var metrics = new PrometheusMetrics(registry);
        var churn = new ConnectionPoolChurn(1, 2, 3, 4, 5, 6, 7);
        var admission = new ProviderConnectionAdmissionSnapshot(
            ConfiguredTransferLimit: 20,
            EffectiveTransferLimit: 15,
            BaseMetadataCapacity: 0,
            MetadataBurstAllowance: 7,
            MaxMetadataCapacity: 7,
            ActiveTransferOperations: 10,
            ActiveMetadataOperations: 5,
            WaitingTransferOperations: 2,
            WaitingMetadataOperations: 3);
        var snapshot = new ProviderConnectionSnapshot(
            "provider-a", "news.example", ProviderType.Pooled,
            LiveConnections: 15,
            IdleConnections: 0,
            ActiveConnections: 15,
            AvailableConnections: 0,
            PendingSelections: 1,
            churn,
            LearnedConnectionLimit: 17,
            ConfiguredMaxConnections: 50,
            EffectiveMaxConnections: 15,
            admission);

        metrics.SetPool(snapshot);
        var exposition = await ExportAsync(registry);

        Assert.Contains("state=\"transfer_active\"} 10", exposition);
        Assert.Contains("state=\"metadata_active\"} 5", exposition);
        Assert.Contains("state=\"transfer_waiting\"} 2", exposition);
        Assert.Contains("state=\"metadata_waiting\"} 3", exposition);
        Assert.Contains("limit=\"configured\"} 50", exposition);
        Assert.Contains("limit=\"effective\"} 15", exposition);
        Assert.Contains("limit=\"transfer_configured\"} 20", exposition);
        Assert.Contains("limit=\"transfer_effective\"} 15", exposition);
        Assert.Contains("limit=\"metadata_base\"} 0", exposition);
        Assert.Contains("limit=\"metadata_burst\"} 7", exposition);
        Assert.Contains("limit=\"metadata_max\"} 7", exposition);

        metrics.SetPool(snapshot with { Admission = null, LearnedConnectionLimit = null });
        exposition = await ExportAsync(registry);

        Assert.DoesNotContain("state=\"transfer_active\"", exposition);
        Assert.DoesNotContain("state=\"metadata_active\"", exposition);
        Assert.DoesNotContain("limit=\"transfer_configured\"", exposition);
        Assert.DoesNotContain("limit=\"learned\"", exposition);
    }

    [Fact]
    public async Task HealthCheckGateMetricsExposeAndRemoveAdmissionState()
    {
        var registry = new CollectorRegistry();
        var metrics = new PrometheusMetrics(registry);

        metrics.SetHealthCheckGate(new HealthCheckConnectionGateSnapshot(
            Limit: 12,
            Active: 7,
            WaitingQueue: 2,
            WaitingBackground: 5));
        var exposition = await ExportAsync(registry);

        Assert.Contains("nzbdav_health_check_gate_operations{state=\"active\"} 7", exposition);
        Assert.Contains("state=\"waiting_queue\"} 2", exposition);
        Assert.Contains("state=\"waiting_background\"} 5", exposition);
        Assert.Contains("nzbdav_health_check_gate_limit{limit=\"effective\"} 12", exposition);

        metrics.ClearHealthCheckGate();
        exposition = await ExportAsync(registry);

        Assert.DoesNotContain("nzbdav_health_check_gate_operations{", exposition);
        Assert.DoesNotContain("nzbdav_health_check_gate_limit{", exposition);
    }

    [Fact]
    public async Task SegmentCacheMetrics_RegisterWithoutDynamicLabels()
    {
        var registry = new CollectorRegistry();
        var metrics = new PrometheusMetrics(registry);
        metrics.SetSegmentCache(DisabledSnapshot());

        var exposition = await ExportAsync(registry);
        foreach (var name in SegmentCacheMetricNames)
            Assert.Contains(name, exposition);

        Assert.DoesNotContain("path=", exposition);
        Assert.DoesNotContain("filename=", exposition);
        Assert.DoesNotContain("message_id=", exposition);
        Assert.DoesNotContain("provider_host=", exposition);
        Assert.DoesNotContain("nzbdav_segment_cache_queued", exposition);
        foreach (var line in exposition.Split('\n')
                     .Where(static line =>
                         line.StartsWith("nzbdav_segment_cache_", StringComparison.Ordinal)
                         && !line.StartsWith('#')))
        {
            Assert.DoesNotContain("NaN", line);
            Assert.DoesNotContain("+Inf", line);
            Assert.DoesNotContain("-Inf", line);
        }
    }

    [Fact]
    public async Task SegmentCacheMetrics_ProjectDisabledInitialAndReadySnapshots()
    {
        var registry = new CollectorRegistry();
        var metrics = new PrometheusMetrics(registry);

        metrics.SetSegmentCache(DisabledSnapshot());
        var disabled = await ExportAsync(registry);
        Assert.Contains("nzbdav_segment_cache_enabled 0", disabled);
        Assert.Contains("nzbdav_segment_cache_catalog_ready 0", disabled);

        metrics.SetSegmentCache(new SegmentCacheSnapshot(
            Enabled: true,
            CatalogReady: false,
            CatalogLoadDurationMs: null,
            Entries: 0,
            CurrentBytes: 0,
            MaxBytes: 1024,
            Hits: 0,
            Misses: 0,
            LookupUnavailable: 0,
            BytesServed: 0,
            BatchBypassRequests: 0,
            BatchBypassArticles: 0,
            WriteAttempts: 0,
            WriteCommits: 0,
            WriteSkipped: 0,
            WriteFailures: 0,
            ReadFailures: 0,
            Evictions: 0,
            BytesEvicted: 0,
            TemporaryFilesCleaned: 0,
            QueuedWriteBytes: null,
            PeakQueuedWriteBytes: null));
        var initial = await ExportAsync(registry);
        Assert.Contains("nzbdav_segment_cache_enabled 1", initial);
        Assert.Contains("nzbdav_segment_cache_catalog_ready 0", initial);
        Assert.Contains("nzbdav_segment_cache_max_bytes 1024", initial);

        var ready = ReadySnapshot();
        metrics.SetSegmentCache(ready);
        var readyExposition = await ExportAsync(registry);
        Assert.Contains("nzbdav_segment_cache_catalog_ready 1", readyExposition);
        Assert.Contains("nzbdav_segment_cache_catalog_load_duration_seconds 0.012", readyExposition);
        Assert.Contains("nzbdav_segment_cache_entries 4", readyExposition);
        Assert.Contains("nzbdav_segment_cache_bytes 40", readyExposition);
        Assert.Contains("nzbdav_segment_cache_hits_total 3", readyExposition);
        Assert.Contains("nzbdav_segment_cache_bytes_served_total 30", readyExposition);

        metrics.SetSegmentCache(ready);
        var second = await ExportAsync(registry);
        Assert.Equal(
            CountMetric(readyExposition, "nzbdav_segment_cache_hits_total"),
            CountMetric(second, "nzbdav_segment_cache_hits_total"));
        Assert.Contains("nzbdav_segment_cache_hits_total 3", second);
    }

    private static SegmentCacheSnapshot DisabledSnapshot() =>
        new(
            Enabled: false,
            CatalogReady: false,
            CatalogLoadDurationMs: null,
            Entries: 0,
            CurrentBytes: 0,
            MaxBytes: 0,
            Hits: 0,
            Misses: 0,
            LookupUnavailable: 0,
            BytesServed: 0,
            BatchBypassRequests: 0,
            BatchBypassArticles: 0,
            WriteAttempts: 0,
            WriteCommits: 0,
            WriteSkipped: 0,
            WriteFailures: 0,
            ReadFailures: 0,
            Evictions: 0,
            BytesEvicted: 0,
            TemporaryFilesCleaned: 0,
            QueuedWriteBytes: null,
            PeakQueuedWriteBytes: null);

    private static SegmentCacheSnapshot ReadySnapshot() =>
        new(
            Enabled: true,
            CatalogReady: true,
            CatalogLoadDurationMs: 12,
            Entries: 4,
            CurrentBytes: 40,
            MaxBytes: 1024,
            Hits: 3,
            Misses: 1,
            LookupUnavailable: 0,
            BytesServed: 30,
            BatchBypassRequests: 2,
            BatchBypassArticles: 7,
            WriteAttempts: 4,
            WriteCommits: 3,
            WriteSkipped: 1,
            WriteFailures: 0,
            ReadFailures: 0,
            Evictions: 1,
            BytesEvicted: 10,
            TemporaryFilesCleaned: 2,
            QueuedWriteBytes: null,
            PeakQueuedWriteBytes: null);

    private static int CountMetric(string exposition, string name) =>
        exposition.Split('\n').Count(line => line.StartsWith(name, StringComparison.Ordinal) && !line.StartsWith('#'));

    private static async Task<string> ExportAsync(CollectorRegistry registry)
    {
        await using var stream = new MemoryStream();
        await registry.CollectAndExportAsTextAsync(stream);
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
