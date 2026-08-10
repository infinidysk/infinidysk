using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;
using NzbWebDAV.Websocket;
using Serilog;

namespace NzbWebDAV.Services;

public class VariantResolver(ConfigManager configManager)
{
    public static string? BuildContentGroupKey(string? type, string? id)
    {
        if (string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(id)) return null;
        return $"{type}:{id}";
    }

    public static string? BuildContentGroupKey(NzbResolutionCache.Entry entry)
        => BuildContentGroupKey(entry.Type, entry.Id);

    public bool IsEnabled => configManager.GetVariantsMode() != "off";

    public async Task<VariantDecision> ResolveAsync(
        DavDatabaseContext ctx,
        NzbResolutionCache.Entry entry,
        CancellationToken ct)
    {
        var mode = configManager.GetVariantsMode();
        if (mode == "off") return VariantDecision.NoOp;

        var groupKey = BuildContentGroupKey(entry);
        if (groupKey is null) return VariantDecision.NoOp;

        var variants = await LoadVariantsAsync(ctx, groupKey, ct).ConfigureAwait(false);
        if (variants.Count == 0) return new VariantDecision(null, false, groupKey);

        var clickSize = entry.Primary.Size;
        var match = mode switch
        {
            "smart" => SelectWithinTolerance(variants, clickSize, configManager.GetVariantsTolerancePct()),
            "collect-all" => SelectWithinTolerance(variants, clickSize, exactMatchPct: 5),
            _ => null,
        };
        return new VariantDecision(match, true, groupKey);
    }

    public async Task<VariantMatch?> TryFallbackAfterFailureAsync(
        DavDatabaseContext ctx,
        NzbResolutionCache.Entry entry,
        CancellationToken ct)
    {
        if (!configManager.IsVariantsFallbackOnFailureEnabled()) return null;

        var groupKey = BuildContentGroupKey(entry);
        if (groupKey is null) return null;

        var variants = await LoadVariantsAsync(ctx, groupKey, ct).ConfigureAwait(false);
        if (variants.Count == 0) return null;

        return SelectClosest(variants, entry.Primary.Size);
    }

