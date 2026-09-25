using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models.Metrics;
using NzbWebDAV.Services.Metrics;

namespace NzbWebDAV.Api.Controllers.GetOverviewStats;

internal static class ProviderSampledRates
{
    internal static async Task LoadAndApplyAsync(
        MetricsDbContext db,
        ProviderBytesTracker tracker,
        List<GetOverviewStatsResponse.ProviderRow> providers,
        IReadOnlyDictionary<string, string?> labels,
        GetOverviewStatsRequest.OverviewWindow window,
        long windowStart,
        long nowMs,
        bool useRollups)
    {
        var geometry = GetOverviewStatsController.ResolveProviderSeriesGeometry(window, windowStart, nowMs);
        var sampleStart = GetSampleStart(window, windowStart, geometry.Start);
        await tracker.PeakPersistenceGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await db.Database.OpenConnectionAsync().ConfigureAwait(false);
            var connection = (Microsoft.Data.Sqlite.SqliteConnection)db.Database.GetDbConnection();
#pragma warning disable CA1849
            await using var transaction = connection.BeginTransaction(deferred: true);
#pragma warning restore CA1849
            await db.Database.UseTransactionAsync(transaction).ConfigureAwait(false);
            try
            {
                var samples = useRollups
                    ? await db.ProviderHourly.AsNoTracking()
                        .Where(row => row.Hour >= sampleStart && row.PeakBytesPerSec != null)
                        .Select(row => new ProviderBytesTracker.ProviderRateSample(row.Hour, row.Provider,
                            row.PeakBytesPerSec!.Value, row.ActiveBytes ?? 0, row.ActiveSeconds ?? 0))
                        .ToListAsync().ConfigureAwait(false)
                    : await db.ProviderMinutes.AsNoTracking()
                        .Where(row => row.Minute >= sampleStart && row.PeakBytesPerSec != null)
                        .Select(row => new ProviderBytesTracker.ProviderRateSample(row.Minute, row.Provider,
                            row.PeakBytesPerSec!.Value, row.ActiveBytes ?? 0, row.ActiveSeconds ?? 0))
                        .ToListAsync().ConfigureAwait(false);
                var lifetime = window == GetOverviewStatsRequest.OverviewWindow.AllTime
                    ? await db.ProviderLifetimeTotals.AsNoTracking().ToListAsync().ConfigureAwait(false)
                    : [];
                samples.AddRange(tracker.PendingProviderRates(sampleStart));
                Apply(providers, labels, samples, lifetime, window, windowStart, nowMs);
            }
            finally
            {
                await db.Database.UseTransactionAsync(null).ConfigureAwait(false);
            }
        }
        finally
        {
            tracker.PeakPersistenceGate.Release();
        }
    }

    internal static void Apply(
        List<GetOverviewStatsResponse.ProviderRow> providers,
        IReadOnlyDictionary<string, string?> labels,
        IEnumerable<ProviderBytesTracker.ProviderRateSample> samples,
        IReadOnlyList<ProviderLifetimeTotal> lifetime,
        GetOverviewStatsRequest.OverviewWindow window,
        long windowStart,
        long nowMs)
    {
        var geometry = GetOverviewStatsController.ResolveProviderSeriesGeometry(window, windowStart, nowMs);
        var sampleStart = GetSampleStart(window, windowStart, geometry.Start);
        var byProvider = samples.Where(sample => sample.Minute >= sampleStart).ToLookup(sample => sample.Provider);
        foreach (var provider in byProvider.Select(group => group.Key)
                     .Concat(lifetime.Select(total => total.Provider)).Distinct()
                     .Where(provider => labels.ContainsKey(provider)
                         && providers.All(row => row.Provider != provider)))
        {
            providers.Add(new GetOverviewStatsResponse.ProviderRow { Provider = provider, Nickname = labels[provider] });
        }

        foreach (var provider in providers)
        {
            var recorded = byProvider[provider.Provider].ToList();
            var totals = recorded.ToList();
            if (window == GetOverviewStatsRequest.OverviewWindow.AllTime)
                totals.AddRange(lifetime.Where(total => total.Provider == provider.Provider && total.PeakBytesPerSec != null)
                    .Select(total => new ProviderBytesTracker.ProviderRateSample(0, total.Provider,
                        total.PeakBytesPerSec!.Value, total.ActiveBytes ?? 0, total.ActiveSeconds ?? 0)));
            var summary = Aggregate(0, totals);
            provider.PeakMbPerSec = summary.PeakMbPerSec;
            provider.ActiveAverageMbPerSec = summary.ActiveAverageMbPerSec;

            var buckets = recorded.Where(sample => sample.Minute >= geometry.Start && sample.Minute < geometry.End)
                .ToLookup(sample => sample.Minute - sample.Minute % geometry.BucketSize);
            provider.SampledSpeedSeries = [];
            for (var bucket = geometry.Start; bucket < geometry.End; bucket += geometry.BucketSize)
                provider.SampledSpeedSeries.Add(Aggregate(bucket, buckets[bucket]));
            provider.PeakSpeedSpark = provider.SampledSpeedSeries.Select(point => point.PeakMbPerSec).ToList();
        }
    }

    private static long GetSampleStart(
        GetOverviewStatsRequest.OverviewWindow window, long windowStart, long alignedStart) =>
        window == GetOverviewStatsRequest.OverviewWindow.AllTime ? windowStart : alignedStart;

    private static GetOverviewStatsResponse.ProviderSampledSpeedPoint Aggregate(
        long bucket, IEnumerable<ProviderBytesTracker.ProviderRateSample> samples)
    {
        long peak = 0, bytes = 0;
        double seconds = 0;
        foreach (var sample in samples)
        {
            peak = Math.Max(peak, sample.PeakBytesPerSec);
            bytes += sample.ActiveBytes;
            seconds += sample.ActiveSeconds;
        }
        return new GetOverviewStatsResponse.ProviderSampledSpeedPoint
        {
            Bucket = bucket,
            PeakMbPerSec = seconds > 0 ? peak / 1_000_000d : null,
            ActiveAverageMbPerSec = seconds > 0 ? bytes / seconds / 1_000_000d : null,
        };
    }
}
