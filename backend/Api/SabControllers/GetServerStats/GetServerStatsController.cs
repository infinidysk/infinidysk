using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models.Metrics;
using NzbWebDAV.Services.Metrics;

namespace NzbWebDAV.Api.SabControllers.GetServerStats;

public class GetServerStatsController(
    HttpContext httpContext,
    ConfigManager configManager
) : SabApiController.BaseController(httpContext, configManager)
{
    protected override async Task<IActionResult> Handle()
    {
        await using var metrics = new MetricsDbContext();
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var response = await BuildAsync(metrics, Config, nowMs, TimeZoneInfo.Local).ConfigureAwait(false);
        return Ok(response);
    }

    internal static async Task<GetServerStatsResponse> BuildAsync(
        MetricsDbContext metrics,
        ConfigManager configManager,
        long nowMs,
        TimeZoneInfo timeZone)
    {
        var dayBoundary = LocalBoundaryUnixMs(nowMs, timeZone, BoundaryKind.Day);
        var weekBoundary = LocalBoundaryUnixMs(nowMs, timeZone, BoundaryKind.Week);
        var monthBoundary = LocalBoundaryUnixMs(nowMs, timeZone, BoundaryKind.Month);

        var hourlyRows = await metrics.ProviderHourly
            .Where(h => h.Hour <= nowMs)
            .Select(h => new HourlyRow(
                h.Hour,
                h.Provider,
                h.Articles,
                h.BytesFetched,
                h.Misses,
                h.Errors))
            .ToListAsync()
            .ConfigureAwait(false);

        var lifetimeRows = await metrics.ProviderLifetimeTotals
            .Select(l => new LifetimeRow(
                l.Provider,
                l.Articles,
                l.BytesFetched,
                l.Misses,
                l.Errors))
            .ToListAsync()
            .ConfigureAwait(false);

        var byMetricsKey = new Dictionary<string, ProviderAgg>(StringComparer.Ordinal);
        foreach (var row in hourlyRows)
        {
            if (!byMetricsKey.TryGetValue(row.Provider, out var agg))
            {
                agg = new ProviderAgg();
                byMetricsKey[row.Provider] = agg;
            }

            AddHourlyRow(
                agg,
                row,
                dayBoundary,
                weekBoundary,
                monthBoundary,
                FormatDayLabel(row.Hour, timeZone));
        }

        foreach (var row in lifetimeRows)
        {
            if (!byMetricsKey.TryGetValue(row.Provider, out var agg))
            {
                agg = new ProviderAgg();
                byMetricsKey[row.Provider] = agg;
            }

            agg.TotalBytes += row.BytesFetched;
        }

        var labelsByMetricsKey = ProviderUsageHelper
            .BuildLabelsByMetricsKey(configManager.GetUsenetProviderConfig().Providers);
        var usedServerKeys = new Dictionary<string, string>(StringComparer.Ordinal);
        var servers = new Dictionary<string, GetServerStatsResponse.ServerStats>(StringComparer.Ordinal);

        foreach (var provider in configManager.GetUsenetProviderConfig().Providers
                     .Where(p => p.ProviderId != Guid.Empty))
        {
            var metricsKey = UsenetProviderIdentity.MetricsKey(provider);
            var label = labelsByMetricsKey.GetValueOrDefault(metricsKey);
            var serverKey = ResolveServerKey(metricsKey, label, usedServerKeys);
            byMetricsKey.TryGetValue(metricsKey, out var agg);
            servers[serverKey] = ToServerStats(agg);
        }

        long total = 0, month = 0, week = 0, day = 0;
        foreach (var agg in byMetricsKey.Values)
        {
            total += agg.TotalBytes;
            month += agg.MonthBytes;
            week += agg.WeekBytes;
            day += agg.DayBytes;
        }

        return new GetServerStatsResponse
        {
            Total = total,
            Month = month,
            Week = week,
            Day = day,
            Servers = servers,
        };
    }

    private static void AddHourlyRow(
        ProviderAgg agg,
        HourlyRow row,
        long dayBoundary,
        long weekBoundary,
        long monthBoundary,
        string dayLabel)
    {
        if (row.Hour >= dayBoundary)
            agg.DayBytes += row.BytesFetched;
        if (row.Hour >= weekBoundary)
            agg.WeekBytes += row.BytesFetched;
        if (row.Hour >= monthBoundary)
            agg.MonthBytes += row.BytesFetched;
        agg.TotalBytes += row.BytesFetched;

        AddToMap(agg.Daily, dayLabel, row.BytesFetched);
        AddToMap(agg.ArticlesTried, dayLabel, row.Articles);
        AddToMap(agg.ArticlesSuccess, dayLabel, Math.Max(0, row.Articles - row.Misses - row.Errors));
    }

    private static GetServerStatsResponse.ServerStats ToServerStats(ProviderAgg? agg) =>
        agg is null
            ? new GetServerStatsResponse.ServerStats()
            : new GetServerStatsResponse.ServerStats
            {
                Total = agg.TotalBytes,
                Month = agg.MonthBytes,
                Week = agg.WeekBytes,
                Day = agg.DayBytes,
                Daily = new Dictionary<string, long>(agg.Daily),
                ArticlesTried = new Dictionary<string, long>(agg.ArticlesTried),
                ArticlesSuccess = new Dictionary<string, long>(agg.ArticlesSuccess),
            };

    internal static string ResolveServerKey(
        string metricsKey,
        string? label,
        IDictionary<string, string> usedServerKeys)
    {
        var candidate = string.IsNullOrEmpty(label) ? metricsKey : label;
        if (!usedServerKeys.TryGetValue(candidate, out var existing))
        {
            usedServerKeys[candidate] = metricsKey;
            return candidate;
        }

        if (string.Equals(existing, metricsKey, StringComparison.Ordinal))
            return candidate;

        usedServerKeys[metricsKey] = metricsKey;
        return metricsKey;
    }

    private static void AddToMap(Dictionary<string, long> map, string key, long amount)
    {
        map.TryGetValue(key, out var current);
        map[key] = current + amount;
    }

    private static string FormatDayLabel(long hourMs, TimeZoneInfo timeZone)
    {
        var local = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(hourMs), timeZone);
        return local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    private enum BoundaryKind
    {
        Day,
        Week,
        Month,
    }

    private static long LocalBoundaryUnixMs(long nowMs, TimeZoneInfo timeZone, BoundaryKind kind)
    {
        var local = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(nowMs), timeZone);
        return kind switch
        {
            BoundaryKind.Day => new DateTimeOffset(local.Year, local.Month, local.Day, 0, 0, 0, local.Offset)
                .ToUnixTimeMilliseconds(),
            BoundaryKind.Week => WeekBoundaryUnixMs(local),
            BoundaryKind.Month => new DateTimeOffset(local.Year, local.Month, 1, 0, 0, 0, local.Offset)
                .ToUnixTimeMilliseconds(),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };
    }

    private static long WeekBoundaryUnixMs(DateTimeOffset local)
    {
        var daysSinceMonday = ((int)local.DayOfWeek + 6) % 7;
        return new DateTimeOffset(local.Year, local.Month, local.Day, 0, 0, 0, local.Offset)
            .AddDays(-daysSinceMonday)
            .ToUnixTimeMilliseconds();
    }

    private sealed class ProviderAgg
    {
        public long TotalBytes;
        public long MonthBytes;
        public long WeekBytes;
        public long DayBytes;
        public Dictionary<string, long> Daily { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, long> ArticlesTried { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, long> ArticlesSuccess { get; } = new(StringComparer.Ordinal);
    }

    private readonly record struct HourlyRow(
        long Hour,
        string Provider,
        long Articles,
        long BytesFetched,
        long Misses,
        long Errors);

    private readonly record struct LifetimeRow(
        string Provider,
        long Articles,
        long BytesFetched,
        long Misses,
        long Errors);
}
