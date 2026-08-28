using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using NzbWebDAV.Clients.RadarrSonarr.BaseModels;
using NzbWebDAV.Clients.RadarrSonarr.SonarrModels;
using NzbWebDAV.Utils;
using Serilog;

namespace NzbWebDAV.Clients.RadarrSonarr;

public class SonarrClient(string host, string apiKey) : ArrClient(host, apiKey)
{
    private static readonly ConcurrentDictionary<(string Host, string Path), int>
        SeriesPathToSeriesIdCache = new();
    private static readonly ConcurrentDictionary<(string Host, string Path), int>
        SymlinkOrStrmToEpisodeFileIdCache = new();

    public Task<SonarrQueue> GetSonarrQueueAsync(CancellationToken ct = default) =>
        Get<SonarrQueue>($"/queue?protocol=usenet&pageSize=5000", ct);

    public override async Task<ArrQueue<ArrQueueRecord>> GetQueueAsync(CancellationToken ct = default) =>
        (await GetSonarrQueueAsync(ct).ConfigureAwait(false)).ToGeneric();

    public Task<List<SonarrSeries>> GetAllSeries(CancellationToken ct = default) =>
        Get<List<SonarrSeries>>($"/series", ct);

    public Task<SonarrSeries> GetSeries(int seriesId, CancellationToken ct = default) =>
        Get<SonarrSeries>($"/series/{seriesId}", ct);

    private Task<SonarrSeries?> GetSeriesOrNull(int seriesId, CancellationToken ct = default) =>
        GetOrNull<SonarrSeries>($"/series/{seriesId}", ct);

    public Task<SonarrEpisodeFile> GetEpisodeFile(int episodeFileId, CancellationToken ct = default) =>
        Get<SonarrEpisodeFile>($"/episodefile/{episodeFileId}", ct);

    private Task<SonarrEpisodeFile?> GetEpisodeFileOrNull(int episodeFileId, CancellationToken ct = default) =>
        GetOrNull<SonarrEpisodeFile>($"/episodefile/{episodeFileId}", ct);

    public Task<List<SonarrEpisodeFile>> GetAllEpisodeFiles(int seriesId, CancellationToken ct = default) =>
        Get<List<SonarrEpisodeFile>>($"/episodefile?seriesId={seriesId}", ct);

    public Task<List<SonarrEpisode>> GetEpisodesFromEpisodeFileId(
        int episodeFileId,
        CancellationToken ct = default) =>
        Get<List<SonarrEpisode>>($"/episode?episodeFileId={episodeFileId}", ct);

    public Task<HttpStatusCode> DeleteEpisodeFile(int episodeFileId, CancellationToken ct = default) =>
        Delete($"/episodefile/{episodeFileId}", ct: ct);

    public override async Task<ArrMediaFileMatch?> FindMediaFileAsync(
        string symlinkOrStrmPath,
        CancellationToken ct = default)
    {
        var episodeFileId = await GetEpisodeFileId(symlinkOrStrmPath, ct).ConfigureAwait(false);
        if (episodeFileId is null)
            return null;

        var episodeIds = (await GetEpisodesFromEpisodeFileId(episodeFileId.Value, ct).ConfigureAwait(false))
            .Select(episode => episode.Id)
            .Distinct()
            .ToArray();
        return new ArrMediaFileMatch(
            ArrMediaKind.Episode,
            episodeFileId.Value,
            episodeIds);
    }

    public override async Task<ArrMissingPayloadCleanupOutcome> RemoveMissingPayloadAndSearchAsync(
        ArrMediaFileMatch match,
        Func<IReadOnlyList<string>, bool>? shouldRequestSearch = null,
        CancellationToken ct = default)
    {
        if (match.Kind != ArrMediaKind.Episode)
            throw new ArgumentException("Sonarr cleanup requires an episode match.", nameof(match));

        if (!Is2xx(await DeleteEpisodeFile(match.FileId, ct).ConfigureAwait(false)))
            throw new InvalidOperationException(
                $"Failed to delete episode file {match.FileId} from sonarr instance '{Host}'.");

        if (match.MediaIds.Count == 0)
            return ArrMissingPayloadCleanupOutcome.RemovedNoSearchTargets;
        if (shouldRequestSearch is not null && !shouldRequestSearch(match.MediaKeys))
            return ArrMissingPayloadCleanupOutcome.RemovedSearchWithheld;

        try
        {
            await ExecuteWithTransientRetryAsync(
                token => CommandAsync(
                    new { name = "EpisodeSearch", episodeIds = match.MediaIds },
                    token),
                ct).ConfigureAwait(false);
            return ArrMissingPayloadCleanupOutcome.RemovedSearchRequested;
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            return LogMissingPayloadSearchFailure(ex, match.FileId);
        }
        catch (OperationCanceledException) { throw; }
        catch (HttpRequestException ex)
        {
            return LogMissingPayloadSearchFailure(ex, match.FileId);
        }
        catch (InvalidDataException ex)
        {
            return LogMissingPayloadSearchFailure(ex, match.FileId);
        }
        catch (JsonException ex)
        {
            return LogMissingPayloadSearchFailure(ex, match.FileId);
        }
    }

    private ArrMissingPayloadCleanupOutcome LogMissingPayloadSearchFailure(
        Exception exception,
        int episodeFileId)
    {
        Log.Warning(
            "Sonarr missing-payload cleanup on {Host}: episode file {EpisodeFileId} was removed, " +
            "but replacement search failed. Reason: {Reason}",
            Host,
            episodeFileId,
            exception.Message);
        Log.Debug(exception, "Sonarr missing-payload replacement-search failure stack");
        return ArrMissingPayloadCleanupOutcome.RemovedSearchFailed;
    }

