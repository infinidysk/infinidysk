using NzbWebDAV.Config;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Models.Catalog;
using NzbWebDAV.Utils;

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
    CatalogPlaybackSessionStore sessionStore)
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
