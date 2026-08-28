using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Models;
using NzbWebDAV.Services.Metrics;

namespace NzbWebDAV.Tests.Services.Metrics;

public class ProviderCircuitOverviewEnricherTests
{
    private const string KeyA = "11111111-1111-1111-1111-111111111111";
    private const string KeyB = "22222222-2222-2222-2222-222222222222";

    [Fact]
    public void EnrichProviders_MergesBreakerFieldsAndAddsConfiguredProvidersWithoutMetrics()
    {
        var providers = new List<ProviderOverviewRow>
        {
            new()
            {
                Provider = KeyA,
                Nickname = "Primary",
                Articles = 10,
                Spark = [1, 2],
                ErrorSpark = [0, 1],
                RetrySpark = [2, 0],
                SpeedSeries =
                [
                    new() { Bucket = 10, SpeedMbPerSec = 1.5, BytesFetched = 1500 },
                ],
            },
        };
        var snapshots = new List<ProviderCircuitRuntimeSnapshot>
        {
            new(KeyA, "news.example", ProviderType.Pooled, new ProviderCircuitBreakerSnapshot(
                ProviderCircuitState.Open, 42, "3 failures in 3-sample window", 1, 3, 0)),
            new(KeyB, "backup.example", ProviderType.BackupOnly, new ProviderCircuitBreakerSnapshot(
                ProviderCircuitState.Closed, null, null, 0, 0, 2)),
        };
        var labels = new Dictionary<string, string?>
        {
            [KeyA] = "Primary",
            [KeyB] = "Backup",
        };

        var enriched = ProviderCircuitOverviewEnricher.EnrichProviders(providers, snapshots, labels);

        Assert.Equal(2, enriched.Count);
        var primary = enriched.Single(p => p.Provider == KeyA);
        Assert.Equal("open", primary.CircuitState);
        Assert.Equal(42, primary.CooldownRemainingSeconds);
        Assert.Equal(10, primary.Articles);
        Assert.Equal([0L, 1L], primary.ErrorSpark);
        Assert.Equal([2L, 0L], primary.RetrySpark);
        var seriesPoint = Assert.Single(primary.SpeedSeries);
        Assert.Equal(10, seriesPoint.Bucket);
        Assert.Equal(1.5, seriesPoint.SpeedMbPerSec);
        Assert.Equal(1500, seriesPoint.BytesFetched);

        var backup = enriched.Single(p => p.Provider == KeyB);
        Assert.Equal("closed", backup.CircuitState);
        Assert.Equal(0, backup.Articles);
        Assert.Equal("Backup", backup.Nickname);
        Assert.Equal(2, backup.ArticleMissCount);
        Assert.Empty(backup.SpeedSeries);
    }
}
