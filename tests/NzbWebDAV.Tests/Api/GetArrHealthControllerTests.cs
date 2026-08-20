using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using NzbWebDAV.Api.Controllers.GetArrHealth;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Interceptors;
using NzbWebDAV.Database.MigrationHelpers;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Database.Models.Metrics;
using NzbWebDAV.Services;

namespace NzbWebDAV.Tests.Api;

public sealed class GetArrHealthControllerTests
{
    [Fact]
    public async Task BuildResponseAsync_WhenUnconfigured_ReturnsConfiguredFalseWithoutOpeningDb()
    {
        var config = new ConfigManager();
        using var service = new ArrHealthService(config);
        var controller = new GetArrHealthController(config, service)
        {
            MetricsContextFactory = () => throw new InvalidOperationException("metrics must not open"),
        };

        var disabled = await controller.BuildResponseAsync(
            GetArrHealthRequest.ArrHealthWindow.Last24Hours,
            DateTimeOffset.UtcNow,
            CancellationToken.None);
        Assert.False(disabled.Configured);

        config.UpdateValues([
            new ConfigItem { ConfigName = ConfigKeys.ArrHealthEnabled, ConfigValue = "false" },
            new ConfigItem
            {
                ConfigName = ConfigKeys.ArrInstances,
                ConfigValue = JsonSerializer.Serialize(new ArrConfig
                {
                    SonarrInstances =
                    [
                        new ArrConfig.ConnectionDetails { Host = "http://sonarr:8989", ApiKey = "k" },
                    ],
                }),
            },
        ]);
        var masterOff = await controller.BuildResponseAsync(
            GetArrHealthRequest.ArrHealthWindow.Last24Hours,
            DateTimeOffset.UtcNow,
            CancellationToken.None);
        Assert.False(masterOff.Configured);
    }

    [Fact]
    public async Task Build_FiltersWindow_ComputesPercentiles_AndExcludesOrphanedInstances()
    {
        await using var harness = await MetricsHarness.CreateAsync();
        var now = DateTimeOffset.Parse("2026-08-19T20:00:00Z");
        var nowMs = now.ToUnixTimeMilliseconds();
        var sonarrKey = "sonarr|http://sonarr:8989";
        var orphanKey = "sonarr|http://gone:8989";

        harness.Context.ArrImportEvents.AddRange(
            Event(sonarrKey, 1, nowMs - 30_000, 10_000),
            Event(sonarrKey, 2, nowMs - 40_000, 20_000),
            Event(sonarrKey, 3, nowMs - 50_000, 30_000),
            Event(sonarrKey, 4, nowMs - 60_000, 40_000),
            Event(sonarrKey, 5, nowMs - 70_000, 50_000),
            Event(sonarrKey, 6, nowMs - 3 * 24 * 60 * 60 * 1000L, 999_000),
            Event(orphanKey, 7, nowMs - 10_000, 5_000));
        await harness.Context.SaveChangesAsync();

        var config = EnabledSonarrConfig();
        using var service = new ArrHealthService(config);
        var controller = new GetArrHealthController(config, service)
        {
            MetricsContextFactory = () => Clone(harness),
        };

        var response = await controller.BuildResponseAsync(
            GetArrHealthRequest.ArrHealthWindow.Last24Hours,
            now,
            CancellationToken.None);

        Assert.True(response.Configured);
        var row = Assert.Single(response.Instances);
        Assert.Equal(sonarrKey, row.Key);
        Assert.Equal(5, row.Imports);
        Assert.Equal(30_000, row.MedianHandoffMs);
        Assert.Equal(50_000, row.P95HandoffMs);
        Assert.Equal(5, response.Summary.ImportsCompleted);
        Assert.Equal(30_000, response.Summary.MedianHandoffMs);
        Assert.Equal(50_000, response.Summary.P95HandoffMs);
        Assert.DoesNotContain(response.Instances, r => r.Key == orphanKey);
    }

