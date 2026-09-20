using NzbWebDAV.Benchmarks;

namespace NzbWebDAV.Tests.Services;

public sealed class NativeCacheScaleReportTests
{
    [Fact]
    public async Task Smoke_UsesRealIndexedCatalogue_WithoutPayloadFiles()
    {
        var report = await NativeCacheScaleReport.RunAsync("smoke", samples: 2);
        Assert.Equal(32, report.Entries);
        Assert.Equal(32L * 256 * 1024 * 1024, report.LogicalBytes);
        Assert.Equal(32 * 64 - 1, report.Blocks);
        Assert.Equal(4 * 1024 * 1024, report.MissingBytes);
        Assert.Equal(0, report.PayloadFiles);
        Assert.True(report.CatalogueBytes > 0);
        Assert.True(report.FinalWorkingSetBytes > 0);
        Assert.Equal(6, report.Measurements.Count);
        Assert.All(report.Measurements.Values, measurement => Assert.Equal(2, measurement.Samples));
        Assert.Contains(report.QueryPlans["entries"], line => line.Contains("EntryPage", StringComparison.Ordinal));
        Assert.Contains(report.QueryPlans["ranges"], line => line.Contains("sqlite_autoindex_Blocks", StringComparison.Ordinal));
        Assert.Contains(report.QueryPlans["pressure-candidates"], line => line.Contains("EntryEvictionCursor", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InvalidShapeOrSampleCount_IsRejectedBeforeCreatingFixture()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => NativeCacheScaleReport.RunAsync("production"));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => NativeCacheScaleReport.RunAsync("smoke", samples: 0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => NativeCacheScaleReport.RunAsync("smoke", samples: 101));
    }
}
