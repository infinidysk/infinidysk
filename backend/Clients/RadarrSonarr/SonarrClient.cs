using System.Collections.Concurrent;
using System.Net;
using NzbWebDAV.Clients.RadarrSonarr.BaseModels;
using NzbWebDAV.Clients.RadarrSonarr.SonarrModels;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Clients.RadarrSonarr;

public class SonarrClient(string host, string apiKey) : ArrClient(host, apiKey)
{
    private static readonly ConcurrentDictionary<(string Host, string Path), int>
        SeriesPathToSeriesIdCache = new();
    private static readonly ConcurrentDictionary<(string Host, string Path), int>
        SymlinkOrStrmToEpisodeFileIdCache = new();

    public Task<SonarrQueue> GetSonarrQueueAsync() =>
        Get<SonarrQueue>($"/queue?protocol=usenet&pageSize=5000");

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

    public override async Task<ArrRepairOutcome> RemoveAndBlocklist(
        string symlinkOrStrmPath,
        Guid downloadId,
        CancellationToken ct = default)
    {
        var episodeFileId = await GetEpisodeFileId(symlinkOrStrmPath, ct).ConfigureAwait(false);
        if (episodeFileId == null) return ArrRepairOutcome.MediaItemNotFound;

        var historyId = await GetHistoryRecordId(downloadId, ct).ConfigureAwait(false);
        if (historyId == null) return ArrRepairOutcome.DownloadHistoryNotFound;

        if (await DeleteEpisodeFile(episodeFileId.Value, ct).ConfigureAwait(false) != HttpStatusCode.OK)
            throw new Exception($"Failed to delete episode file `{symlinkOrStrmPath}` from sonarr instance `{Host}`.");

        await MarkHistoryFailed(historyId.Value, ct).ConfigureAwait(false);
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
            if (series?.Path != null && symlinkOrStrmPath.StartsWith(series.Path))
                return cachedSeriesId;
            SeriesPathToSeriesIdCache.TryRemove((Host, cachedSeriesPath), out _);
        }

        // otherwise, fetch all series and repopulate the cache
        int? result = null;
        foreach (var series in await GetAllSeries(ct).ConfigureAwait(false))
        {
            SeriesPathToSeriesIdCache[(Host, series.Path!)] = series.Id;
            if (symlinkOrStrmPath.StartsWith(series.Path!))
                result = series.Id;
        }

        // return the found series-id
        return result;
    }
}
