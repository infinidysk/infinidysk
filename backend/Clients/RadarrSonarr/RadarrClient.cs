using System.Collections.Concurrent;
using System.Net;
using NzbWebDAV.Clients.RadarrSonarr.BaseModels;
using NzbWebDAV.Clients.RadarrSonarr.RadarrModels;
using Serilog;

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

    public Task<RadarrQueue> GetRadarrQueueAsync(CancellationToken ct = default) =>
        Get<RadarrQueue>($"/queue?protocol=usenet&pageSize=5000", ct);

    public override async Task<ArrQueue<ArrQueueRecord>> GetQueueAsync(CancellationToken ct = default) =>
        (await GetRadarrQueueAsync(ct).ConfigureAwait(false)).ToGeneric();

    public Task<HttpStatusCode> DeleteMovieFile(int id, CancellationToken ct = default) =>
        Delete($"/moviefile/{id}", ct: ct);

    public override async Task<ArrRepairOutcome> RemoveAndBlocklist(
        string symlinkOrStrmPath,
        Guid downloadId,
        Func<string, bool>? shouldRequestSearch = null,
        CancellationToken ct = default)
    {
        var movieIds = await GetMovieFileIds(symlinkOrStrmPath, ct).ConfigureAwait(false);
        if (movieIds == null) return ArrRepairOutcome.MediaItemNotFound;

        var historyId = await GetHistoryRecordId(downloadId, ct).ConfigureAwait(false);
        if (historyId == null) return ArrRepairOutcome.DownloadHistoryNotFound;

        if (!Is2xx(await DeleteMovieFile(movieIds.MovieFileId, ct).ConfigureAwait(false)))
            throw new InvalidOperationException($"Failed to delete movie file `{symlinkOrStrmPath}` from radarr instance `{Host}`.");

        await MarkHistoryFailed(historyId.Value, ct).ConfigureAwait(false);

        if (shouldRequestSearch is not null && !shouldRequestSearch($"movie:{movieIds.MovieId}"))
        {
            Log.Warning(
                "Radarr repair on {Host}: automatic replacement-search limit reached for movie {MovieId}; " +
                "the file was removed and its download blocklisted without starting another search.",
                Host,
                movieIds.MovieId);
            return ArrRepairOutcome.RemoveAndBlocklistSucceeded;
        }

        try
        {
            await ExecuteWithTransientRetryAsync(
                ct => CommandAsync(
                    new { name = "MoviesSearch", movieIds = new[] { movieIds.MovieId } },
                    ct),
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Warning(
                ex,
                "Radarr repair on {Host}: failed to request MoviesSearch for movie {MovieId}",
                Host,
                movieIds.MovieId);
        }

        return ArrRepairOutcome.RemoveAndBlocklistSucceeded;
    }

    private sealed record MovieFileIds(int MovieFileId, int MovieId);

    private async Task<MovieFileIds?> GetMovieFileIds(string symlinkOrStrmPath, CancellationToken ct)
    {
        var cacheKey = (Host, symlinkOrStrmPath);

        // if we already have the movie-id cached
        // then let's use it to find and return the corresponding movie-file-id
        if (SymlinkOrStrmToMovieIdCache.TryGetValue(cacheKey, out var movieId))
        {
            var movie = await GetMovieOrNullAsync(movieId, ct).ConfigureAwait(false);
            var movieFile = movie?.MovieFile;
            if (movieFile is not null && movieFile.Path == symlinkOrStrmPath)
                return new MovieFileIds(movieFile.Id, movieId);
            SymlinkOrStrmToMovieIdCache.TryRemove(cacheKey, out _);
        }

        // otherwise, let's fetch all movies, cache all movie files
        // and return the matching movie-id and movie-file-id
        var allMovies = await GetMoviesAsync(ct).ConfigureAwait(false);
        MovieFileIds? result = null;
        foreach (var movie in allMovies)
        {
            var movieFile = movie.MovieFile;
            if (movieFile?.Path != null)
                SymlinkOrStrmToMovieIdCache[(Host, movieFile.Path)] = movie.Id;
            if (movieFile is not null && movieFile.Path == symlinkOrStrmPath)
                result = new MovieFileIds(movieFile.Id, movie.Id);
        }

        return result;
    }
}
