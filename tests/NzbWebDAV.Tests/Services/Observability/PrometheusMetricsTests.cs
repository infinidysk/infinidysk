using System.Text;
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
}
