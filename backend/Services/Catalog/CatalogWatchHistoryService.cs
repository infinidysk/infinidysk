using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Services.Catalog;

/// <summary>
/// Watch history for the catalog: one resumable row per title (per episode for series),
/// written from playback creation and from the player's progress reports. Single-admin
/// deployments mean no per-user column and no cross-user locking; each write is one
/// upsert statement under EF's own transaction.
/// </summary>
public sealed class CatalogWatchHistoryService(
    Func<DavDatabaseContext> contextFactory,
    TimeProvider timeProvider)
{
    internal const int DefaultLimit = 20;
    internal const int MaxLimit = 200;
    internal const int RetentionDays = 32;
    internal const string FallbackTitle = "Unknown title";

    /// <summary>Rows that finished within the retention window stay visible for a re-watch.</summary>
    public static long RetentionCutoff(long nowEpoch) => nowEpoch - (RetentionDays * 86400L);

    /// <summary>
    /// Records the last known position for a title. Display fields are always resolved
    /// server-side, so an empty title is replaced with the fallback instead of being stored.
    /// </summary>
    public async Task ReportProgressAsync(ReportProgressRequest request, CancellationToken ct)
    {
        var kind = string.Equals(request.Type, "movie", StringComparison.Ordinal)
            ? WatchKind.Movie
            : WatchKind.Episode;
        var season = kind == WatchKind.Episode ? request.Season : null;
        var episode = kind == WatchKind.Episode ? request.Episode : null;
        var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        var duration = ToSeconds(request.Duration);
        var position = ToSeconds(request.Position);
        if (duration > 0 && position > duration) position = duration;
        if (request.Finished && duration > 0) position = duration;
        var title = NormalizeTitle(request.Title);

        // The unfinished lookup and the insert are not atomic, so two overlapping
        // reports can both miss and the resume index rejects the loser. One retry runs
        // against a fresh context where the winner's row exists.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await WriteAsync(ct).ConfigureAwait(false);
                return;
            }
            catch (DbUpdateException) when (attempt == 0 && !ct.IsCancellationRequested)
            {
            }
        }

        async Task WriteAsync(CancellationToken token)
        {
            await using var context = contextFactory();

            var existing = await context.CatalogWatchHistories
                .FirstOrDefaultAsync(entry => !entry.Finished
                                              && entry.Kind == kind
                                              && entry.TmdbId == request.TmdbId
                                              && entry.Season == season
                                              && entry.Episode == episode, token)
                .ConfigureAwait(false);

            if (existing is null)
            {
                // A finished row for the same title would render a second, stale card
                // beside this resume row; the new watch supersedes it.
                await context.CatalogWatchHistories
                    .Where(entry => entry.Finished
                                    && entry.Kind == kind
                                    && entry.TmdbId == request.TmdbId
                                    && entry.Season == season
                                    && entry.Episode == episode)
                    .ExecuteDeleteAsync(token)
                    .ConfigureAwait(false);

                context.CatalogWatchHistories.Add(new CatalogWatchHistory
                {
                    Kind = kind,
                    TmdbId = request.TmdbId,
                    Season = season,
                    Episode = episode,
                    Title = title,
                    Subtitle = request.Subtitle,
                    PosterUrl = request.PosterUrl,
                    BackdropUrl = request.BackdropUrl,
                    DurationSeconds = duration,
                    PositionSeconds = position,
                    StartedAtEpoch = now,
                    UpdatedAtEpoch = now,
                    Finished = request.Finished,
                });
            }
            else
            {
                existing.Title = title;
                existing.Subtitle = request.Subtitle ?? existing.Subtitle;
                existing.PosterUrl = request.PosterUrl ?? existing.PosterUrl;
                existing.BackdropUrl = request.BackdropUrl ?? existing.BackdropUrl;
                if (duration > 0) existing.DurationSeconds = duration;
                existing.PositionSeconds = position;
                existing.UpdatedAtEpoch = now;
                existing.Finished = request.Finished;
            }

            await PruneAsync(context, now, token).ConfigureAwait(false);
            await context.SaveChangesAsync(token).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Resume candidates, resumable rows first and finished rows at the tail, newest first
    /// within each group. <paramref name="limit"/> is clamped to a bounded page size.
    /// </summary>
    public async Task<IReadOnlyList<CatalogWatchHistory>> ListAsync(int limit, CancellationToken ct)
    {
        var take = Math.Clamp(limit, 1, MaxLimit);
        await using var context = contextFactory();
        return await context.CatalogWatchHistories
            .AsNoTracking()
            .OrderBy(entry => entry.Finished)
            .ThenByDescending(entry => entry.UpdatedAtEpoch)
            .Take(take)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <summary>Removes one row: the user's "Remove from Continue Watching" action.</summary>
    public async Task<bool> RemoveAsync(int id, CancellationToken ct)
    {
        await using var context = contextFactory();
        var removed = await context.CatalogWatchHistories
            .Where(entry => entry.Id == id)
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(false);
        return removed > 0;
    }

    private static async Task PruneAsync(DavDatabaseContext context, long now, CancellationToken ct)
    {
        var cutoff = RetentionCutoff(now);
        await context.CatalogWatchHistories
            .Where(entry => entry.UpdatedAtEpoch < cutoff)
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(false);
    }

    private static long ToSeconds(double? value)
        => value is > 0 ? (long)Math.Round(value.Value) : 0;

    private static string NormalizeTitle(string? title)
        => string.IsNullOrWhiteSpace(title) ? FallbackTitle : title.Trim();
}

/// <summary>
/// One progress write. Display fields are filled by the server from provider metadata;
/// the player only supplies coordinates and position.
/// </summary>
public sealed record ReportProgressRequest(
    string Type,
    int TmdbId,
    int? Season,
    int? Episode,
    double? Position,
    double? Duration,
    bool Finished,
    string? Title,
    string? Subtitle,
    string? PosterUrl,
    string? BackdropUrl);
