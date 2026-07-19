using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Config;
using NzbWebDAV.Extensions;
using NzbWebDAV.Services;
using Serilog;

namespace NzbWebDAV.Api.Controllers.JellyfinWebhook;

/// <summary>
/// Receives Jellyfin's "Playback Progress" webhook events and, once the configured
/// watch-progress threshold is crossed, resolves and prefetches the next episode.
/// Authenticated via a dedicated `?apikey=` token (not the general api.key), configured
/// as part of the webhook URL entered into Jellyfin's webhook plugin.
/// </summary>
[ApiController]
[Route("api/jellyfin-webhook")]
public class JellyfinWebhookController(
    ConfigManager configManager,
    EpisodeResolverService episodeResolverService,
    PrefetchCacheService prefetchCacheService
) : BaseApiController
{
    // dedupes repeated PlaybackProgress pings (Jellyfin fires one roughly every ~10s)
    // for an episode already handled, so a full Sonarr lookup + library filesystem scan
    // doesn't re-run on every single ping once past the threshold. Capped and cleared
    // wholesale past a size limit rather than tracked with per-entry expiry, since this
    // is a soft perf guard, not a correctness-critical cache.
    private static readonly HashSet<string> HandledItemIds = new();
    private static readonly Lock HandledItemIdsLock = new();
    private const int MaxHandledItemIds = 1000;

    // authenticated via the ?apikey= query param below instead of the general api.key
    protected override bool RequiresAuthentication => false;

    protected override async Task<IActionResult> HandleRequest()
    {
        var providedToken = HttpContext.GetRequestParam("apikey");
        if (string.IsNullOrEmpty(providedToken) || providedToken != configManager.GetJellyfinWebhookToken())
            return Unauthorized(new BaseApiResponse { Status = false, Error = "Invalid or missing webhook token" });

        // never let a bad/unexpected payload or downstream failure surface as a 500 --
        // Jellyfin's webhook plugin can disable a destination after repeated failures.
        try
        {
            await ProcessAsync().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Log.Warning(e, $"Failed to process jellyfin webhook: {e.Message}");
        }

        return Ok(new BaseApiResponse());
    }

    private async Task ProcessAsync()
    {
        if (!configManager.IsPrefetchCacheEnabled()) return;

        var payload = await HttpContext.Request
            .ReadFromJsonAsync<JellyfinWebhookPayload>(HttpContext.RequestAborted)
            .ConfigureAwait(false);
        if (payload is null) return;
        if (payload.NotificationType != "PlaybackProgress") return;
        if (payload.ItemType != "Episode") return;
        if (payload.SeriesName is null || payload.SeasonNumber is null || payload.EpisodeNumber is null) return;
        if (payload.PlaybackPositionTicks is null || payload.RunTimeTicks is not > 0) return;

        var percentWatched = (double)payload.PlaybackPositionTicks.Value / payload.RunTimeTicks.Value * 100;
        if (percentWatched < configManager.GetPrefetchCacheThresholdPercent()) return;

        if (payload.ItemId != null && !TryMarkHandled(payload.ItemId)) return;

        var nextEpisodeDavItemId = await episodeResolverService
            .ResolveNextEpisodeDavItemIdAsync(
                payload.SeriesName, payload.SeasonNumber.Value, payload.EpisodeNumber.Value,
                HttpContext.RequestAborted)
            .ConfigureAwait(false);
        if (nextEpisodeDavItemId is null) return;

        await prefetchCacheService
            .TriggerPrefetchAsync(nextEpisodeDavItemId.Value, HttpContext.RequestAborted)
            .ConfigureAwait(false);
    }

    private static bool TryMarkHandled(string itemId)
    {
        lock (HandledItemIdsLock)
        {
            if (HandledItemIds.Count >= MaxHandledItemIds) HandledItemIds.Clear();
            return HandledItemIds.Add(itemId);
        }
    }
}
