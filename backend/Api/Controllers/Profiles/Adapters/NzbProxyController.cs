using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Config;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Extensions;
using NzbWebDAV.Services;
using NzbWebDAV.Utils;
using Serilog;

namespace NzbWebDAV.Api.Controllers.Profiles.Adapters;

[ApiController]
[Route("api/search/{token}/nzb/{playToken}.nzb")]
public class NzbProxyController(
    ConfigManager configManager,
    SearchProfileService searchService,
    NzbResolutionCache cache,
    NzbFetchCoalescer nzbFetchCoalescer
) : ControllerBase
{
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(60);

    [HttpGet]
    public async Task<IActionResult> Get(string token, string playToken)
    {
        var profile = searchService.GetProfile(token);
        if (profile is null) return NotFound();
        if (!searchService.IsAdapterEnabled(token, "json")
            && !searchService.IsAdapterEnabled(token, "newznab"))
        {
            return NotFound();
        }

        var entry = cache.Get(playToken);
        if (entry is null) return NotFound("Link expired. Re-search to refresh.");
        if (!entry.ProfileToken.FixedTimeEquals(token)) return NotFound();

        var candidate = entry.Primary;
        if (string.IsNullOrWhiteSpace(candidate.NzbUrl)) return NotFound();

        byte[]? bytes;
        try
        {
            var skipTlsVerification = configManager.GetIndexerConfig().ShouldSkipTlsVerification(candidate.IndexerName);
            bytes = await nzbFetchCoalescer.GetOrFetchAsync(
                candidate.NzbUrl,
                candidate.ProxyUrl,
                skipTlsVerification,
                async innerCt =>
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, candidate.NzbUrl);
                    req.Headers.TryAddWithoutValidation("User-Agent", candidate.IndexerUserAgent);
                    var client = ProxyHttpClientPool.GetClient(candidate.ProxyUrl, skipTlsVerification);
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(innerCt);
                    cts.CancelAfter(FetchTimeout);
                    using var resp = await client
                        .SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                        .ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode) return null;
                    try
                    {
                        return await HttpContentReadUtil
                            .ReadBoundedAsync(resp.Content, NzbFetchLimits.MaxResponseBytes, cts.Token)
                            .ConfigureAwait(false);
                    }
                    catch (NzbResponseTooLargeException e)
                    {
                        Log.Warning(
                            "NZB proxy rejected oversized response from {Url}. Limit: {Limit} bytes. Reason: {Reason}",
                            candidate.NzbUrl, e.MaxBytes, e.Message);
                        Log.Debug(e, "NZB proxy oversized response stack");
                        return null;
                    }
                },
                HttpContext.RequestAborted).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new EmptyResult();
        }
        catch (Exception e) when (e is HttpRequestException or IOException or TimeoutException)
        {
            Log.Debug(e, "NZB proxy fetch failed for {Url}", candidate.NzbUrl);
            return StatusCode(502, "Failed to fetch NZB from source indexer.");
        }

        if (bytes is null)
        {
            return StatusCode(502, "Source indexer failed to return the NZB.");
        }

        Response.Headers["Content-Disposition"] =
            $"attachment; filename=\"{ProfileReleaseName.ToNzbFileName(candidate.Title)}\"";
        return File(bytes, "application/x-nzb");
    }
}
