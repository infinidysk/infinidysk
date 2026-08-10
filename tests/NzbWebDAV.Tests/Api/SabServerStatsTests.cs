using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using NzbWebDAV.Api.SabControllers.GetServerStats;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Interceptors;
using NzbWebDAV.Database.MigrationHelpers;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Database.Models.Metrics;
using NzbWebDAV.Models;

namespace NzbWebDAV.Tests.Api;

public sealed class SabServerStatsTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    private static readonly long NowMs = new DateTimeOffset(2026, 8, 12, 15, 0, 0, TimeSpan.Zero)
        .ToUnixTimeMilliseconds();
    private static readonly long DayBoundary = new DateTimeOffset(2026, 8, 12, 0, 0, 0, TimeSpan.Zero)
        .ToUnixTimeMilliseconds();
    private static readonly long WeekBoundary = new DateTimeOffset(2026, 8, 10, 0, 0, 0, TimeSpan.Zero)
        .ToUnixTimeMilliseconds();
    private static readonly long MonthBoundary = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero)
        .ToUnixTimeMilliseconds();

    [Fact]
    public async Task BuildAsync_ComputesDayWeekMonthTotals()
    {
        await using var harness = await MetricsHarness.CreateAsync();
        var provider = MakeProvider("news.example.com", "alice");
        var key = UsenetProviderIdentity.MetricsKey(provider);
        var config = ConfigWithProviders(provider);

        harness.Context.ProviderHourly.AddRange(
            Hour(key, MonthBoundary - 3_600_000, bytes: 100),
            Hour(key, WeekBoundary - 3_600_000, bytes: 200),
            Hour(key, DayBoundary - 3_600_000, bytes: 300),
            Hour(key, DayBoundary + 3_600_000, bytes: 400));
        await harness.Context.SaveChangesAsync();

        var stats = await GetServerStatsController.BuildAsync(harness.Context, config, NowMs, Utc);

        Assert.Equal(1000, stats.Total);
        Assert.Equal(900, stats.Month);
        Assert.Equal(700, stats.Week);
        Assert.Equal(400, stats.Day);
        Assert.Equal(1000, stats.Servers["news.example.com"].Total);
        Assert.Equal(400, stats.Servers["news.example.com"].Day);
    }

    [Fact]
    public async Task BuildAsync_FoldsLifetimeTotalsIntoServerAndRootTotals()
    {
        await using var harness = await MetricsHarness.CreateAsync();
        var provider = MakeProvider("news.example.com", "alice");
        var key = UsenetProviderIdentity.MetricsKey(provider);
        var config = ConfigWithProviders(provider);

        harness.Context.ProviderHourly.Add(Hour(key, DayBoundary, bytes: 250));
        harness.Context.ProviderLifetimeTotals.Add(new ProviderLifetimeTotal
        {
            Provider = key,
            BytesFetched = 750,
            Articles = 5,
        });
        await harness.Context.SaveChangesAsync();

        var stats = await GetServerStatsController.BuildAsync(harness.Context, config, NowMs, Utc);

        Assert.Equal(1000, stats.Total);
        Assert.Equal(1000, stats.Servers["news.example.com"].Total);
    }

    [Fact]
    public async Task BuildAsync_GroupsDailyBytesAndArticlesByLocalDayLabel()
    {
        await using var harness = await MetricsHarness.CreateAsync();
        var provider = MakeProvider("news.example.com", "alice");
        var key = UsenetProviderIdentity.MetricsKey(provider);
        var config = ConfigWithProviders(provider);
        var aug9 = new DateTimeOffset(2026, 8, 9, 22, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var aug10 = new DateTimeOffset(2026, 8, 10, 10, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

        harness.Context.ProviderHourly.AddRange(
            Hour(key, aug9, bytes: 111, articles: 10, misses: 1, errors: 1),
            Hour(key, aug10, bytes: 222, articles: 4, misses: 10, errors: 10));
        await harness.Context.SaveChangesAsync();

        var stats = await GetServerStatsController.BuildAsync(harness.Context, config, NowMs, Utc);
        var server = stats.Servers["news.example.com"];

        Assert.Equal(111, server.Daily["2026-08-09"]);
        Assert.Equal(222, server.Daily["2026-08-10"]);
        Assert.Equal(10, server.ArticlesTried["2026-08-09"]);
        Assert.Equal(8, server.ArticlesSuccess["2026-08-09"]);
        Assert.Equal(4, server.ArticlesTried["2026-08-10"]);
        Assert.Equal(0, server.ArticlesSuccess["2026-08-10"]);
    }

    [Fact]
    public async Task BuildAsync_UsesMetricsKeyWhenDisplayLabelsCollide()
    {
        await using var harness = await MetricsHarness.CreateAsync();
        var first = MakeProvider("news.example.com", "alice");
        var second = MakeProvider("news.example.com", "bob");
        var firstKey = UsenetProviderIdentity.MetricsKey(first);
        var secondKey = UsenetProviderIdentity.MetricsKey(second);
        var config = ConfigWithProviders(first, second);

        harness.Context.ProviderHourly.AddRange(
            Hour(firstKey, DayBoundary, bytes: 100),
            Hour(secondKey, DayBoundary, bytes: 200));
        await harness.Context.SaveChangesAsync();

        var stats = await GetServerStatsController.BuildAsync(harness.Context, config, NowMs, Utc);

        Assert.True(stats.Servers.ContainsKey("news.example.com"));
        Assert.True(stats.Servers.ContainsKey(secondKey));
        Assert.Equal(100, stats.Servers["news.example.com"].Total);
        Assert.Equal(200, stats.Servers[secondKey].Total);
    }

    [Fact]
    public async Task BuildAsync_IncludesUnconfiguredProvidersInRootTotalsOnly()
    {
        await using var harness = await MetricsHarness.CreateAsync();
        var provider = MakeProvider("news.example.com", "alice");
        var key = UsenetProviderIdentity.MetricsKey(provider);
        var config = ConfigWithProviders(provider);

        harness.Context.ProviderHourly.AddRange(
            Hour(key, DayBoundary, bytes: 100),
            Hour("deleted-provider", DayBoundary, bytes: 50));
        await harness.Context.SaveChangesAsync();

        var stats = await GetServerStatsController.BuildAsync(harness.Context, config, NowMs, Utc);

        Assert.Equal(150, stats.Total);
        Assert.Equal(150, stats.Day);
        Assert.Single(stats.Servers);
        Assert.Equal(100, stats.Servers["news.example.com"].Total);
    }

    [Fact]
    public async Task BuildAsync_IncludesConfiguredProvidersWithZeroTraffic()
    {
        await using var harness = await MetricsHarness.CreateAsync();
        var provider = MakeProvider("news.example.com", "alice", nickname: "Primary");
        var config = ConfigWithProviders(provider);

        var stats = await GetServerStatsController.BuildAsync(harness.Context, config, NowMs, Utc);

        Assert.Single(stats.Servers);
        Assert.True(stats.Servers.ContainsKey("Primary"));
        Assert.Equal(0, stats.Servers["Primary"].Total);
        Assert.Equal(0, stats.Total);
    }

    [Fact]
    public async Task BuildAsync_IgnoresHourlyRowsAfterNow()
    {
        await using var harness = await MetricsHarness.CreateAsync();
        var provider = MakeProvider("news.example.com", "alice");
        var key = UsenetProviderIdentity.MetricsKey(provider);
        var config = ConfigWithProviders(provider);

        harness.Context.ProviderHourly.AddRange(
            Hour(key, DayBoundary, bytes: 100),
            Hour(key, NowMs + 3_600_000, bytes: 999));
        await harness.Context.SaveChangesAsync();

        var stats = await GetServerStatsController.BuildAsync(harness.Context, config, NowMs, Utc);

        Assert.Equal(100, stats.Total);
    }

    [Fact]
    public async Task BuildAsync_EmptyDatabaseReturnsZeros()
    {
        await using var harness = await MetricsHarness.CreateAsync();
        var config = ConfigWithProviders(MakeProvider("news.example.com", "alice"));

        var stats = await GetServerStatsController.BuildAsync(harness.Context, config, NowMs, Utc);

        Assert.Equal(0, stats.Total);
        Assert.Equal(0, stats.Month);
        Assert.Equal(0, stats.Week);
        Assert.Equal(0, stats.Day);
        Assert.Single(stats.Servers);
        Assert.Equal(0, stats.Servers["news.example.com"].Total);
    }

    [Fact]
    public void Response_SerializesExactSnakeCaseKeys()
    {
        var response = new GetServerStatsResponse
        {
            Total = 1,
            Month = 2,
            Week = 3,
            Day = 4,
            Servers =
            {
                ["server-a"] = new GetServerStatsResponse.ServerStats
                {
                    Total = 5,
                    Month = 6,
                    Week = 7,
                    Day = 8,
                    Daily = { ["2026-08-10"] = 9 },
                    ArticlesTried = { ["2026-08-10"] = 10 },
                    ArticlesSuccess = { ["2026-08-10"] = 11 },
                },
            },
        };

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(response));
        var root = json.RootElement;

        Assert.True(root.TryGetProperty("total", out _));
        Assert.True(root.TryGetProperty("month", out _));
        Assert.True(root.TryGetProperty("week", out _));
        Assert.True(root.TryGetProperty("day", out _));
        Assert.True(root.TryGetProperty("servers", out var servers));
        var server = servers.GetProperty("server-a");
        Assert.True(server.TryGetProperty("daily", out _));
        Assert.True(server.TryGetProperty("articles_tried", out _));
        Assert.True(server.TryGetProperty("articles_success", out _));
    }

    private static ProviderHourly Hour(
        string provider,
        long hour,
        long bytes,
        long articles = 0,
        long misses = 0,
        long errors = 0) =>
        new()
        {
            Hour = hour,
            Provider = provider,
            BytesFetched = bytes,
            Articles = articles,
            Misses = misses,
            Errors = errors,
        };

    private static ConfigManager ConfigWithProviders(params UsenetProviderConfig.ConnectionDetails[] providers)
    {
        var config = new ConfigManager();
        config.UpdateValues(
        [
            new ConfigItem
            {
                ConfigName = ConfigKeys.UsenetProviders,
                ConfigValue = JsonSerializer.Serialize(new UsenetProviderConfig
                {
                    Providers = providers.ToList(),
                }),
            },
        ]);
        return config;
    }

    private static UsenetProviderConfig.ConnectionDetails MakeProvider(
        string host,
        string user,
        string? nickname = null,
        Guid? providerId = null) =>
        new()
        {
            ProviderId = providerId ?? Guid.NewGuid(),
            Type = ProviderType.Pooled,
            Host = host,
            Port = 563,
            UseSsl = true,
            User = user,
            Pass = "pass",
            MaxConnections = 10,
            Nickname = nickname,
        };

    private sealed class MetricsHarness : IAsyncDisposable
    {
        private readonly string _dir;

        private MetricsHarness(string dir, MetricsDbContext context)
        {
            _dir = dir;
            Context = context;
        }

        public MetricsDbContext Context { get; }

        public static async Task<MetricsHarness> CreateAsync()
        {
            var dir = Path.Join(Path.GetTempPath(), $"nzbdav-sab-server-stats-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            var path = Path.Join(dir, "metrics.sqlite");
            var options = new DbContextOptionsBuilder<MetricsDbContext>()
                .UseSqlite($"Data Source={path}")
                .AddInterceptors(new SqliteMetricsPragmas())
                .ReplaceService<
                    IMigrationsSqlGenerator,
                    SqliteMigrationsSqlGenerator<SqliteMigrationsSqlGenerator>>()
                .Options;
            var context = new MetricsDbContext(options);
            await context.Database.MigrateAsync();
            return new MetricsHarness(dir, context);
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            try
            {
                Directory.Delete(_dir, recursive: true);
            }
            catch (IOException)
            {
                // best effort
            }
        }
    }
}