    [Fact]
    public void Build_OrdersAwaitingTop10_AndFlagsUnusualWaits()
    {
        var now = DateTimeOffset.Parse("2026-08-19T20:00:00Z");
        var created = DateTime.SpecifyKind(now.UtcDateTime.AddMinutes(-10).ToLocalTime(), DateTimeKind.Unspecified);
        var snapshot = new ArrHealthSnapshot
        {
            InstanceKey = "sonarr|http://sonarr:8989",
            DisplayName = "Sonarr Main",
            AppType = "sonarr",
            Host = "http://sonarr:8989",
            Status = ArrInstanceHealthStatus.Degraded,
            MedianHandoffMs30d = 10_000,
            MedianSampleCount30d = 5,
            AwaitingCount = 12,
            Awaiting = Enumerable.Range(0, 12).Select(i => new ArrAwaitingSnapshot
            {
                Title = $"item-{i}",
                DownloadId = Guid.NewGuid(),
                CreatedAt = DateTime.SpecifyKind(
                    now.UtcDateTime.AddMinutes(-1 - i).ToLocalTime(),
                    DateTimeKind.Unspecified),
            }).ToList(),
        };

        var details = new ArrConfig.ConnectionDetails { Host = "http://sonarr:8989", ApiKey = "k", Name = "Sonarr Main" };
        var response = GetArrHealthController.Build(
            [],
            [snapshot],
            [("sonarr", details)],
            now);

        Assert.Equal(10, response.Awaiting.Count);
        Assert.Equal("item-11", response.Awaiting[0].Title);
        Assert.True(response.Awaiting[0].WaitingMs >= response.Awaiting[^1].WaitingMs);
        Assert.Contains(response.Awaiting, a => a.IsUnusual);
        Assert.Equal("degraded", Assert.Single(response.Instances).Status);
        Assert.Equal(1, response.Summary.Degraded);
        Assert.Equal(1, response.Summary.InstancesOnline);

        var notUnusual = GetArrHealthController.Build(
            [],
            [snapshot with
            {
                MedianSampleCount30d = 4,
                AwaitingCount = 1,
                Awaiting = [new ArrAwaitingSnapshot { Title = "short", CreatedAt = created }],
            }],
            [("sonarr", details)],
            now);
        Assert.False(Assert.Single(notUnusual.Awaiting).IsUnusual);

        var noHistory = GetArrHealthController.Build(
            [],
            [snapshot with
            {
                AwaitingCount = 1,
                Awaiting = [new ArrAwaitingSnapshot { Title = "unknown" }],
            }],
            [("sonarr", details)],
            now);
        Assert.Null(Assert.Single(noHistory.Awaiting).WaitingMs);
        Assert.False(noHistory.Awaiting[0].IsUnusual);
    }

    private static ArrImportEvent Event(string key, int recordId, long importedAtMs, long handoffMs) =>
        new()
        {
            InstanceKey = key,
            ArrRecordId = recordId,
            DownloadId = Guid.NewGuid(),
            ImportedAtMs = importedAtMs,
            HandoffMs = handoffMs,
            Title = $"t-{recordId}",
        };

    private static ConfigManager EnabledSonarrConfig()
    {
        var config = new ConfigManager();
        config.UpdateValues([
            new ConfigItem
            {
                ConfigName = ConfigKeys.ArrInstances,
                ConfigValue = JsonSerializer.Serialize(new ArrConfig
                {
                    SonarrInstances =
                    [
                        new ArrConfig.ConnectionDetails { Host = "http://sonarr:8989", ApiKey = "k", Name = "Sonarr Main" },
                    ],
                }),
            },
        ]);
        return config;
    }

    private static MetricsDbContext Clone(MetricsHarness harness)
    {
        var options = new DbContextOptionsBuilder<MetricsDbContext>()
            .UseSqlite($"Data Source={harness.Path}")
            .AddInterceptors(new SqliteMetricsPragmas())
            .ReplaceService<IMigrationsSqlGenerator, SqliteMigrationsSqlGenerator<SqliteMigrationsSqlGenerator>>()
            .Options;
        return new MetricsDbContext(options);
    }

    private sealed class MetricsHarness : IAsyncDisposable
    {
        private readonly string _dir;

        private MetricsHarness(string dir, string path, MetricsDbContext context)
        {
            _dir = dir;
            Path = path;
            Context = context;
        }

        public string Path { get; }
        public MetricsDbContext Context { get; }

        public static async Task<MetricsHarness> CreateAsync()
        {
            var dir = System.IO.Path.Join(System.IO.Path.GetTempPath(), $"nzbdav-arr-health-api-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            var path = System.IO.Path.Join(dir, "metrics.sqlite");
            var options = new DbContextOptionsBuilder<MetricsDbContext>()
                .UseSqlite($"Data Source={path}")
                .AddInterceptors(new SqliteMetricsPragmas())
                .ReplaceService<IMigrationsSqlGenerator, SqliteMigrationsSqlGenerator<SqliteMigrationsSqlGenerator>>()
                .Options;
            var context = new MetricsDbContext(options);
            await context.Database.MigrateAsync();
            return new MetricsHarness(dir, path, context);
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            try { Directory.Delete(_dir, recursive: true); }
            catch (IOException) { /* best effort */ }
        }
    }
}
