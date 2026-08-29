using System.Text;
using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Models;
using NzbWebDAV.Services;
using NzbWebDAV.Services.Observability;
using Prometheus;

namespace NzbWebDAV.Tests.Services.Observability;

public sealed class PrometheusMetricsTests
{
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

    private static async Task<string> ExportAsync(CollectorRegistry registry)
    {
        await using var stream = new MemoryStream();
        await registry.CollectAndExportAsTextAsync(stream);
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
