using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models.Metrics;
using Serilog;

namespace NzbWebDAV.Services.Metrics;

/// <summary>
/// Glue between the persistent metrics rollups and the in-memory byte tracker
/// for the per-provider data cap. Two responsibilities:
///   - hydrate <see cref="ProviderBytesTracker"/> from durable quota state so the
///     hot-path limit check has an accurate "bytes since reset" number after restart,
///   - expose the same computation directly for read-only API consumers that
///     don't want to round-trip through the tracker.
///
/// The reset semantics are intentional: ResetAt is a unix-ms cutoff applied at
/// query time, not a DESTRUCTIVE delete of older rows. Historical graphs stay
/// intact across a reset; only the "bytes consumed against this block" gauge
/// rewinds. Offset is added on top so a user migrating from another client can
/// pre-seed "I've already burned 300 GB on this account" without faking events
/// into the metrics tables.
///
/// Metrics keys are stable per-account <c>ProviderId</c> strings, not NNTP hosts.
/// </summary>
public static class ProviderUsageHelper
{
    /// <summary>
    /// Fraction of the configured cap at which the provider is taken out of
    /// rotation. The headroom absorbs in-flight fetches that already passed
    /// the per-call check at <see cref="MultiProviderNntpClient"/> startup
    /// but haven't finished streaming bytes through CountingYencStream yet.
    /// 0.95 means "stop at 95% so the remaining 5% covers parallel fetches"
    /// — well above the worst realistic overshoot (MaxConnections × ~1 MB),
    /// and tiny compared to a typical multi-hundred-GB block.
    /// </summary>
    public const double EffectiveLimitFraction = 0.95;

    /// <summary>
    /// <summary>
    /// Raw ProviderHourly rows over the last 7 days for the supplied provider keys,
    /// grouped by key. Returning rows rather than a pre-aggregated sum lets
    /// the caller apply each provider's own ResetAt cutoff in memory without
    /// firing N queries — the settings page polls every 10s.
    /// </summary>
    public static async Task<Dictionary<string, List<(long Hour, long Bytes)>>> ReadRecentHoursAsync(
        IEnumerable<string> providerKeys)
    {
        var distinct = providerKeys.Where(k => !string.IsNullOrEmpty(k)).Distinct().ToArray();
        if (distinct.Length == 0) return new Dictionary<string, List<(long, long)>>();

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        const long sevenDaysMs = 7L * 24 * 60 * 60 * 1000;
        var since = nowMs - sevenDaysMs;

        await using var db = new MetricsDbContext();
        var rows = await db.ProviderHourly
            .Where(x => distinct.Contains(x.Provider) && x.Hour >= since)
            .Select(x => new { x.Provider, x.Hour, x.BytesFetched })
            .ToListAsync()
            .ConfigureAwait(false);

        return rows
            .GroupBy(r => r.Provider, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g.Select(r => (r.Hour, r.BytesFetched)).ToList(),
                StringComparer.Ordinal);
    }

    /// <summary>
    /// Per-provider burn rate (bytes/day) and projected days-until-cap,
    /// computed against the same time window as the live usage gauge.
    ///
    /// Window = [max(ResetAt, now − 7d), now]. This is the fix for the
    /// "0 used / 1h left" paradox: never fold pre-reset history into a
    /// post-reset projection. A freshly-reset counter shouldn't display
    /// a runout inherited from last week's downloads.
    ///
    /// Returns (rate, null) — honest rate, no projection — when any of:
    ///   - no cap configured,
    ///   - nothing used since reset (no signal to project from),
    ///   - window shorter than 1h (a few minutes of data inflates the rate
    ///     into nonsense like "1h left" after a tiny burst),
    ///   - already at/over the cap (nothing to extrapolate).
    /// </summary>
    public static (long BytesPerDay, double? DaysRemaining) ComputeBurnRate(
        UsenetProviderConfig.ConnectionDetails provider,
        long bytesUsed,
        List<(long Hour, long Bytes)>? recentHours)
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        const long sevenDaysMs = 7L * 24 * 60 * 60 * 1000;
        const long oneHourMs = 60L * 60 * 1000;
        const double msPerDay = 86_400_000d;

        var effectiveStart = Math.Max(provider.BytesUsedResetAt, nowMs - sevenDaysMs);
        var windowMs = nowMs - effectiveStart;

        long bytesInWindow = 0;
        if (recentHours != null)
        {
            foreach (var (hour, bytes) in recentHours)
                if (hour >= effectiveStart) bytesInWindow += bytes;
        }

        var bytesPerDay = windowMs > 0
            ? (long)(bytesInWindow / (windowMs / msPerDay))
            : 0;