    public async Task<Guid?> FindInFlightAsync(
        DavDatabaseContext ctx,
        NzbResolutionCache.Entry entry,
        CancellationToken ct)
    {
        var groupKey = BuildContentGroupKey(entry);
        if (groupKey is null) return null;

        return await ctx.QueueItems.AsNoTracking()
            .Where(q => q.ContentGroupKey == groupKey)
            .OrderByDescending(q => q.CreatedAt)
            .Select(q => (Guid?)q.Id)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Guid>> EnforceCapAsync(
        DavDatabaseClient dbClient,
        WebsocketManager? websocketManager,
        string contentGroupKey,
        CancellationToken ct)
    {
        var cap = configManager.GetVariantsMaxPerGroup();
        if (cap <= 0) return Array.Empty<Guid>();

        var strategy = configManager.GetVariantsEvictionStrategy();
        if (strategy == "never") return Array.Empty<Guid>();

        var variants = await LoadVariantsAsync(dbClient.Ctx, contentGroupKey, ct).ConfigureAwait(false);
        if (variants.Count <= cap) return Array.Empty<Guid>();

        var graceCutoff = DateTimeOffset.UtcNow
            - TimeSpan.FromSeconds(configManager.GetVariantsEvictionActiveGraceSeconds());

        bool IsProtected(VariantRow v) => v.LastPlayedAt is { } t && t >= graceCutoff;

        var ranked = strategy switch
        {
            "largest-first" => variants.OrderByDescending(v => v.LargestFileSize ?? 0).ToList(),
            "smallest-first" => variants.OrderBy(v => v.LargestFileSize ?? 0).ToList(),
            _ => variants.OrderBy(v => v.LastPlayedAt ?? DateTimeOffset.MinValue).ToList(),
        };

        var surplus = ranked.Count - cap;
        var toRemove = new List<Guid>(surplus);
        foreach (var v in ranked)
        {
            if (toRemove.Count >= surplus) break;
            if (IsProtected(v)) continue;
            toRemove.Add(v.HistoryItemId);
        }

        if (toRemove.Count == 0) return Array.Empty<Guid>();

        try
        {
            await dbClient.RemoveHistoryItemsAsync(toRemove, deleteFiles: true, ct).ConfigureAwait(false);
            await dbClient.Ctx.SaveChangesAsync(ct).ConfigureAwait(false);
            if (websocketManager is not null)
                _ = websocketManager.SendMessage(
                    WebsocketTopic.HistoryItemRemoved, string.Join(",", toRemove));
        }
#pragma warning disable CA2016 // CA2016: classify cancellation regardless of the ambient token -- forwarding it would misclassify cancellations from internal timeout/child tokens
        catch (Exception e) when (!e.IsCancellationException() && e is not OutOfMemoryException)
#pragma warning restore CA2016
        {
            Log.Warning(e, "Variants: failed to evict surplus variants for group {Group}", contentGroupKey);
            return Array.Empty<Guid>();
        }

        return toRemove;
    }

    public async Task MarkPlayedAsync(Guid historyItemId, CancellationToken ct)
    {
        try
        {
            await using var ctx = new DavDatabaseContext();
            var now = DateTimeOffset.UtcNow;
            var item = await ctx.HistoryItems
                .FirstOrDefaultAsync(h => h.Id == historyItemId, ct).ConfigureAwait(false);
            if (item is null) return;
            item.LastPlayedAt = now;
            await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
        }
#pragma warning disable CA2016 // CA2016: classify cancellation regardless of the ambient token -- forwarding it would misclassify cancellations from internal timeout/child tokens
        catch (Exception e) when (!e.IsCancellationException() && e is not OutOfMemoryException)
#pragma warning restore CA2016
        {
            Log.Debug(e, "Variants: failed to update LastPlayedAt for {Id}", historyItemId);
        }
    }

    public string ReplayStrategy => configManager.GetVariantsReplayStrategy();

    public VariantRow? PickReplay(IReadOnlyList<VariantRow> variants, long clickSize)
    {
        if (variants.Count == 0) return null;
        return ReplayStrategy switch
        {
            "largest" => variants.OrderByDescending(v => v.LargestFileSize ?? 0).First(),
            "smallest" => variants.OrderBy(v => v.LargestFileSize ?? long.MaxValue).First(),
            _ => SelectClosest(variants, clickSize)?.Row,
        };
    }

    private static VariantMatch? SelectWithinTolerance(
        IReadOnlyList<VariantRow> variants,
        long clickSize,
        int? tolerancePct = null,
        int? exactMatchPct = null)
    {
        var pct = tolerancePct ?? exactMatchPct ?? 0;
        var closest = SelectClosest(variants, clickSize);
        if (closest is null) return null;

        if (clickSize <= 0) return closest;

        var winnerSize = closest.Row.LargestFileSize ?? 0;
        if (winnerSize <= 0) return null;
        var deltaPct = Math.Abs(winnerSize - clickSize) * 100.0 / clickSize;
        return deltaPct <= pct ? closest : null;
    }

    private static VariantMatch? SelectClosest(IReadOnlyList<VariantRow> variants, long clickSize)
    {
        VariantRow? best = null;
        long bestDelta = long.MaxValue;
        DateTimeOffset bestPlayed = DateTimeOffset.MinValue;

        foreach (var v in variants)
        {
            var size = v.LargestFileSize ?? 0;
            var delta = clickSize > 0 ? Math.Abs(size - clickSize) : 0;
            var played = v.LastPlayedAt ?? DateTimeOffset.MinValue;
            if (best is null || delta < bestDelta || (delta == bestDelta && played > bestPlayed))
            {
                best = v;
                bestDelta = delta;
                bestPlayed = played;
            }
        }

        return best is null ? null : new VariantMatch(best, bestDelta);
    }

    private static async Task<List<VariantRow>> LoadVariantsAsync(
        DavDatabaseContext ctx,
        string contentGroupKey,
        CancellationToken ct)
    {
        var rows = await ctx.HistoryItems.AsNoTracking()
            .Where(h => h.ContentGroupKey == contentGroupKey
                        && h.DownloadStatus == HistoryItem.DownloadStatusOption.Completed)
            .Select(h => new
            {
                HistoryItemId = h.Id,
                h.LastPlayedAt,
                h.CreatedAt,
                LargestFileSize = ctx.Items
                    .Where(d => d.HistoryItemId == h.Id && d.Type == DavItem.ItemType.UsenetFile)
                    .Max(d => (long?)d.FileSize),
            })
            .ToListAsync(ct).ConfigureAwait(false);

        return rows
            .Select(r => new VariantRow(r.HistoryItemId, r.LargestFileSize, r.LastPlayedAt, r.CreatedAt))
            .ToList();
    }
}

public sealed record VariantRow(
    Guid HistoryItemId,
    long? LargestFileSize,
    DateTimeOffset? LastPlayedAt,
    DateTime CreatedAt);

public sealed record VariantMatch(VariantRow Row, long SizeDeltaBytes);

public sealed record VariantDecision(VariantMatch? ReuseMatch, bool GroupHasMembers, string? GroupKey)
{
    public static readonly VariantDecision NoOp = new(null, false, null);
}
