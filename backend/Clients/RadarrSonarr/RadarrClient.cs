using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
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

    public override async Task<ArrMediaFileMatch?> FindMediaFileAsync(
        string symlinkOrStrmPath,
        CancellationToken ct = default)
    {
        var movieIds = await GetMovieFileIds(symlinkOrStrmPath, ct).ConfigureAwait(false);
        return movieIds is null
            ? null
            : new ArrMediaFileMatch(
                ArrMediaKind.Movie,
                movieIds.MovieFileId,
                [movieIds.MovieId]);
    }

    public override async Task<ArrMissingPayloadCleanupOutcome> RemoveMissingPayloadAndSearchAsync(
        ArrMediaFileMatch match,
        Func<IReadOnlyList<string>, bool>? shouldRequestSearch = null,
        CancellationToken ct = default)
    {
        if (match.Kind != ArrMediaKind.Movie || match.MediaIds.Count != 1)
            throw new ArgumentException("Radarr cleanup requires one movie match.", nameof(match));

        if (!Is2xx(await DeleteMovieFile(match.FileId, ct).ConfigureAwait(false)))
            throw new InvalidOperationException(
                $"Failed to delete movie file {match.FileId} from radarr instance '{Host}'.");

        if (match.MediaKeys.Count == 0)
            return ArrMissingPayloadCleanupOutcome.RemovedNoSearchTargets;
        if (shouldRequestSearch is not null && !shouldRequestSearch(match.MediaKeys))
            return ArrMissingPayloadCleanupOutcome.RemovedSearchWithheld;

        try
        {
            await ExecuteWithTransientRetryAsync(
                token => CommandAsync(
                    new { name = "MoviesSearch", movieIds = match.MediaIds },
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
        int movieFileId)
    {
        Log.Warning(
            "Radarr missing-payload cleanup on {Host}: movie file {MovieFileId} was removed, " +
            "but replacement search failed. Reason: {Reason}",
            Host,
            movieFileId,
            exception.Message);
        Log.Debug(exception, "Radarr missing-payload replacement-search failure stack");
        return ArrMissingPayloadCleanupOutcome.RemovedSearchFailed;
    }

    public override async Task<ArrRepairOutcome> RemoveAndBlocklist(
        string symlinkOrStrmPath,
        Guid downloadId,
        Func<IReadOnlyList<string>, bool>? shouldRequestSearch = null,
        CancellationToken ct = default)
    {
        var match = await FindMediaFileAsync(symlinkOrStrmPath, ct).ConfigureAwait(false);
        if (match is null) return ArrRepairOutcome.MediaItemNotFound;
        return await RemoveAndBlocklist(match, downloadId, shouldRequestSearch, ct)
            .ConfigureAwait(false);
    }

    public override async Task<ArrRepairOutcome> RemoveAndBlocklist(
        ArrMediaFileMatch mediaFile,
        Guid downloadId,
        Func<IReadOnlyList<string>, bool>? shouldRequestSearch = null,
        CancellationToken ct = default)
    {
        if (mediaFile.Kind != ArrMediaKind.Movie || mediaFile.MediaIds.Count != 1)
            throw new ArgumentException("Radarr repair requires one movie match.", nameof(mediaFile));

        var historyId = await GetHistoryRecordId(downloadId, ct).ConfigureAwait(false);
        if (historyId == null) return ArrRepairOutcome.DownloadHistoryNotFound;

        var movieId = mediaFile.MediaIds[0];
        if (!Is2xx(await DeleteMovieFile(mediaFile.FileId, ct).ConfigureAwait(false)))
            throw new InvalidOperationException(
                $"Failed to delete movie file {mediaFile.FileId} from radarr instance `{Host}`.");

        await MarkHistoryFailed(historyId.Value, ct).ConfigureAwait(false);

        if (shouldRequestSearch is not null && !shouldRequestSearch([$"movie:{movieId}"]))
        {
            Log.Warning(
                "Radarr repair on {Host}: automatic replacement-search limit reached for movie {MovieId}; " +
                "the file was removed and its download blocklisted without starting another search.",
                Host,
                movieId);
            return ArrRepairOutcome.RemoveAndBlocklistSucceededSearchWithheld;
        }

        try
        {
            await ExecuteWithTransientRetryAsync(
                token => CommandAsync(
                    new { name = "MoviesSearch", movieIds = new[] { movieId } },
                    token),
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Warning(
                ex,
                "Radarr repair on {Host}: failed to request MoviesSearch for movie {MovieId}",
                Host,
                movieId);
        }

        return ArrRepairOutcome.RemoveAndBlocklistSucceeded;
    }

    public override Task<ArrHistory> GetMediaImportHistoryAsync(
        ArrMediaFileMatch mediaFile,
        int page,
        int pageSize,
        CancellationToken ct = default)
    {
        if (mediaFile.Kind != ArrMediaKind.Movie || mediaFile.MediaIds.Count == 0)
            return Task.FromResult(new ArrHistory());

        var movieId = mediaFile.MediaIds[0];
        return Get<ArrHistory>(
            $"/history?movieId={movieId}&eventType=3&page={page}&pageSize={pageSize}&sortKey=date&sortDirection=descending",
            ct);
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