        var limit = provider.ByteLimit;
        if (!limit.HasValue || limit.Value <= 0) return (bytesPerDay, null);
        if (bytesUsed <= 0) return (bytesPerDay, null);
        if (windowMs < oneHourMs) return (bytesPerDay, null);
        if (bytesPerDay <= 0) return (bytesPerDay, null);
        var remaining = limit.Value - bytesUsed;
        if (remaining <= 0) return (bytesPerDay, null);
        return (bytesPerDay, (double)remaining / bytesPerDay);
    }

    /// <summary>
    public static async Task HydrateQuotaAsync(
        ProviderBytesTracker tracker,
        UsenetProviderConfig config,
        Func<MetricsDbContext> dbFactory,
        long nowMs,
        CancellationToken ct)
    {
        await using var db = dbFactory();
        var rows = await db.ProviderQuotaUsage
            .ToDictionaryAsync(x => x.Provider, ct)
            .ConfigureAwait(false);

        foreach (var provider in config.Providers.Where(p => p.ProviderId != Guid.Empty))
        {
            var key = UsenetProviderIdentity.MetricsKey(provider);
            long bytesUsed;
            if (rows.TryGetValue(key, out var row) && row is not null &&
                row.ResetAt == provider.BytesUsedResetAt)
            {
                bytesUsed = row.BytesUsed;
            }
            else if (row is not null)
            {
                bytesUsed = 0;
                row.BytesUsed = 0;
                row.ResetAt = provider.BytesUsedResetAt;
                row.UpdatedAt = nowMs;
            }
            else
            {
                bytesUsed = await db.ProviderHourly
                    .Where(x => x.Provider == key && x.Hour >= provider.BytesUsedResetAt)
                    .SumAsync(x => x.BytesFetched, ct)
                    .ConfigureAwait(false);
                db.ProviderQuotaUsage.Add(new ProviderQuotaUsage
                {
                    Provider = key,
                    BytesUsed = bytesUsed,
                    ResetAt = provider.BytesUsedResetAt,
                    UpdatedAt = nowMs,
                });
            }

            tracker.InitializeQuota(key, provider.BytesUsedResetAt, bytesUsed);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public static Task PersistQuotaSnapshotAsync(
        MetricsDbContext db,
        ProviderBytesTracker.ProviderQuotaSnapshot snapshot,
        long updatedAt,
        CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO ProviderQuotaUsage (Provider, BytesUsed, ResetAt, UpdatedAt)
            VALUES ({snapshot.Provider}, {snapshot.BytesUsed}, {snapshot.ResetAt}, {updatedAt})
            ON CONFLICT(Provider) DO UPDATE SET
                BytesUsed = excluded.BytesUsed,
                ResetAt = excluded.ResetAt,
                UpdatedAt = excluded.UpdatedAt
            WHERE excluded.ResetAt > ProviderQuotaUsage.ResetAt
               OR (excluded.ResetAt = ProviderQuotaUsage.ResetAt
                   AND excluded.BytesUsed >= ProviderQuotaUsage.BytesUsed);
            """,
            ct);

    /// <summary>
    /// Total user-facing usage = bytes since reset (live in tracker) + offset.
    /// Clamped to 0 so a negative offset (manual correction) never reports as
    /// a negative gauge value.
    /// </summary>
    public static long ComputeUsage(ProviderBytesTracker tracker, UsenetProviderConfig.ConnectionDetails provider)
    {
        if (provider.ProviderId == Guid.Empty) return Math.Max(0, provider.BytesUsedOffset);
        var live = tracker.GetQuotaBytes(UsenetProviderIdentity.MetricsKey(provider));
        return Math.Max(0, live + provider.BytesUsedOffset);
    }

    /// <summary>
    /// True when a configured ByteLimit exists and the live counter has caught
    /// up to or passed the effective cutoff (configured limit × safety margin).
    /// A ByteLimit of null or 0 means "no cap".
    /// </summary>
    public static bool IsOverLimit(ProviderBytesTracker tracker, UsenetProviderConfig.ConnectionDetails provider)
    {
        var limit = provider.ByteLimit;
        if (!limit.HasValue || limit.Value <= 0) return false;
        var effective = (long)(limit.Value * EffectiveLimitFraction);
        return ComputeUsage(tracker, provider) >= effective;
    }

    /// <summary>
    /// Builds a metrics-key → display-label map (nickname, else host).
    /// Used by overview/queue/history/live-reads so Guids never render.
    /// </summary>
    public static IReadOnlyDictionary<string, string?> BuildLabelsByMetricsKey(
        IEnumerable<UsenetProviderConfig.ConnectionDetails> providers)
    {
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var group in providers
                     .Where(p => p.ProviderId != Guid.Empty)
                     .GroupBy(p => UsenetProviderIdentity.MetricsKey(p), StringComparer.Ordinal))
        {
            var p = group.First();
            var nick = p.Nickname?.Trim();
            result[group.Key] = string.IsNullOrEmpty(nick) ? p.Host : nick;
        }
        return result;
    }

    /// <summary>
    /// Builds a metrics-key → (Host, Nickname) map for API payloads that still
    /// expose a host field for display (queue/history/live-reads).
    /// </summary>
    public static IReadOnlyDictionary<string, (string Host, string? Nickname)> BuildDisplayByMetricsKey(
        IEnumerable<UsenetProviderConfig.ConnectionDetails> providers)
    {
        return providers
            .Where(p => p.ProviderId != Guid.Empty)
            .GroupBy(p => UsenetProviderIdentity.MetricsKey(p), StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g =>
                {
                    var p = g.First();
                    var nick = p.Nickname?.Trim();
                    return (p.Host, string.IsNullOrEmpty(nick) ? null : nick);
                },
                StringComparer.Ordinal);
    }
}
