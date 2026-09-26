using System.Text.Json;
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
                ProviderCircuitState.Open, 42, "3 failures in 3-sample window", 1, 1, 3, 0)),
            new(KeyB, "backup.example", ProviderType.BackupOnly, new ProviderCircuitBreakerSnapshot(
                ProviderCircuitState.Closed, null, null, 0, 0, 0, 2)),
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
        Assert.Equal("Pooled", primary.ProviderType);
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
        Assert.Equal("BackupOnly", backup.ProviderType);
        Assert.Empty(backup.SpeedSeries);
    }

    [Fact]
    public void EnrichProviders_UsesRuntimeProviderTypeForRole()
    {
        var providers = new List<ProviderOverviewRow> { new() { Provider = KeyA, Articles = 5 } };
        var snapshots = new List<ProviderCircuitRuntimeSnapshot>
        {
            new(KeyA, "news.example", ProviderType.BackupAndStats, new ProviderCircuitBreakerSnapshot(
                ProviderCircuitState.Closed, null, null, 0, 0, 0, 0)),
            new(KeyB, "disabled.example", ProviderType.Disabled, new ProviderCircuitBreakerSnapshot(
                ProviderCircuitState.Closed, null, null, 0, 0, 0, 0)),
        };

        var enriched = ProviderCircuitOverviewEnricher.EnrichProviders(providers, snapshots, new Dictionary<string, string?>());

        var backup = enriched.Single(p => p.Provider == KeyA);
        Assert.Equal("BackupAndStats", backup.ProviderType);
        Assert.Equal(5, backup.Articles);
        Assert.Equal("Disabled", enriched.Single(p => p.Provider == KeyB).ProviderType);
    }

    [Fact]
    public void EnrichProviders_LeavesProviderTypeNullWithoutRuntimeSnapshot()
    {
        var enriched = ProviderCircuitOverviewEnricher.EnrichProviders(
            [new ProviderOverviewRow { Provider = KeyA }],
            [],
            new Dictionary<string, string?>());

        Assert.Null(Assert.Single(enriched).ProviderType);
    }

    [Fact]
    public void ToLivePayload_IncludesConsecutiveTrips()
    {
        var snapshots = new List<ProviderCircuitRuntimeSnapshot>
        {
            new(KeyA, "news.example", ProviderType.Pooled, new ProviderCircuitBreakerSnapshot(
                ProviderCircuitState.Open, 42, "connection refused", 5, 3, 8, 0)),
        };

        var payload = Assert.Single(ProviderCircuitOverviewEnricher.ToLivePayload(
            snapshots, new Dictionary<string, string?> { [KeyA] = "Primary" }));
        var json = JsonSerializer.SerializeToElement(payload);

        Assert.Equal(3, json.GetProperty("consecutiveTrips").GetInt64());
    }
}
