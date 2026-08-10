using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Extensions;
using NzbWebDAV.Logging;
using NzbWebDAV.Utils;
using Serilog;

namespace NzbWebDAV.Services;

public class WatchtowerService(
    ConfigManager configManager,
    SearchProfileService searchProfileService,
    PlaybackFastVerifier fastVerifier,
    IndexerHitTracker hitTracker,
    NewznabRateLimiter rateLimiter,
    CandidateNegativeCache negativeCache,
    WardenStore wardenStore,
    PreflightCache preflightCache,
    ListSourceEnumerator enumerator,
    EpisodeEnumerator episodeEnumerator,
    PreferredOrderStore preferredOrderStore,
    NzbFetchCoalescer nzbFetchCoalescer,
    BenchmarkGate benchmarkGate
) : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan NzbFetchTimeout = TimeSpan.FromSeconds(15);

    // Heartbeat throttles. The cycle loop ticks every 20s forever, so logging each
    // tick would fill the whole log buffer and evict everything support needs.
    private static readonly TimeSpan CycleStartLogInterval = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan NoProfileLogInterval = TimeSpan.FromMinutes(60);
    private const string CycleStartLogKey = "cycle-starting";
    private const string NoProfileLogKey = "no-search-profile";
    private readonly LogThrottle _logThrottle = new();
    private const int ResolvesPerTick = 3;
    private const int AutoResolveBatch = 25;
    private static readonly TimeSpan AutoStageBudget = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan CycleWatchdog = TimeSpan.FromMinutes(15);
    private const int KeepFreshPerTick = 5;
    private const int ExpandsPerTick = 5;
    private const long SeasonBundleGraceSeconds = 14L * 86400L;

    private int _resolveDayKey = -1;
    private int _resolvesToday;

    private readonly object _ctsLock = new();
    private CancellationTokenSource? _disabledCts;

    private readonly SemaphoreSlim _indexerGate = new(1, 1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        configManager.OnConfigChanged += OnConfigChanged;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    // pause verification while a connection speed-test is running
                    if (benchmarkGate.IsPaused)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken).ConfigureAwait(false);
                        continue;
                    }

                    if (!configManager.IsWatchtowerEnabled())
                    {
                        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken).ConfigureAwait(false);
                        continue;
                    }

                    using var cycleCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    lock (_ctsLock) _disabledCts = cycleCts;
                    var ct = cycleCts.Token;
                    var cycleWatch = Stopwatch.StartNew();
                    if (_logThrottle.ShouldLog(CycleStartLogKey, CycleStartLogInterval, out var skippedCycles))
                    {
                        if (skippedCycles > 0)
                            LogActivity("Watchtower: cycle starting ({Skipped:n0} cycles since the last log)",
                                skippedCycles);
                        else
                            LogActivity("Watchtower: cycle starting");
                    }
                    try
                    {
                        var cycle = RunCycleAsync(cycleWatch, ct);
                        var winner = await Task.WhenAny(cycle, Task.Delay(CycleWatchdog, stoppingToken)).ConfigureAwait(false);
                        if (winner != cycle)
                        {
                            Log.Warning("Watchtower: cycle exceeded {Budget:n0}s watchdog; abandoning and restarting",
                                CycleWatchdog.TotalSeconds);
#pragma warning disable CA1849 // synchronous Cancel is required here -- CancelAsync cannot be awaited while holding _ctsLock, and the cancel must be ordered before the abandoned-cycle continuation
                            lock (_ctsLock) cycleCts.Cancel();
#pragma warning restore CA1849
                            _ = cycle.ContinueWith(static t => _ = t.Exception, CancellationToken.None,
                                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                                TaskScheduler.Default);
                        }
                        else
                        {
                            await cycle.ConfigureAwait(false);
                        }
                    }
                    catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                    {
                        continue;
                    }
                    finally
                    {
                        lock (_ctsLock) _disabledCts = null;
                    }

                    await Task.Delay(Tick, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (SigtermUtil.IsSigtermTriggered() || stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    e.LogWarningKnownOrStack("Watchtower loop error.");
                    await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            configManager.OnConfigChanged -= OnConfigChanged;
        }
    }

    private async Task RunCycleAsync(Stopwatch cycleWatch, CancellationToken ct)
    {
        await SyncDueSourcesAsync(ct).ConfigureAwait(false);
        await ExpandDueExpandersAsync(ct).ConfigureAwait(false);
        await ResolveDueItemsAsync(ct).ConfigureAwait(false);
        await KeepFreshDueItemsAsync(ct).ConfigureAwait(false);
        await LogCycleHeartbeatAsync(cycleWatch, ct).ConfigureAwait(false);
    }

    private void OnConfigChanged(object? sender, ConfigManager.ConfigEventArgs e)
    {
        if (e.ChangedConfig.ContainsKey(ConfigKeys.WatchtowerVerboseLogging))
        {
            Log.Information("Watchtower: verbose activity logging {State}",
                configManager.IsWatchtowerVerboseLoggingEnabled() ? "enabled" : "disabled");
            _logThrottle.Reset(CycleStartLogKey);
        }

        // Report the fresh state on the next cycle instead of waiting out the throttle.
        if (e.ChangedConfig.ContainsKey(ConfigKeys.WatchtowerProfileToken))
            _logThrottle.Reset(NoProfileLogKey);

        if (!e.ChangedConfig.ContainsKey(ConfigKeys.WatchtowerEnabled)) return;
        if (configManager.IsWatchtowerEnabled()) return;
        lock (_ctsLock) _disabledCts?.Cancel();
    }

    private void LogActivity(string template, params object?[] args)
    {
        if (configManager.IsWatchtowerVerboseLoggingEnabled())
            Log.Information(template, args);
        else
            Log.Debug(template, args);
    }

    private void LogActivity(Exception exception, string template, params object?[] args)
    {
        if (configManager.IsWatchtowerVerboseLoggingEnabled())
            Log.Information(exception, template, args);
        else
            Log.Debug(exception, template, args);
    }

    private async Task LogCycleHeartbeatAsync(Stopwatch cycleWatch, CancellationToken ct)
    {
        if (!configManager.IsWatchtowerVerboseLoggingEnabled()) return;

        await using var ctx = new DavDatabaseContext();
        var grouped = await ctx.WantedItems
            .GroupBy(w => w.State)
            .Select(g => new { State = g.Key, Count = g.Count() })
            .ToListAsync(ct).ConfigureAwait(false);
        var counts = grouped.ToDictionary(x => x.State, x => x.Count);
        int Get(string state) => counts.GetValueOrDefault(state);

        Log.Information(
            "Watchtower: cycle complete in {Elapsed:n1}s — {Ready} ready, {Scouting} scouting, {Unavailable} unavailable, {Series} series, {Parked} parked",
            cycleWatch.Elapsed.TotalSeconds,
            Get(WantedItem.StateReady), Get(WantedItem.StateScouting), Get(WantedItem.StateUnavailable),
            Get(WantedItem.StateExpander), Get(WantedItem.StateParked));
    }

    private async Task SyncDueSourcesAsync(CancellationToken ct)
    {
        var now = Now();
        var interval = configManager.GetWatchtowerSyncIntervalSeconds();

        await using var ctx = new DavDatabaseContext();
        var sources = await ctx.ListSources
            .Where(s => s.Enabled && s.Kind != ListSource.KindManual)
            .ToListAsync(ct).ConfigureAwait(false);

        foreach (var source in sources)
        {
            ct.ThrowIfCancellationRequested();
            if (source.LastSyncedAtUnix is { } last && now - last < interval) continue;
            try
            {
                var refs = await enumerator.EnumerateAsync(source, ct).ConfigureAwait(false);
                await ReconcileSourceAsync(ctx, source, refs, now, ct).ConfigureAwait(false);
                source.LastSyncError = null;
            }
            catch (Exception e) when (e is not OperationCanceledException && e is not OutOfMemoryException)
            {
                source.LastSyncError = e.Message;
                Log.Warning(e, "Watchtower: sync failed for source {Name}", source.Name);
            }
            source.LastSyncedAtUnix = now;
            await TrySaveBatchItemAsync(ctx, ct).ConfigureAwait(false);
            DetachWantedItems(ctx);
        }
    }

    private async Task ReconcileSourceAsync(
        DavDatabaseContext ctx, ListSource source, IReadOnlyList<WtContentRef> refs, long now, CancellationToken ct)
    {
        var srcId = source.Id.ToString();

        var yielded = new Dictionary<string, WtContentRef>();
        foreach (var r in refs.Where(r => !string.IsNullOrWhiteSpace(r.Type) && !string.IsNullOrWhiteSpace(r.ContentId)))
        {
            yielded[$"{r.Type}:{r.ContentId}"] = r;
        }

        var existing = await LoadRowsByKeysAsync(ctx, yielded.Keys, ct).ConfigureAwait(false);
        var existingByKey = existing.ToDictionary(w => w.Key);

        foreach (var (key, r) in yielded)
        {
            if (!existingByKey.TryGetValue(key, out var row))
            {
                ctx.WantedItems.Add(new WantedItem
                {
                    Id = Guid.NewGuid(),
                    Key = key,
                    Type = r.Type,
                    ContentId = r.ContentId,
                    Title = string.IsNullOrWhiteSpace(r.Title) ? r.ContentId : r.Title!,
                    State = WantedItem.IsBareSeries(r.Type, r.ContentId) ? WantedItem.StateExpander : WantedItem.StateScouting,
                    Provenance = WtJson.WriteStrings(new[] { srcId }),
                    Shortlist = "[]",
                    CreatedAtUnix = now,
                    UpdatedAtUnix = now,
                    NextCheckAtUnix = now,
                });
                continue;
            }

            var prov = WtJson.ReadStrings(row.Provenance);
            if (!prov.Contains(srcId))
            {
                prov.Add(srcId);
                await ctx.WantedItems.Where(w => w.Id == row.Id)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(w => w.Provenance, WtJson.WriteStrings(prov))
                        .SetProperty(w => w.UpdatedAtUnix, now), ct).ConfigureAwait(false);
            }
            if (string.IsNullOrWhiteSpace(row.Title) && !string.IsNullOrWhiteSpace(r.Title))
                await ctx.WantedItems.Where(w => w.Id == row.Id)
                    .ExecuteUpdateAsync(s => s.SetProperty(w => w.Title, r.Title!), ct).ConfigureAwait(false);
            if (row.State != WantedItem.StateExpander && WantedItem.IsBareSeries(row.Type, row.ContentId))
                await ctx.WantedItems.Where(w => w.Id == row.Id)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(w => w.State, WantedItem.StateExpander)
                        .SetProperty(w => w.Shortlist, "[]")
                        .SetProperty(w => w.WinnerNzb, (byte[]?)null)
                        .SetProperty(w => w.FailReason, (string?)null)
                        .SetProperty(w => w.NextCheckAtUnix, (long?)now)
                        .SetProperty(w => w.UpdatedAtUnix, now), ct).ConfigureAwait(false);
        }

        async Task<List<ClaimRow>> LoadClaimedAsync() =>
            await ctx.WantedItems.AsNoTracking()
                .Where(w => w.Provenance.Contains(srcId))
                .Select(w => new ClaimRow(w.Id, w.Key, w.State, w.Provenance))
                .ToListAsync(ct).ConfigureAwait(false);

        var claimed = await LoadClaimedAsync().ConfigureAwait(false);
        var cascaded = false;
        foreach (var c in claimed)
        {
            if (yielded.ContainsKey(c.Key) || c.State != WantedItem.StateExpander) continue;
            var prov = WtJson.ReadStrings(c.Provenance);
            prov.Remove(srcId);
            if (prov.Count != 0) continue;
            await WtReconcile.RemoveWithChildrenByRefAsync(ctx, c.Id, c.Key, c.State, now, ct).ConfigureAwait(false);
            cascaded = true;
        }

        var staleIds = new List<Guid>();
        foreach (var c in (cascaded ? await LoadClaimedAsync().ConfigureAwait(false) : claimed)
                     .Where(c => !yielded.ContainsKey(c.Key)))
        {
            var prov = WtJson.ReadStrings(c.Provenance);
            prov.Remove(srcId);
            if (prov.Count == 0)
            {
                if (c.State == WantedItem.StateExpander)
                    await WtReconcile.RemoveWithChildrenByRefAsync(ctx, c.Id, c.Key, c.State, now, ct).ConfigureAwait(false);
                else
                    staleIds.Add(c.Id);
            }
            else
            {
                await ctx.WantedItems.Where(w => w.Id == c.Id)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(w => w.Provenance, WtJson.WriteStrings(prov))
                        .SetProperty(w => w.UpdatedAtUnix, now), ct).ConfigureAwait(false);
            }
        }
        await WtReconcile.DeleteByIdsAsync(ctx, staleIds, ct).ConfigureAwait(false);
    }

    private sealed record ClaimRow(Guid Id, string Key, string State, string Provenance);

    private sealed record WantedRow(
        Guid Id, string Key, string Type, string ContentId, string Title, string State, string Provenance);

    private static async Task<List<WantedRow>> LoadRowsByKeysAsync(
        DavDatabaseContext ctx, IReadOnlyCollection<string> keys, CancellationToken ct)
    {
        var result = new List<WantedRow>(keys.Count);
        if (keys.Count == 0) return result;
        var all = keys as string[] ?? keys.ToArray();
        const int chunk = 400;
        for (var i = 0; i < all.Length; i += chunk)
        {
            var slice = all.Skip(i).Take(chunk).ToArray();
            var rows = await ctx.WantedItems.AsNoTracking()
                .Where(w => slice.Contains(w.Key))
                .Select(w => new WantedRow(w.Id, w.Key, w.Type, w.ContentId, w.Title, w.State, w.Provenance))
                .ToListAsync(ct).ConfigureAwait(false);
            result.AddRange(rows);
        }
        return result;
    }

    private static void DetachWantedItems(DavDatabaseContext ctx)
    {
        foreach (var entry in ctx.ChangeTracker.Entries<WantedItem>().ToList())
            entry.State = EntityState.Detached;
    }

    private static async Task<bool> TrySaveBatchItemAsync(DavDatabaseContext ctx, CancellationToken ct)
    {
        try
        {
            await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateConcurrencyException e)
        {
            foreach (var entry in e.Entries)
                entry.State = EntityState.Detached;
            foreach (var entry in ctx.ChangeTracker.Entries().Where(en => en.State == EntityState.Added).ToList())
                entry.State = EntityState.Detached;
            Log.Debug(e, "Watchtower: batch item removed concurrently during save; skipping");
            return false;
        }
    }

    private async Task ExpandDueExpandersAsync(CancellationToken ct)
    {
        var globalScope = configManager.GetWatchtowerSeriesScope();

        var now = Now();
        var interval = configManager.GetWatchtowerSyncIntervalSeconds();

        await using var ctx = new DavDatabaseContext();
        var due = await ctx.WantedItems
            .Where(w => w.State == WantedItem.StateExpander
                        && (w.NextCheckAtUnix == null || w.NextCheckAtUnix <= now))
            .OrderBy(w => w.NextCheckAtUnix)
            .Take(ExpandsPerTick)
            .ToListAsync(ct).ConfigureAwait(false);

        if (due.Count == 0) return;

        var scopeBySource = await ctx.ListSources.AsNoTracking()
            .ToDictionaryAsync(
                s => s.Id.ToString(),
                s => ConfigManager.NormalizeSeriesScope(s.SeriesScope) ?? globalScope,
                ct)
            .ConfigureAwait(false);

        foreach (var expander in due)
        {
            if (ct.IsCancellationRequested) break;
            var scopes = EffectiveScopes(expander, scopeBySource, globalScope);
            if (scopes.Count > 0)
            {
                try
                {
                    await ExpandOneAsync(ctx, expander, scopes, now, ct).ConfigureAwait(false);
                }
                catch (Exception e) when (e is not OperationCanceledException && e is not OutOfMemoryException)
                {
                    LogActivity(e, "Watchtower: expand failed for {Key}", expander.Key);
                }
            }
            expander.NextCheckAtUnix = now + interval;
            expander.UpdatedAtUnix = now;
            await TrySaveBatchItemAsync(ctx, ct).ConfigureAwait(false);
        }
    }

    private static List<string> EffectiveScopes(
        WantedItem expander, Dictionary<string, string> scopeBySource, string globalScope)
    {
        var scopes = new List<string>();
        foreach (var srcId in WtJson.ReadStrings(expander.Provenance))
        {
            var scope = scopeBySource.TryGetValue(srcId, out var s) ? s : globalScope;
            if (scope == "off" || scopes.Contains(scope)) continue;
            scopes.Add(scope);
        }
        return scopes;
    }

    private async Task ExpandOneAsync(DavDatabaseContext ctx, WantedItem expander, IReadOnlyList<string> scopes, long now, CancellationToken ct)
    {
        var tag = WtReconcile.ExpanderTag(expander.Key);
        var desired = await BuildDesiredAsync(ctx, expander, scopes, now, ct).ConfigureAwait(false);
        if (desired is null) return;

        var existing = await LoadRowsByKeysAsync(ctx, desired.Keys, ct).ConfigureAwait(false);
        var existingByKey = existing.ToDictionary(w => w.Key);

        foreach (var (childKey, row) in desired)
        {
            if (existingByKey.TryGetValue(childKey, out var child))
            {
                var prov = WtJson.ReadStrings(child.Provenance);
                if (!prov.Contains(tag))
                {
                    prov.Add(tag);
                    await ctx.WantedItems.Where(w => w.Id == child.Id)
                        .ExecuteUpdateAsync(s => s
                            .SetProperty(w => w.Provenance, WtJson.WriteStrings(prov))
                            .SetProperty(w => w.UpdatedAtUnix, now), ct).ConfigureAwait(false);
                }
            }
            else
            {
                ctx.WantedItems.Add(new WantedItem
                {
                    Id = Guid.NewGuid(),
                    Key = childKey,
                    Type = row.Type,
                    ContentId = row.ContentId,
                    Title = row.Title,
                    State = WantedItem.StateScouting,
                    Provenance = WtJson.WriteStrings(new[] { tag }),
                    Shortlist = "[]",
                    CreatedAtUnix = now,
                    UpdatedAtUnix = now,
                    NextCheckAtUnix = now,
                });
            }
        }

        var tagged = await ctx.WantedItems.AsNoTracking()
            .Where(w => w.Provenance.Contains(tag))
            .Select(w => new { w.Id, w.Key, w.Provenance })
            .ToListAsync(ct).ConfigureAwait(false);
        var orphanIds = new List<Guid>();
        foreach (var child in tagged)
        {
            var prov = WtJson.ReadStrings(child.Provenance);
            if (!prov.Contains(tag)) continue;
            if (desired.ContainsKey(child.Key)) continue;
            prov.Remove(tag);
            if (prov.Count == 0)
                orphanIds.Add(child.Id);
            else
                await ctx.WantedItems.Where(w => w.Id == child.Id)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(w => w.Provenance, WtJson.WriteStrings(prov))
                        .SetProperty(w => w.UpdatedAtUnix, now), ct).ConfigureAwait(false);
        }
        await WtReconcile.DeleteByIdsAsync(ctx, orphanIds, ct).ConfigureAwait(false);

        Log.Information("Watchtower: expanded {Key} -> {Count} row(s) (scope {Scope})",
            expander.Key, desired.Count, string.Join("+", scopes));
    }

    private async Task<Dictionary<string, DesiredRow>?> BuildDesiredAsync(
        DavDatabaseContext ctx, WantedItem expander, IReadOnlyList<string> scopes, long now, CancellationToken ct)
    {
        if (IsImdbId(expander.ContentId))
        {
            var episodes = await episodeEnumerator.EnumerateImdbAsync(expander.ContentId, ct).ConfigureAwait(false);
            if (episodes.Count == 0) return null;
            var imdb = CanonicalImdb(expander.ContentId);
            var parked = configManager.IsWatchtowerSeasonBundleFallbackEnabled()
                ? await GetParkedFallbackSeasonsAsync(ctx, imdb, ct).ConfigureAwait(false)
                : new HashSet<int>();
            var desired = new Dictionary<string, DesiredRow>();
            foreach (var scope in scopes)
                foreach (var (k, v) in BuildDesiredRows(episodes, expander.Title, imdb, scope, now, parked))
                    desired[k] = v;
            return ClampSeries(desired);
        }

        var kitsuId = ParseKitsuId(expander.ContentId);
        if (kitsuId is not null)
        {
            var episodes = await episodeEnumerator.EnumerateKitsuAsync(kitsuId, ct).ConfigureAwait(false);
            if (episodes.Count == 0) return null;
            var desired = new Dictionary<string, DesiredRow>();
            foreach (var scope in scopes)
                foreach (var (k, v) in BuildAnimeDesiredRows(episodes, expander.Title, kitsuId, scope, now))
                    desired[k] = v;
            return ClampSeries(desired);
        }

        return null;
    }

    private Dictionary<string, DesiredRow> BuildAnimeDesiredRows(
        IReadOnlyList<EpisodeEnumerator.Episode> episodes, string? seriesTitle, string kitsuId, string scope, long now)
    {
        var desired = new Dictionary<string, DesiredRow>();
        var aired = episodes.Where(e => e.AirDateUnix is null || e.AirDateUnix <= now).OrderBy(e => e.Number).ToList();
        if (aired.Count == 0) return desired;

        var count = scope == "recent"
            ? configManager.GetWatchtowerSeriesRecentCount()
            : configManager.GetWatchtowerSeriesMaxEpisodes();

        var selected = count <= 0
            ? (IEnumerable<EpisodeEnumerator.Episode>)aired
            : scope == "first-season"
                ? aired.Take(count)
                : aired.Skip(Math.Max(0, aired.Count - count));

        foreach (var ep in selected)
        {
            var contentId = $"kitsu:{kitsuId}:{ep.Number}";
            desired[$"series:{contentId}"] = new DesiredRow("series", contentId, AnimeTitle(seriesTitle, ep.Number));
        }
        return desired;
    }

    private static string? ParseKitsuId(string contentId)
    {
        var parts = contentId.Split(':');
        if (parts.Length != 2 || !parts[0].Equals("kitsu", StringComparison.OrdinalIgnoreCase)) return null;
        return parts[1].Length > 0 && parts[1].All(char.IsDigit) ? parts[1] : null;
    }

    private static string AnimeTitle(string? seriesTitle, int number)
    {
        var code = $"E{number:D2}";
        var baseTitle = seriesTitle?.Trim();
        return string.IsNullOrEmpty(baseTitle) ? code : $"{baseTitle} {code}";
    }

    private static async Task<HashSet<int>> GetParkedFallbackSeasonsAsync(
        DavDatabaseContext ctx, string imdb, CancellationToken ct)
    {
        var prefix = imdb + ":";
        var ids = await ctx.WantedItems.AsNoTracking()
            .Where(w => w.Type == "season" && w.State == WantedItem.StateParked && w.ContentId.StartsWith(prefix))
            .Select(w => w.ContentId)
            .ToListAsync(ct).ConfigureAwait(false);

        var seasons = new HashSet<int>();
        foreach (var parts in ids.Select(id => id.Split(':')))
        {
            if (parts.Length >= 2 && int.TryParse(parts[^1], out var s)) seasons.Add(s);
        }
        return seasons;
    }

    private Dictionary<string, DesiredRow> BuildDesiredRows(
        IReadOnlyList<EpisodeEnumerator.Episode> episodes, string? seriesTitle, string imdb, string scope, long now,
        HashSet<int> parkedFallbackSeasons)
    {
        var desired = new Dictionary<string, DesiredRow>();
        var aired = episodes.Where(e => e.AirDateUnix is null || e.AirDateUnix <= now).ToList();
        if (aired.Count == 0) return desired;

        var recentCount = configManager.GetWatchtowerSeriesRecentCount();
        if (scope == "recent")
        {
            foreach (var ep in aired.Skip(Math.Max(0, aired.Count - recentCount)))
                AddEpisodeRow(desired, imdb, seriesTitle, ep);
            return desired;
        }

        var bundlesEnabled = configManager.IsWatchtowerSeasonBundlesEnabled();
        var fallbackCap = configManager.GetWatchtowerSeasonBundleFallbackMaxEpisodes();
        var seasons = scope switch
        {
            "all-aired" => aired.Select(e => e.Season).Distinct().OrderByDescending(s => s).ToList(),
            "first-season" => new List<int> { aired.Min(e => e.Season) },
            _ => new List<int> { aired.Max(e => e.Season) },
        };

        foreach (var season in seasons)
        {
            if (bundlesEnabled && SeasonComplete(episodes, season, now))
            {
                AddSeasonRow(desired, imdb, seriesTitle, season);
                if (parkedFallbackSeasons.Contains(season))
                {
                    foreach (var ep in aired.Where(e => e.Season == season).OrderBy(e => e.Number).Take(fallbackCap))
                        AddEpisodeRow(desired, imdb, seriesTitle, ep);
                }
                continue;
            }
            foreach (var ep in aired.Where(e => e.Season == season).OrderBy(e => e.Number))
                AddEpisodeRow(desired, imdb, seriesTitle, ep);
        }
        return desired;
    }

    private Dictionary<string, DesiredRow> ClampSeries(Dictionary<string, DesiredRow> desired)
    {
        var cap = configManager.GetWatchtowerSeriesMaxEpisodes();
        if (cap <= 0) return desired;

        var episodes = desired.Where(kv => kv.Value.Type != "season").ToList();
        if (episodes.Count <= cap) return desired;

        var keepOldest = configManager.GetWatchtowerSeriesCapKeep() == "oldest";
        var keptEpisodes = (keepOldest
                ? episodes.OrderBy(kv => RowOrder(kv.Value))
                : episodes.OrderByDescending(kv => RowOrder(kv.Value)))
            .Take(cap);
        var bundles = desired.Where(kv => kv.Value.Type == "season");
        return bundles.Concat(keptEpisodes).ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    private static (int Season, int Number) RowOrder(DesiredRow row)
    {
        var parts = row.ContentId.Split(':');
        if (row.Type == "season")
            return (parts.Length >= 2 && int.TryParse(parts[^1], out var s) ? s : 0, int.MaxValue);
        if (parts.Length >= 1 && parts[0].Equals("kitsu", StringComparison.OrdinalIgnoreCase))
            return (1, parts.Length >= 3 && int.TryParse(parts[^1], out var kn) ? kn : 0);
        var season = parts.Length >= 3 && int.TryParse(parts[^2], out var se) ? se : 0;
        var number = parts.Length >= 3 && int.TryParse(parts[^1], out var ne) ? ne : 0;
        return (season, number);
    }

    private static void AddEpisodeRow(
        Dictionary<string, DesiredRow> desired, string imdb, string? seriesTitle, EpisodeEnumerator.Episode ep)
    {
        var contentId = $"{imdb}:{ep.Season}:{ep.Number}";
        desired[$"series:{contentId}"] = new DesiredRow("series", contentId, ChildTitle(seriesTitle, ep));
    }

    private static void AddSeasonRow(
        Dictionary<string, DesiredRow> desired, string imdb, string? seriesTitle, int season)
    {
        var contentId = $"{imdb}:{season}";
        desired[$"season:{contentId}"] = new DesiredRow("season", contentId, SeasonTitle(seriesTitle, season));
    }

    private static bool SeasonComplete(IReadOnlyList<EpisodeEnumerator.Episode> all, int season, long now)
    {
        var eps = all.Where(e => e.Season == season).ToList();
        if (eps.Count == 0) return false;
        if (eps.Any(e => e.AirDateUnix is { } a && a > now)) return false;
        var known = eps.Where(e => e.AirDateUnix is not null).Select(e => e.AirDateUnix!.Value).ToList();
        if (known.Count == 0) return true;
        return now - known.Max() >= SeasonBundleGraceSeconds;
    }

    private static string SeasonTitle(string? seriesTitle, int season)
    {
        var code = $"S{season:D2} (season bundle)";
        var baseTitle = seriesTitle?.Trim();
        return string.IsNullOrEmpty(baseTitle) ? code : $"{baseTitle} {code}";
    }

    private sealed record DesiredRow(string Type, string ContentId, string Title);

    private static string ChildTitle(string? seriesTitle, EpisodeEnumerator.Episode ep)
    {
        var code = $"S{ep.Season:D2}E{ep.Number:D2}";
        var baseTitle = seriesTitle?.Trim();
        return string.IsNullOrEmpty(baseTitle) ? code : $"{baseTitle} {code}";
    }

    private static bool IsImdbId(string contentId)
    {
        var s = contentId;
        var colon = s.IndexOf(':', StringComparison.Ordinal);
        if (colon > 0) s = s[..colon];
        if (s.StartsWith("tt", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        return s.Length > 0 && s.All(char.IsDigit);
    }

    private static string CanonicalImdb(string contentId)
    {
        var s = contentId;
        var colon = s.IndexOf(':', StringComparison.Ordinal);
        if (colon > 0) s = s[..colon];
        return s.StartsWith("tt", StringComparison.OrdinalIgnoreCase) ? s : "tt" + s;
    }

    private async Task ResolveDueItemsAsync(CancellationToken ct)
    {
        var auto = configManager.IsWatchtowerAutoThroughput();
        var dailyBudget = configManager.GetWatchtowerDailyResolveBudget();
        RollDailyBudget();

        var profileToken = ResolveProfileToken();
        if (profileToken is null)
        {
            if (_logThrottle.ShouldLog(NoProfileLogKey, NoProfileLogInterval, out var skippedResolves))
            {
                if (skippedResolves > 0)
                    LogActivity(
                        "Watchtower: no search profile configured; skipping resolve " +
                        "({Skipped:n0} cycles skipped since the last log)",
                        skippedResolves);
                else
                    LogActivity("Watchtower: no search profile configured; skipping resolve");
            }

            return;
        }

        _logThrottle.Reset(NoProfileLogKey);

        var concurrency = configManager.GetWatchtowerResolveConcurrency();
        var stageWatch = Stopwatch.StartNew();
        do
        {
            if (ct.IsCancellationRequested) return;

            var budgetRoom = auto || dailyBudget == 0 ? int.MaxValue : dailyBudget - _resolvesToday;
            if (budgetRoom <= 0) return;

            var now = Now();
            await using var ctx = new DavDatabaseContext();

            var cap = configManager.GetWatchtowerActiveSetCap();
            var activeReady = await ctx.WantedItems.CountAsync(w => w.State == WantedItem.StateReady, ct).ConfigureAwait(false);
            var capRoom = cap - activeReady;
            if (capRoom <= 0) return;

            var batch = auto ? AutoResolveBatch : ResolvesPerTick;
            var take = Math.Min(batch, Math.Min(capRoom, budgetRoom));
            if (take <= 0) return;

            var dueIds = await ctx.WantedItems
                .Where(w => w.State == WantedItem.StateScouting
                            || (w.State == WantedItem.StateUnavailable
                                && w.NextCheckAtUnix != null && w.NextCheckAtUnix <= now))
                .OrderByDescending(w => w.CreatedAtUnix)
                .Take(take)
                .Select(w => w.Id)
                .ToListAsync(ct).ConfigureAwait(false);

            if (dueIds.Count == 0) return;

            if (auto && !await searchProfileService.HasResolveHeadroomAsync(profileToken, ct).ConfigureAwait(false))
            {
                LogActivity("Watchtower: all indexers out of headroom; pausing resolve until caps reset");
                return;
            }

            await Parallel.ForEachAsync(dueIds, new ParallelOptions
            {
                MaxDegreeOfParallelism = concurrency,
                CancellationToken = ct,
            }, async (id, itemCt) =>
            {
                if (auto && !await searchProfileService.HasResolveHeadroomAsync(profileToken, itemCt).ConfigureAwait(false))
                    return;

                await using var itemCtx = new DavDatabaseContext();
                var item = await itemCtx.WantedItems.FirstOrDefaultAsync(w => w.Id == id, itemCt).ConfigureAwait(false);
                if (item is null) return;

                var resolved = false;
                try
                {
                    await ResolveOneAsync(itemCtx, profileToken, item, itemCt).ConfigureAwait(false);
                    resolved = true;
                }
                catch (Exception e) when (e is not OperationCanceledException && e is not OutOfMemoryException)
                {
                    LogActivity(e, "Watchtower: resolve failed for {Key}", item.Key);
                }

                if (resolved && await TrySaveBatchItemAsync(itemCtx, itemCt).ConfigureAwait(false))
                    Interlocked.Increment(ref _resolvesToday);
            }).ConfigureAwait(false);
        }
        while (auto && stageWatch.Elapsed < AutoStageBudget);
    }

    private static string KeyOf(NzbResolutionCache.Candidate c) =>
        ReleaseIdentity.Key(c.Size, c.Poster, c.UsenetDate, c.NzbUrl);

    private async Task ResolveOneAsync(DavDatabaseContext ctx, string profileToken, WantedItem item, CancellationToken ct)
    {
        var now = Now();
        if (WantedItem.IsBareSeries(item.Type, item.ContentId))
        {
            item.State = WantedItem.StateExpander;
            item.Shortlist = "[]";
            item.WinnerNzb = null;
            item.FailReason = null;
            item.NextCheckAtUnix = now;
            item.UpdatedAtUnix = now;
            return;
        }
        SearchProfileService.SearchResult? search;
        await _indexerGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            search = await searchProfileService
                .SearchByImdbAsync(profileToken, item.Type, item.ContentId, ct, verifyIdentity: true, startPreflight: false)
                .ConfigureAwait(false);
        }
        finally
        {
            _indexerGate.Release();
        }
        var candidates = search?.Candidates ?? (IReadOnlyList<NzbResolutionCache.Candidate>)Array.Empty<NzbResolutionCache.Candidate>();

        var floor = configManager.GetWatchtowerSizeFloorBytes();
        var ceiling = configManager.GetWatchtowerSizeCeilingBytes();
        var minGrabs = configManager.GetWatchtowerMinGrabs();

        var filtered = candidates
            .Where(c => (c.Password ?? 0) == 0)
            .Where(c => floor <= 0 || c.Size <= 0 || c.Size >= floor)
            .Where(c => ceiling <= 0 || c.Size <= 0 || c.Size <= ceiling)
            .Where(c => minGrabs <= 0 || (c.Grabs ?? 0) >= minGrabs);
        var filteredList = filtered.ToList();
        var preferredOrder = preferredOrderStore.GetOrder(profileToken, item.Type, item.ContentId);
        var ranked = preferredOrder is { Count: > 0 }
            ? PreferredOrderStore.ApplyOrder(filteredList, KeyOf, preferredOrder)
            : configManager.GetWatchtowerRanking() == "largest"
                ? filteredList.OrderByDescending(c => c.Size).ToList()
                : filteredList;

        var depth = configManager.GetWatchtowerShortlistDepth();
        var grabCap = configManager.GetWatchtowerGrabCapPerResolve();
        var sample = configManager.GetWatchtowerVerifySampleCount();

        var shortlist = new List<WtPointer>();
        byte[]? winnerBytes = null;
        string? responderHost = null;
        var grabs = 0;
        var knownDead = 0;
        var verifiedDead = 0;
        var timedOut = 0;

        foreach (var c in ranked)
        {
            if (shortlist.Count >= depth || grabs >= grabCap || ct.IsCancellationRequested) break;
            if (negativeCache.IsFailed(c.NzbUrl)
                || wardenStore.IsDeadAnywhere(WardenFingerprint.Compute(c.Size, c.Poster, c.UsenetDate)))
            {
                knownDead++;
                continue;
            }

            var bytes = await FetchNzbBytesAsync(c, ct).ConfigureAwait(false);
            grabs++;
            if (bytes is null) continue;

            PlaybackFastVerifier.VerifyOutcome outcome;
            using (var ms = new MemoryStream(bytes, writable: false))
                outcome = await fastVerifier
                    .VerifyAsync(ms, "stat", sample, ct, VerifyTimeout)
                    .ConfigureAwait(false);

            if (outcome.Verdict == PlaybackFastVerifier.Verdict.Dead)
            {
                verifiedDead++;
                negativeCache.MarkFailed(c.NzbUrl);
                wardenStore.MarkDead(WardenFingerprint.Compute(c.Size, c.Poster, c.UsenetDate));
                continue;
            }
            if (outcome.Verdict == PlaybackFastVerifier.Verdict.Timeout)
            {
                timedOut++;
                continue;
            }

            var ptr = new WtPointer
            {
                NzbUrl = c.NzbUrl,
                IndexerName = c.IndexerName,
                IndexerUserAgent = c.IndexerUserAgent,
                ProxyUrl = c.ProxyUrl,
                Title = c.Title,
                Size = c.Size,
                Grabs = c.Grabs,
                Poster = c.Poster,
                UsenetDate = c.UsenetDate,
                Verdict = "available",
                LastVerifiedAtUnix = now,
            };
            if (shortlist.Count == 0)
            {
                winnerBytes = bytes;
                responderHost = outcome.ResponderHost;
            }
            shortlist.Add(ptr);
        }

        if (shortlist.Count > 0)
        {
            item.State = WantedItem.StateReady;
            item.Shortlist = WtJson.WritePointers(shortlist);
            item.WinnerNzb = winnerBytes;
            item.ResponderHost = responderHost;
            item.LastResolvedAtUnix = now;
            item.LastVerifiedAtUnix = now;
            item.NextCheckAtUnix = now + configManager.GetWatchtowerKeepFreshBaseSeconds();
            item.FailReason = null;
            item.UpdatedAtUnix = now;
            preflightCache.SetVerified(shortlist[0].NzbUrl, winnerBytes,
                PlaybackFastVerifier.Verdict.Available, responderHost);
            Log.Information("Watchtower: ready {Key} -> {Title} ({Size} bytes, {Count} pointer(s))",
                item.Key, shortlist[0].Title, shortlist[0].Size, shortlist.Count);
            if (knownDead + verifiedDead + timedOut > 0)
                LogActivity(
                    "Watchtower: {Key} readied after skipping {KnownDead} known-dead, {VerifiedDead} verified-dead, {TimedOut} timed-out candidate(s)",
                    item.Key, knownDead, verifiedDead, timedOut);
        }
        else
        {
            if (await TryParkForEpisodeFallbackAsync(ctx, item, candidates.Count, now, ct).ConfigureAwait(false))
                return;
            item.State = WantedItem.StateUnavailable;
            item.Shortlist = "[]";
            item.WinnerNzb = null;
            item.FailReason = candidates.Count == 0 ? "No releases found" : "No healthy release found";
            item.NextCheckAtUnix = now + configManager.GetWatchtowerUnavailableRetrySeconds();
            item.UpdatedAtUnix = now;
            LogActivity(
                "Watchtower: unavailable {Key} ({Reason}) — {Candidates} candidate(s), {KnownDead} known-dead, {VerifiedDead} verified-dead, {TimedOut} timed-out",
                item.Key, item.FailReason, candidates.Count, knownDead, verifiedDead, timedOut);
        }
    }

    private async Task<bool> TryParkForEpisodeFallbackAsync(
        DavDatabaseContext ctx, WantedItem item, int candidateCount, long now, CancellationToken ct)
    {
        if (item.Type != "season") return false;
        if (!configManager.IsWatchtowerSeasonBundleFallbackEnabled()) return false;

        var parts = item.ContentId.Split(':');
        if (parts.Length < 2) return false;
        var imdb = parts[0];
        if (!int.TryParse(parts[^1], out var season)) return false;
        if (!await SeasonEligibleForFallbackAsync(ctx, imdb, season, ct).ConfigureAwait(false)) return false;

        item.State = WantedItem.StateParked;
        item.Shortlist = "[]";
        item.WinnerNzb = null;
        item.FailReason = candidateCount == 0
            ? "No season bundle found, using episodes"
            : "No healthy season bundle, using episodes";
        item.NextCheckAtUnix = null;
        item.UpdatedAtUnix = now;
        await NudgeParentExpanderAsync(ctx, item, now, ct).ConfigureAwait(false);
        Log.Information("Watchtower: parked season bundle {Key}; falling back to episodes", item.Key);
        return true;
    }

    private async Task<bool> SeasonEligibleForFallbackAsync(
        DavDatabaseContext ctx, string imdb, int season, CancellationToken ct)
    {
        var scope = configManager.GetWatchtowerSeasonBundleFallbackScope();
        if (scope == "all") return true;

        var prefix = imdb + ":";
        var ids = await ctx.WantedItems.AsNoTracking()
            .Where(w => w.Type == "season" && w.ContentId.StartsWith(prefix))
            .Select(w => w.ContentId)
            .ToListAsync(ct).ConfigureAwait(false);

        var maxSeason = season;
        foreach (var parts in ids.Select(id => id.Split(':')))
        {
            if (parts.Length >= 2 && int.TryParse(parts[^1], out var s) && s > maxSeason) maxSeason = s;
        }

        if (scope == "recent")
            return season > maxSeason - configManager.GetWatchtowerSeasonBundleFallbackRecentCount();
        return season == maxSeason;
    }

    private static async Task NudgeParentExpanderAsync(
        DavDatabaseContext ctx, WantedItem item, long now, CancellationToken ct)
    {
        var parentKey = WtJson.ReadStrings(item.Provenance)
            .FirstOrDefault(p => p.StartsWith("exp:", StringComparison.Ordinal));
        if (parentKey is null) return;
        parentKey = parentKey[4..];

        var parent = await ctx.WantedItems.FirstOrDefaultAsync(w => w.Key == parentKey, ct).ConfigureAwait(false);
        if (parent is null) return;
        parent.NextCheckAtUnix = now;
        parent.UpdatedAtUnix = now;
    }

    private async Task KeepFreshDueItemsAsync(CancellationToken ct)
    {
        var now = Now();
        await using var ctx = new DavDatabaseContext();
        var due = await ctx.WantedItems
            .Where(w => w.State == WantedItem.StateReady
                        && w.NextCheckAtUnix != null && w.NextCheckAtUnix <= now)
            .OrderBy(w => w.NextCheckAtUnix)
            .Take(KeepFreshPerTick)
            .ToListAsync(ct).ConfigureAwait(false);

        foreach (var item in due)
        {
            if (ct.IsCancellationRequested) break;
            await KeepFreshOneAsync(item, ct).ConfigureAwait(false);
            await TrySaveBatchItemAsync(ctx, ct).ConfigureAwait(false);
        }
    }

    private async Task KeepFreshOneAsync(WantedItem item, CancellationToken ct)
    {
        var now = Now();
        var shortlist = WtJson.ReadPointers(item.Shortlist);
        if (shortlist.Count == 0 || item.WinnerNzb is null)
        {
            item.State = WantedItem.StateScouting;
            item.NextCheckAtUnix = now;
            item.UpdatedAtUnix = now;
            return;
        }

        var sample = configManager.GetWatchtowerVerifySampleCount();

        PlaybackFastVerifier.VerifyOutcome outcome;
        using (var ms = new MemoryStream(item.WinnerNzb, writable: false))
            outcome = await fastVerifier
                .VerifyAsync(ms, "stat", sample, ct, VerifyTimeout)
                .ConfigureAwait(false);

        if (outcome.Verdict == PlaybackFastVerifier.Verdict.Available)
        {
            item.LastVerifiedAtUnix = now;
            item.ResponderHost = outcome.ResponderHost;
            shortlist[0].Verdict = "available";
            shortlist[0].LastVerifiedAtUnix = now;
            item.Shortlist = WtJson.WritePointers(shortlist);
            item.NextCheckAtUnix = now + NextBackoff(item, now);
            item.UpdatedAtUnix = now;
            preflightCache.SetVerified(shortlist[0].NzbUrl, item.WinnerNzb,
                PlaybackFastVerifier.Verdict.Available, outcome.ResponderHost);
            return;
        }

        if (outcome.Verdict == PlaybackFastVerifier.Verdict.Timeout)
        {
            item.NextCheckAtUnix = now + Math.Min(configManager.GetWatchtowerKeepFreshBaseSeconds(), 1800);
            item.UpdatedAtUnix = now;
            return;
        }

        LogActivity("Watchtower: re-check found winner dead for {Key} ({Title}); promoting backup",
            item.Key, shortlist[0].Title);
        negativeCache.MarkFailed(shortlist[0].NzbUrl);
        wardenStore.MarkDead(WardenFingerprint.Compute(shortlist[0].Size, shortlist[0].Poster, shortlist[0].UsenetDate));
        shortlist.RemoveAt(0);
        await PromoteBackupAsync(item, shortlist, now, ct).ConfigureAwait(false);
    }

    private async Task PromoteBackupAsync(WantedItem item, List<WtPointer> shortlist, long now, CancellationToken ct)
    {
        var sample = configManager.GetWatchtowerVerifySampleCount();

        while (shortlist.Count > 0 && !ct.IsCancellationRequested)
        {
            var ptr = shortlist[0];
            var bytes = await FetchNzbBytesAsync(MakeCandidate(ptr), ct).ConfigureAwait(false);
            if (bytes is null) { shortlist.RemoveAt(0); continue; }

            PlaybackFastVerifier.VerifyOutcome outcome;
            using (var ms = new MemoryStream(bytes, writable: false))
                outcome = await fastVerifier
                    .VerifyAsync(ms, "stat", sample, ct, VerifyTimeout)
                    .ConfigureAwait(false);

            if (outcome.Verdict == PlaybackFastVerifier.Verdict.Available)
            {
                ptr.Verdict = "available";
                ptr.LastVerifiedAtUnix = now;
                item.WinnerNzb = bytes;
                item.ResponderHost = outcome.ResponderHost;
                item.Shortlist = WtJson.WritePointers(shortlist);
                item.LastVerifiedAtUnix = now;
                item.NextCheckAtUnix = now + configManager.GetWatchtowerKeepFreshBaseSeconds();
                item.UpdatedAtUnix = now;
                preflightCache.SetVerified(ptr.NzbUrl, bytes,
                    PlaybackFastVerifier.Verdict.Available, outcome.ResponderHost);
                Log.Information("Watchtower: promoted backup for {Key} -> {Title}", item.Key, ptr.Title);
                return;
            }

            if (outcome.Verdict == PlaybackFastVerifier.Verdict.Dead)
            {
                negativeCache.MarkFailed(ptr.NzbUrl);
                wardenStore.MarkDead(WardenFingerprint.Compute(ptr.Size, ptr.Poster, ptr.UsenetDate));
            }
            shortlist.RemoveAt(0);
        }

        item.State = WantedItem.StateScouting;
        item.Shortlist = "[]";
        item.WinnerNzb = null;
        item.NextCheckAtUnix = now;
        item.UpdatedAtUnix = now;
        LogActivity("Watchtower: shortlist exhausted for {Key}; re-resolving", item.Key);
    }

    private Task<byte[]?> FetchNzbBytesAsync(NzbResolutionCache.Candidate c, CancellationToken ct)
    {
        var skipTlsVerification = configManager.GetIndexerConfig().ShouldSkipTlsVerification(c.IndexerName);
        return nzbFetchCoalescer.GetOrFetchAsync(c.NzbUrl, c.ProxyUrl, skipTlsVerification, async innerCt =>
        {
            try
            {
                var indexer = configManager.GetIndexerConfig().Indexers
                    .FirstOrDefault(x => x.Name == c.IndexerName);
                if (indexer is not null)
                {
                    var hitCheck = await hitTracker
                        .CheckAsync(c.IndexerName, IndexerApiHit.HitType.Download, indexer.DownloadLimit, indexer.HitLimitResetTime, innerCt)
                        .ConfigureAwait(false);
                    if (hitCheck is { Allowed: false })
                    {
                        Log.Information("Watchtower: NZB download skipped for {Indexer}: {Reason}",
                            c.IndexerName, IndexerHitTracker.FormatSkipReason(hitCheck, IndexerApiHit.HitType.Download));
                        return null;
                    }
                }

                await _indexerGate.WaitAsync(innerCt).ConfigureAwait(false);
                try
                {
                    if (indexer is not null)
                        await rateLimiter.WaitAsync(c.IndexerName, indexer.MaxRequestsPerMinute, innerCt).ConfigureAwait(false);

                    using var req = new HttpRequestMessage(HttpMethod.Get, c.NzbUrl);
                    req.Headers.TryAddWithoutValidation("User-Agent", c.IndexerUserAgent);
                    var client = ProxyHttpClientPool.GetClient(c.ProxyUrl, skipTlsVerification);
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(innerCt);
                    cts.CancelAfter(NzbFetchTimeout);
                    using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode)
                    {
                        LogActivity("Watchtower: NZB fetch HTTP {Status} from {Indexer} for {Url}",
                            (int)resp.StatusCode, c.IndexerName, c.NzbUrl);
                        return null;
                    }
                    byte[] bytes;
                    try
                    {
                        bytes = await HttpContentReadUtil
                            .ReadBoundedAsync(resp.Content, NzbFetchLimits.MaxResponseBytes, cts.Token)
                            .ConfigureAwait(false);
                    }
                    catch (NzbResponseTooLargeException e)
                    {
                        Log.Warning(
                            "Watchtower rejected oversized NZB response from {Url}. Limit: {Limit} bytes. Reason: {Reason}",
                            c.NzbUrl, e.MaxBytes, e.Message);
                        Log.Debug(e, "Watchtower oversized NZB response stack");
                        return null;
                    }
                    _ = hitTracker.RecordAsync(c.IndexerName, IndexerApiHit.HitType.Download, CancellationToken.None);
                    return bytes;
                }
                finally
                {
                    _indexerGate.Release();
                }
            }
            catch (OperationCanceledException) when (innerCt.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException)
            {
                LogActivity(e, "Watchtower: NZB fetch failed for {Url}", c.NzbUrl);
                return null;
            }
        }, ct);
    }

    private static NzbResolutionCache.Candidate MakeCandidate(WtPointer p) => new()
    {
        IndexerName = p.IndexerName,
        IndexerUserAgent = p.IndexerUserAgent,
        NzbUrl = p.NzbUrl,
        Title = p.Title,
        Size = p.Size,
        Grabs = p.Grabs,
        Poster = p.Poster,
        UsenetDate = p.UsenetDate,
        ProxyUrl = p.ProxyUrl,
    };

    private long NextBackoff(WantedItem item, long now)
    {
        var baseSec = configManager.GetWatchtowerKeepFreshBaseSeconds();
        var maxSec = configManager.GetWatchtowerKeepFreshMaxSeconds();
        var sinceResolved = item.LastResolvedAtUnix is { } r ? Math.Max(0, now - r) : baseSec;
        return Math.Clamp(sinceResolved, baseSec, maxSec);
    }

    private string? ResolveProfileToken()
    {
        var profiles = configManager.GetProfileConfig().Profiles;
        var configured = configManager.GetWatchtowerProfileToken();
        if (!string.IsNullOrEmpty(configured) && profiles.Any(p => p.Token == configured)) return configured;
        return profiles.FirstOrDefault()?.Token;
    }

    private void RollDailyBudget()
    {
        var dayKey = (int)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 86400);
        if (dayKey != _resolveDayKey)
        {
            _resolveDayKey = dayKey;
            _resolvesToday = 0;
        }
    }

    private TimeSpan VerifyTimeout =>
        TimeSpan.FromSeconds(configManager.GetWatchtowerVerifyTimeoutSeconds());

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    public override void Dispose()
    {
        lock (_ctsLock)
        {
            _disabledCts?.Dispose();
            _disabledCts = null;
        }
        _indexerGate.Dispose();
        GC.SuppressFinalize(this);
        base.Dispose();
    }

}
