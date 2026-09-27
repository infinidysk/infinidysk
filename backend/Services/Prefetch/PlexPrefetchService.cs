using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NzbWebDAV.Config;
using NzbWebDAV.Services.Plex;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services.NativeCache;
using NzbWebDAV.Utils;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NzbWebDAV.Services.Prefetch;

public sealed record PrefetchPrediction(Guid ItemId, string DisplayName, string Source, string Reason, long Start, long Length, long FileSize)
{
    public bool Eligible { get; init; } = true;
    public string? PlexRatingKey { get; init; }
}

public sealed class PlexPrefetchService(ConfigManager config, PlexApiClient api, PrefetchRuntime runtime,
    IServiceScopeFactory scopes, ActiveReadRegistry reads, PlexPlaybackRegistry? playback = null) : BackgroundService
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2213", Justification = "Managed-only semaphore: in-flight HTTP previews can release after hosted-service disposal. AvailableWaitHandle is never used.")]
    private readonly SemaphoreSlim _sync = new(1, 1);
    private readonly Dictionary<string, DateTimeOffset> _last = new(StringComparer.Ordinal);
    private readonly PlexPlaybackRegistry _playback = playback ?? new(TimeProvider.System);
    private int _requested;
    private int _remainingCandidates;
    private int _serverCursor;
    private string? _policyRevision;
    private string? _passRevision;
    private readonly Dictionary<string, IReadOnlyList<PlexDiscoveredServer>> _userResources = new(StringComparer.Ordinal);
    private List<PrefetchPrediction>? _preview;
    public DateTimeOffset? LastSuccess { get; private set; }
    public string? LastError { get; private set; }
    public void RequestSync() => Interlocked.Exchange(ref _requested, 1);

    public Task SyncAsync(bool force, CancellationToken ct) => RunAsync(force, null, ct);
    public async Task<IReadOnlyList<PrefetchPrediction>> PreviewAsync(CancellationToken ct)
    {
        var preview = new List<PrefetchPrediction>();
        await RunAsync(true, preview, ct).ConfigureAwait(false);
        return preview;
    }

    private async Task RunAsync(bool force, List<PrefetchPrediction>? preview, CancellationToken ct)
    {
        if (!await _sync.WaitAsync(0, ct).ConfigureAwait(false))
        {
            if (preview is not null) throw new ArgumentException("A policy refresh is already running. Retry the preview shortly.");
            return;
        }
        _preview = preview;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(preview is null ? TimeSpan.FromMinutes(2) : TimeSpan.FromSeconds(20));
        try
        {
            if (runtime.RuntimeError is not null) { LastError = runtime.RuntimeError; return; }
            var settings = runtime.Settings();
            var servers = PlexSettings.ParseServers(config.GetEffectiveConfigValue(ConfigKeys.PlexServers));
            if (preview is null) _playback.RetainServers(settings.Enabled && settings.RealtimeEnabled
                ? servers.Where(server => server.Enabled).Select(server => server.Id).ToArray() : []);
            var revision = ConfigurationRevision(settings, servers);
            _passRevision = revision;
            _userResources.Clear();
            if (preview is null && _policyRevision != revision)
            {
                _last.Clear();
                if (_policyRevision is not null) runtime.Coordinator?.PruneOwners(owner => owner == "manual");
                _policyRevision = revision;
            }
            await runtime.WaitForInitializationAsync(deadline.Token).ConfigureAwait(false);
            var jobs = runtime.Jobs;
            if (jobs is null) return;
            if (preview is null) runtime.Coordinator!.PruneOwners(owner => IsOwnerEnabled(owner, settings, servers));
            if (!settings.Enabled) return;
            LastError = null;
            _remainingCandidates = preview is null ? 2048 : 100;
            var enabled = servers.Where(server => server.Enabled).ToArray();
            for (var index = 0; index < enabled.Length; index++)
            {
                var server = enabled[(_serverCursor + index) % enabled.Length];
                try { await SyncServerAsync(server, settings, force, deadline.Token).ConfigureAwait(false); }
                catch (PlexRequestException) { LastError = "A Plex server could not be refreshed. Other servers and cached content remain available."; }
                finally { if (index == enabled.Length - 1) _serverCursor = (_serverCursor + 1) % Math.Max(1, enabled.Length); }
            }
            if (settings.ReadActivityEnabled)
            {
                // Raw reads are low-confidence hints only. They never enter the verified Plex registry.
                using var scope = scopes.CreateScope();
                var database = scope.ServiceProvider.GetRequiredService<DavDatabaseClient>();
                foreach (var read in reads.Snapshot().Where(read => read.QualifiesForWarming(DateTimeOffset.UtcNow)).Take(32))
                {
                    var item = await ResolveDavAsync(database, read.Path, deadline.Token).ConfigureAwait(false);
                    if (item is not null) QueueImported(item, "read", 5, 0, 0, settings);
                }
            }
            if (preview is null) LastSuccess = DateTimeOffset.UtcNow;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { LastError = "Policy refresh reached its bounded time window; remaining sources will be retried."; _serverCursor++; }
        catch (Exception exception) when (exception is IOException or Microsoft.Data.Sqlite.SqliteException or InvalidOperationException)
        { runtime.ReportMetadataFailure(); LastError = runtime.RuntimeError; }
        finally { _preview = null; _sync.Release(); }
    }

    private async Task SyncServerAsync(PlexServer server, PrefetchSettings settings, bool force, CancellationToken ct)
    {
        if (settings.RealtimeEnabled && Due("sessions:" + server.Id, TimeSpan.FromSeconds(settings.RealtimeCheckIntervalSeconds), force))
        {
            try
            {
            var sessions = await api.GetSessionsAsync(server, ct).ConfigureAwait(false);
            if (_preview is null) _playback.Record(server.Id, sessions, TimeSpan.FromSeconds(settings.VerifiedSessionExpirySeconds));
            foreach (var session in sessions.Where(session => session.State is "playing" or "paused"
                && UserSelected(settings, server.Id, session.UserId)).Take(32))
            {
                var owner = Owner(server.Id, "realtime", session.UserId);
                if (session.Item.ViewOffset > 0) await QueueMediaAsync(server, session.Item, owner, 80, settings, null, ct).ConfigureAwait(false);
                if (settings.PredictionsEnabled && session.Item.Type == "episode")
                    await QueueNextAsync(server, session.Item, Owner(server.Id, "realtime-next", session.UserId), 90, settings, null, ct).ConfigureAwait(false);
            }
            }
            catch (PlexRequestException) { LastError = "Plex playback could not be refreshed; other source policies remain available."; }
        }
        if (settings.HistoryEnabled && Due("history:" + server.Id, TimeSpan.FromMinutes(settings.SyncIntervalMinutes), force))
        {
            try
            {
            var history = await api.GetHistoryAsync(server, DateTimeOffset.UtcNow.AddDays(-settings.LookbackDays), 1000, ct).ConfigureAwait(false);
            foreach (var item in PrefetchPolicy.HistoryCandidates(history, settings, server.Id, DateTimeOffset.UtcNow))
            {
                var owner = Owner(server.Id, "history", item.UserId ?? "");
                if (item.Type == "movie" || item.ViewOffset > 0) await QueueMediaAsync(server, item, owner, 40, settings, null, ct).ConfigureAwait(false);
                if (settings.PredictionsEnabled && item.Type == "episode")
                    await QueueNextAsync(server, item, Owner(server.Id, "history-next", item.UserId ?? ""), 50, settings, null, ct).ConfigureAwait(false);
            }
            }
            catch (PlexRequestException) { LastError = "Plex history could not be refreshed; other source policies remain available."; }
        }
        foreach (var source in settings.Sources.Where(source => source.Enabled && source.ServerId == server.Id
            && !settings.IsLibraryDisabled(source)))
        {
            if (_remainingCandidates <= 0) break;
            ct.ThrowIfCancellationRequested();
            if (source.Type == "movie" ? !settings.MovieEnabled : !settings.TvEnabled) continue;
            var owner = Owner(server.Id, "source", source.Kind + ":" + source.Key);
            var interval = source.Type == "movie" ? settings.MovieSyncIntervalMinutes : settings.TvSyncIntervalMinutes;
            if (!Due(owner, TimeSpan.FromMinutes(interval), force)) continue;
            try
            {
                var items = await api.GetPreviewAsync(server, source.Key, source.Limit, ct).ConfigureAwait(false);
                foreach (var item in items.Take(source.Limit))
                {
                    ct.ThrowIfCancellationRequested();
                    if (!PrefetchPolicy.IsEligible(item, settings, source)) { RejectPreview(item, owner, "Excluded show or disabled media policy."); continue; }
                    if (item.Type == "show")
                    {
                        var users = settings.Users.Where(user => user.StartsWith(server.Id + ":", StringComparison.Ordinal))
                            .Select(user => user[(server.Id.Length + 1)..]).Take(16).ToArray();
                        if (users.Length == 0) users = [server.AccountId ?? ""];
                        foreach (var user in users)
                            await QueueNextAsync(server, item with { UserId = user }, owner, 20, settings, source, ct).ConfigureAwait(false);
                    }
                    else await QueueMediaAsync(server, item, owner, 20, settings, source, ct).ConfigureAwait(false);
                }
            }
            catch (PlexRequestException) { LastError = "A selected Plex source could not be refreshed; its previous cache is retained."; }
        }
    }

    private async Task QueueNextAsync(PlexServer server, PlexMediaItem current, string owner, int priority,
        PrefetchSettings settings, PrefetchSource? source, CancellationToken ct)
    {
        if (_remainingCandidates <= 0 || !settings.TvEnabled || !PrefetchPolicy.IsEligible(current, settings, source)) return;
        var show = current.ShowRatingKey ?? (current.Type == "show" ? current.RatingKey : null);
        if (show is null) return;
        var userServer = await UserServerAsync(server, current.UserId, ct).ConfigureAwait(false);
        if (userServer is null) LastError = "Watched status is unknown for a selected user. Connect that Plex/Home account to verify next-unwatched predictions; chronological candidates remain available.";
        var episodes = await api.GetNextEpisodesAsync(userServer ?? server, current, 20, ct).ConfigureAwait(false);
        var remaining = Math.Min(settings.MaxQueueAhead, settings.TvEpisodesPerShow);
        foreach (var item in PrefetchPolicy.NextEpisodes(current, episodes, 20))
            if (await QueueMediaAsync(server, item, owner, priority, settings, source, ct, prediction: true).ConfigureAwait(false) && --remaining == 0) break;
    }

    private async Task<PlexServer?> UserServerAsync(PlexServer server, string? user, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(user)) return null;
        if (server.AccountId == user) return server;
        var account = PlexSettings.ParseAccounts(config.GetEffectiveConfigValue(ConfigKeys.PlexAccounts)).FirstOrDefault(account => account.Id == user);
        if (account is null) return null;
        if (!_userResources.TryGetValue(user, out var resources))
        {
            try { resources = await api.DiscoverAsync(account.Token, ct).ConfigureAwait(false); }
            catch (PlexRequestException) { resources = []; }
            _userResources[user] = resources;
        }
        var resource = resources.FirstOrDefault(resource => resource.Id == server.Id);
        return resource is null ? null : server with { AccountId = user, Token = resource.Token };
    }

    private string ConfigurationRevision(PrefetchSettings settings, IReadOnlyList<PlexServer> servers) =>
        Hash(JsonSerializer.Serialize(settings) + JsonSerializer.Serialize(servers) + config.GetEffectiveConfigValue(ConfigKeys.PlexAccounts));

    private bool RejectPreview(PlexMediaItem item, string owner, string reason)
    {
        if (_preview is { Count: < 100 } preview)
            preview.Add(new(Guid.Empty, item.Title, SourceLabel(owner), reason, 0, 0, 0)
                { Eligible = false, PlexRatingKey = item.RatingKey });
        return false;
    }

    private async Task<bool> QueueMediaAsync(PlexServer server, PlexMediaItem item, string owner, int priority,
        PrefetchSettings settings, PrefetchSource? source, CancellationToken ct, bool prediction = false)
    {
        if (--_remainingCandidates < 0) return false;
        if (!PrefetchPolicy.IsEligible(item, settings, source)) return RejectPreview(item, owner, "Excluded show or disabled media policy.");
        if (item.File is null)
        {
            var details = await api.GetMetadataAsync(server, item.RatingKey, ct).ConfigureAwait(false);
            item = details with { ViewOffset = item.ViewOffset, Duration = item.Duration > 0 ? item.Duration : details.Duration,
                UserId = item.UserId, ViewedAt = item.ViewedAt, ShowRatingKey = details.ShowRatingKey ?? item.ShowRatingKey,
                Season = details.Season ?? item.Season, Episode = details.Episode ?? item.Episode, WatchStateUserId = item.WatchStateUserId };
        }
        if (item.File is null || PrefetchPathResolver.Map(item.File, server.PathMappings) is not { } mapped)
            return RejectPreview(item, owner, "No exact configured path mapping for this Plex media.");
        using var scope = scopes.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<DavDatabaseClient>();
        DavItem? imported = null;
        if (mapped.DavPath is { } dav) imported = await ResolveDavAsync(database, dav, ct).ConfigureAwait(false);
        else if (mapped.LocalPath is { } local)
        {
            if (!settings.WarmLocalFiles) return RejectPreview(item, owner, "Mapped local-library warming is disabled.");
            try
            {
                var link = SymlinkAndStrmUtil.GetSymlinkOrStrmInfo(new FileInfo(local));
                var target = link switch
                {
                    SymlinkAndStrmUtil.SymlinkInfo symlink => OrganizedLinksUtil.GetDavItemLink(symlink, config.GetRcloneMountDir()),
                    SymlinkAndStrmUtil.StrmInfo strm => OrganizedLinksUtil.GetDavItemLink(strm),
                    _ => null
                };
                if (target is { } match) imported = await database.GetFileById(match.DavItemId.ToString()).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
            { return RejectPreview(item, owner, "Local mapping is unavailable or does not resolve to imported media."); }
        }
        var scopedOwner = owner + ":" + item.Type + ":" + Hash(item.ShowRatingKey ?? item.RatingKey)
            + (mapped.LocalPath is null ? "" : ":local")
            + (prediction ? item.WatchStateUserId is null ? ":watch-unknown" : ":unwatched" : "");
        if (imported is null) return RejectPreview(item, owner, "Mapping does not resolve to an imported media file.");
        if (imported.FileSize is not > 0 || imported.FileSize > runtime.Settings().MaxBytesPerItem
            || imported.FileSize < NativeCacheSettings.MinimumFileBytes(config) || imported.FileBlobId is null)
            return RejectPreview(item, owner, "Imported media is unavailable or exceeds the per-file warming cap.");
        return QueueImported(imported, scopedOwner, priority, item.ViewOffset, item.Duration, settings);
    }

    private bool QueueImported(DavItem item, string owner, int priority, long viewOffset, long duration, PrefetchSettings settings)
    {
        var current = runtime.Settings();
        var servers = PlexSettings.ParseServers(config.GetEffectiveConfigValue(ConfigKeys.PlexServers));
        if (_passRevision != ConfigurationRevision(current, servers))
        {
            LastError = "Plex policy or mappings changed during refresh. Stale candidates were skipped; another refresh is requested.";
            RequestSync();
            return false;
        }
        if (item.FileSize is not > 0 || item.FileSize > current.MaxBytesPerItem
            || item.FileSize < NativeCacheSettings.MinimumFileBytes(config) || item.FileBlobId is null
            || !IsOwnerEnabled(owner, current, servers)) return false;
        if (_preview is { } preview)
        {
            foreach (var range in PrefetchPolicy.Ranges(item.FileSize.Value, viewOffset, duration, current, minimum: false))
                if (preview.Count < 100) preview.Add(new(item.Id, item.Name, SourceLabel(owner),
                    RangeReason(owner, range.Length), range.Start, range.Length, item.FileSize.Value));
            if (current.MinimumWarmEnabled && !current.FullFileWarming)
                foreach (var range in PrefetchPolicy.Ranges(item.FileSize.Value, 0, 0, current, minimum: true))
                    if (preview.Count < 100) preview.Add(new(item.Id, item.Name, SourceLabel(owner), "Minimum head/tail", range.Start, range.Length, item.FileSize.Value));
            return true;
        }
        var cooldownKey = "queued:" + item.Id.ToString("N") + ":" + owner;
        if (_last.TryGetValue(cooldownKey, out var previous) && DateTimeOffset.UtcNow - previous < TimeSpan.FromMinutes(current.CooldownMinutes)) return true;
        var accepted = false;
        foreach (var range in PrefetchPolicy.Ranges(item.FileSize.Value, viewOffset, duration, current, minimum: false))
        {
            try { runtime.Jobs!.Enqueue(item.Id, owner, priority, range.Start, range.Length); accepted = true; }
            catch (ArgumentException) { return accepted; }
        }
        if (current.MinimumWarmEnabled && !current.FullFileWarming)
            foreach (var range in PrefetchPolicy.Ranges(item.FileSize.Value, 0, 0, current, minimum: true))
                try { runtime.Jobs!.Enqueue(item.Id, owner + ":minimum", priority - 5, range.Start, range.Length); }
                catch (ArgumentException) { break; }
        if (accepted) Due(cooldownKey, TimeSpan.Zero, force: true);
        return accepted;
    }

    private static Task<DavItem?> ResolveDavAsync(DavDatabaseClient database, string path, CancellationToken ct)
    {
        var normalized = path.StartsWith("/view/", StringComparison.Ordinal) ? path[5..] : path;
        if (normalized.StartsWith("/.ids/", StringComparison.Ordinal) && Guid.TryParse(Path.GetFileNameWithoutExtension(normalized), out var id))
            return database.GetFileById(id.ToString());
        return database.GetItemByPathAsync(normalized, ct);
    }

    private bool Due(string key, TimeSpan interval, bool force)
    {
        if (_preview is not null) return true;
        var now = DateTimeOffset.UtcNow;
        if (!force && _last.TryGetValue(key, out var previous) && now - previous < interval) return false;
        if (_last.Count >= 2048 && !_last.ContainsKey(key)) _last.Remove(_last.MinBy(pair => pair.Value).Key);
        _last[key] = now;
        return true;
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16];
    public static string SourceLabel(string owner) => owner.Split(':') switch
    {
        ["manual"] => "Manual",
        ["read", ..] => "Read activity (unverified)",
        ["plex", _, "source", ..] => "Selected Plex hub/collection",
        ["plex", _, "realtime-next", ..] => "Plex playback prediction",
        ["plex", _, "history-next", ..] => "Plex history prediction",
        ["plex", _, "realtime", ..] => "Verified Plex playback",
        ["plex", _, "history", ..] => "Plex watch history",
        _ => "Background warming"
    };
    public static string RangeReason(string owner, long length) =>
        (owner.EndsWith(":minimum", StringComparison.Ordinal) ? "Minimum head/tail" : length == 0 ? "Whole-file warming" : "Resume/start range")
        + (owner.Contains(":watch-unknown", StringComparison.Ordinal) ? "; watched status unknown"
            : owner.Contains(":unwatched", StringComparison.Ordinal) ? "; next unwatched episode" : "");
    private static string Owner(string server, string kind, string key) => $"plex:{Hash(server)}:{kind}:{Hash(key)}";
    private static bool UserSelected(PrefetchSettings settings, string server, string user) => settings.Users.Length == 0
        || settings.Users.Contains(server + ":" + user, StringComparer.Ordinal);
    private static bool IsOwnerEnabled(string owner, PrefetchSettings settings, IReadOnlyList<PlexServer> servers)
    {
        if (owner == "manual") return true;
        if (!settings.Enabled) return false;
        if (owner == "read") return settings.ReadActivityEnabled;
        if (owner == "read:minimum") return settings.ReadActivityEnabled && settings.MinimumWarmEnabled;
        var parts = owner.Split(':');
        if (parts.Length is < 6 or > 9 || parts[0] != "plex") return false;
        foreach (var qualifier in parts.Skip(6))
            if (!(qualifier switch { "minimum" => settings.MinimumWarmEnabled, "local" => settings.WarmLocalFiles,
                "unwatched" or "watch-unknown" => true, _ => false })) return false;
        if (parts[4] == "movie" ? !settings.MovieEnabled : !settings.TvEnabled) return false;
        var server = servers.FirstOrDefault(server => server.Enabled && Hash(server.Id) == parts[1]);
        if (server is null) return false;
        return parts[2] switch
        {
            "source" => settings.Sources.Any(source => source.Enabled && source.ServerId == server.Id && Hash(source.Kind + ":" + source.Key) == parts[3]
                && !source.ExcludedShows.Any(show => Hash(show) == parts[5])),
            "realtime" or "realtime-next" => settings.RealtimeEnabled && (parts[2] == "realtime" || settings.PredictionsEnabled)
                && (settings.Users.Length == 0 || settings.Users.Any(user => user.StartsWith(server.Id + ":", StringComparison.Ordinal) && Hash(user[(server.Id.Length + 1)..]) == parts[3])),
            "history" or "history-next" => settings.HistoryEnabled && (parts[2] == "history" || settings.PredictionsEnabled)
                && (settings.Users.Length == 0 || settings.Users.Any(user => user.StartsWith(server.Id + ":", StringComparison.Ordinal) && Hash(user[(server.Id.Length + 1)..]) == parts[3])),
            _ => false
        };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await SyncAsync(Interlocked.Exchange(ref _requested, 0) != 0, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception) when (exception is not OutOfMemoryException) { LastError = "Policy refresh failed. Check Plex configuration and imported path mappings."; }
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
        }
    }
}
