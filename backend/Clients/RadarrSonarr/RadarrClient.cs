using System.Collections.Concurrent;
using System.Net;
using NzbWebDAV.Clients.RadarrSonarr.BaseModels;
using NzbWebDAV.Clients.RadarrSonarr.RadarrModels;

namespace NzbWebDAV.Clients.RadarrSonarr;

public class RadarrClient(string host, string apiKey) : ArrClient(host, apiKey)
{
    private static readonly ConcurrentDictionary<(string Host, string Path), int>
        SymlinkOrStrmToMovieIdCache = new();

    public Task<RadarrMovie> GetMovieAsync(int id, CancellationToken ct = default) =>
        Get<RadarrMovie>($"/movie/{id}", ct);

    private Task<RadarrMovie?> GetMovieOrNullAsync(int id, CancellationToken ct = default) =>
        GetOrNull<RadarrMovie>($"/movie/{id}", ct);

    public Task<List<RadarrMovie>> GetMoviesAsync(CancellationToken ct = default) =>
        Get<List<RadarrMovie>>($"/movie", ct);

    public Task<RadarrQueue> GetRadarrQueueAsync() =>
        Get<RadarrQueue>($"/queue?protocol=usenet&pageSize=5000");

    public Task<HttpStatusCode> DeleteMovieFile(int id, CancellationToken ct = default) =>
        Delete($"/moviefile/{id}", ct: ct);

    public override async Task<ArrRepairOutcome> RemoveAndBlocklist(
        string symlinkOrStrmPath,
        Guid downloadId,
        CancellationToken ct = default)
    {
        var movieFileId = await GetMovieFileId(symlinkOrStrmPath, ct).ConfigureAwait(false);
        if (movieFileId == null) return ArrRepairOutcome.MediaItemNotFound;

        var historyId = await GetHistoryRecordId(downloadId, ct).ConfigureAwait(false);
        if (historyId == null) return ArrRepairOutcome.DownloadHistoryNotFound;

        if (!Is2xx(await DeleteMovieFile(movieFileId.Value, ct).ConfigureAwait(false)))
            throw new InvalidOperationException($"Failed to delete movie file `{symlinkOrStrmPath}` from radarr instance `{Host}`.");

        await MarkHistoryFailed(historyId.Value, ct).ConfigureAwait(false);
        return ArrRepairOutcome.RemoveAndBlocklistSucceeded;
    }

    private async Task<int?> GetMovieFileId(string symlinkOrStrmPath, CancellationToken ct)
    {
        var cacheKey = (Host, symlinkOrStrmPath);

        // if we already have the movie-id cached
        // then let's use it to find and return the corresponding movie-file-id
        if (SymlinkOrStrmToMovieIdCache.TryGetValue(cacheKey, out var movieId))
        {
            var movie = await GetMovieOrNullAsync(movieId, ct).ConfigureAwait(false);
            var movieFile = movie?.MovieFile;
            if (movieFile is not null && movieFile.Path == symlinkOrStrmPath)
                return movieFile.Id;
            SymlinkOrStrmToMovieIdCache.TryRemove(cacheKey, out _);
        }

        // otherwise, let's fetch all movies, cache all movie files
        // and return the matching movie-id and movie-file-id
        var allMovies = await GetMoviesAsync(ct).ConfigureAwait(false);
        int? result = null;
        foreach (var movie in allMovies)
        {
            var movieFile = movie.MovieFile;
            if (movieFile?.Path != null)
                SymlinkOrStrmToMovieIdCache[(Host, movieFile.Path)] = movie.Id;
            if (movieFile is not null && movieFile.Path == symlinkOrStrmPath)
                result = movieFile.Id;
        }

        return result;
    }
}