    public override async Task<ArrRepairOutcome> RemoveAndBlocklist(
        string symlinkOrStrmPath,
        Guid downloadId,
        Func<IReadOnlyList<string>, bool>? shouldRequestSearch = null,
        CancellationToken ct = default)
    {
        var episodeFileId = await GetEpisodeFileId(symlinkOrStrmPath, ct).ConfigureAwait(false);
        if (episodeFileId == null) return ArrRepairOutcome.MediaItemNotFound;

        var historyId = await GetHistoryRecordId(downloadId, ct).ConfigureAwait(false);
        if (historyId == null) return ArrRepairOutcome.DownloadHistoryNotFound;

        List<int> episodeIds;
        try
        {
            episodeIds = (await GetEpisodesFromEpisodeFileId(episodeFileId.Value, ct).ConfigureAwait(false))
                .Select(episode => episode.Id)
                .ToList();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Warning(
                ex,
                "Sonarr repair on {Host}: could not resolve episodes for episode file {EpisodeFileId}; repair will continue without explicit search",
                Host,
                episodeFileId.Value);
            episodeIds = [];
        }

        if (!Is2xx(await DeleteEpisodeFile(episodeFileId.Value, ct).ConfigureAwait(false)))
            throw new InvalidOperationException($"Failed to delete episode file `{symlinkOrStrmPath}` from sonarr instance `{Host}`.");

        await MarkHistoryFailed(historyId.Value, ct).ConfigureAwait(false);

        try
        {
            if (episodeIds.Count == 0)
            {
                Log.Warning(
                    "Sonarr repair on {Host}: no episodes linked to episode file {EpisodeFileId}; skipping EpisodeSearch",
                    Host,
                    episodeFileId.Value);
            }
            else if (shouldRequestSearch is not null &&
                     !shouldRequestSearch(episodeIds.Select(id => $"episode:{id}").ToArray()))
            {
                Log.Warning(
                    "Sonarr repair on {Host}: automatic replacement-search limit reached for episode file {EpisodeFileId}; " +
                    "the file was removed and its download blocklisted without starting another search.",
                    Host,
                    episodeFileId.Value);
                return ArrRepairOutcome.RemoveAndBlocklistSucceededSearchWithheld;
            }
            else
            {
                await ExecuteWithTransientRetryAsync(
                    ct => CommandAsync(new { name = "EpisodeSearch", episodeIds }, ct),
                    ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Warning(
                ex,
                "Sonarr repair on {Host}: failed to request EpisodeSearch for episode file {EpisodeFileId}",
                Host,
                episodeFileId.Value);
        }

        return ArrRepairOutcome.RemoveAndBlocklistSucceeded;
    }

    private async Task<int?> GetEpisodeFileId(string symlinkOrStrmPath, CancellationToken ct)
    {
        var cacheKey = (Host, symlinkOrStrmPath);

        // if episode-file-id is found in the cache, verify it and return it
        if (SymlinkOrStrmToEpisodeFileIdCache.TryGetValue(cacheKey, out var episodeFileId))
        {
            var episodeFile = await GetEpisodeFileOrNull(episodeFileId, ct).ConfigureAwait(false);
            if (episodeFile?.Path == symlinkOrStrmPath) return episodeFileId;
            SymlinkOrStrmToEpisodeFileIdCache.TryRemove(cacheKey, out _);
        }

        // otherwise, find the series-id
        var seriesId = await GetSeriesId(symlinkOrStrmPath, ct).ConfigureAwait(false);
        if (seriesId == null) return null;

        // then use it to find all episode-files and repopulate the cache
        int? result = null;
        foreach (var episodeFile in await GetAllEpisodeFiles(seriesId.Value, ct).ConfigureAwait(false))
        {
            SymlinkOrStrmToEpisodeFileIdCache[(Host, episodeFile.Path!)] = episodeFile.Id;
            if (episodeFile.Path == symlinkOrStrmPath)
                result = episodeFile.Id;
        }

        // return the found episode-file-id
        return result;
    }

    private async Task<int?> GetSeriesId(string symlinkOrStrmPath, CancellationToken ct)
    {
        // get series-id from cache
        string? cachedSeriesPath = null;
        var cachedSeriesId = 0;
        foreach (var parentPath in PathUtil.GetAllParentDirectories(symlinkOrStrmPath))
        {
            if (!SeriesPathToSeriesIdCache.TryGetValue((Host, parentPath), out cachedSeriesId))
                continue;

            cachedSeriesPath = parentPath;
            break;
        }

        // if found, verify and return it
        if (cachedSeriesPath != null)
        {
            var series = await GetSeriesOrNull(cachedSeriesId, ct).ConfigureAwait(false);
            if (series?.Path != null && symlinkOrStrmPath.StartsWith(series.Path, StringComparison.Ordinal))
                return cachedSeriesId;
            SeriesPathToSeriesIdCache.TryRemove((Host, cachedSeriesPath), out _);
        }

        // otherwise, fetch all series and repopulate the cache
        int? result = null;
        foreach (var series in await GetAllSeries(ct).ConfigureAwait(false))
        {
            SeriesPathToSeriesIdCache[(Host, series.Path!)] = series.Id;
            if (symlinkOrStrmPath.StartsWith(series.Path!, StringComparison.Ordinal))
                result = series.Id;
        }

        // return the found series-id
        return result;
    }
}
