using System.Data.Common;
using NzbWebDAV.Config;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Models.Catalog;
using NzbWebDAV.Utils;
using Serilog;

namespace NzbWebDAV.Services.Catalog;

/// <summary>
/// Turns a catalog title selection into the existing profile play-token flow: one
/// automatic indexer search, then the first ranked candidate's play URL. Candidate
/// verification, queue submission, variant reuse, and failover stay in
/// <c>ProfilePlayController</c>; nothing here re-implements them.
/// </summary>
public sealed class CatalogPlaybackService(
    ConfigManager configManager,
    CatalogService catalogService,
    SearchProfileService searchProfileService,
    CatalogPlaybackSessionStore sessionStore,
    CatalogWatchHistoryService watchHistory)
{
    internal const string NotConfiguredMessage = "Catalog playback is not configured.";
    internal const string NoReleasesMessage = "No Usenet releases found for this title.";

    public async Task<CatalogPlayback> CreateAsync(
        CreatePlaybackRequest request,
        string publicBaseUrl,
        string publicPathPrefix,
        CancellationToken ct)
    {
        var profileToken = ResolveProfileToken();
        var identity = await catalogService
            .GetPlaybackIdentityAsync(request.Type, request.TmdbId, request.Season, request.Episode, ct)
            .ConfigureAwait(false);

        var search = await searchProfileService.SearchByImdbAsync(
                profileToken,
                identity.Type,
                identity.Id,
                ct,
                clientQuery: identity.Title,
                verifyIdentity: true,
                startPreflight: true)
            .ConfigureAwait(false);

        if (search is null || search.PlayTokens.Length == 0 || search.Candidates.Count == 0)
            throw CatalogException.NotFound(NoReleasesMessage);

        var playToken = search.PlayTokens[0];
        var playPath = $"/api/search/{profileToken}/play/{playToken}.mkv";
        var publicDirectUrl = publicBaseUrl + playPath;
        var internalDirectUrl = CatalogBackendUrl.Combine(
            CatalogBackendUrl.GetLoopbackBase(EnvironmentUtil.GetEnvironmentVariable("BACKEND_URL")),
            playPath);

        var session = sessionStore.Add(
            publicDirectUrl,
            internalDirectUrl,
            identity.Type,
            identity.Id,
            search.Candidates[0].Title,
            publicPathPrefix);

        // The row appears as soon as a stream is found, so Continue Watching is accurate
        // even if the player never gets to its first timed report. A history failure must
        // never cost the viewer a working stream.
        try
        {
            await watchHistory.ReportProgressAsync(
                    new ReportProgressRequest(
                        identity.Type,
                        request.TmdbId,
                        request.Season,
                        request.Episode,
                        Position: 0,
                        Duration: session.MediaDuration?.TotalSeconds,
                        Finished: false,
                        identity.Title,
                        identity.Subtitle,
                        identity.PosterUrl,
                        identity.BackdropUrl),
                    ct)
                .ConfigureAwait(false);
        }
        catch (Exception e) when (e is DbException or InvalidOperationException)
        {
            Log.Warning(
                "Catalog: could not record watch history when starting playback ({ExceptionType}).",
                e.GetType().Name);
        }

        var playbackId = session.Id.ToString("D");
        return new CatalogPlayback(
            playbackId,
            publicDirectUrl,
            $"{publicBaseUrl}/api/catalog/playback/{playbackId}/playlist.m3u8",
            session.ReleaseTitle);
    }

    private string ResolveProfileToken()
    {
        var profileToken = configManager.GetCatalogProfileToken();
        if (profileToken is null || searchProfileService.GetProfile(profileToken) is null)
            throw new CatalogException(CatalogException.NotConfigured, NotConfiguredMessage);
        return profileToken;
    }
}
