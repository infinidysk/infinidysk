using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Clients.Indexers;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Api.Controllers.SearchIndexers;

[ApiController]
[Route("api/search-indexers")]
public class SearchIndexersController(
    ConfigManager configManager,
    NewznabRateLimiter rateLimiter,
    IndexerHitTracker hitTracker) : BaseApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        var request = new SearchIndexersRequest(HttpContext);
        var indexerConfig = configManager.GetIndexerConfig();
        var indexers = indexerConfig.Indexers.Where(x => x.Enabled).ToList();
        var globalProxy = indexerConfig.ProxyUrl;
        var ct = HttpContext.RequestAborted;

        var perIndexer = await Task.WhenAll(indexers.Select(async x =>
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var hitCheck = await hitTracker
                    .CheckAsync(x.Name, IndexerApiHit.HitType.Search, x.HitLimit, x.HitLimitResetTime, ct)
                    .ConfigureAwait(false);
                if (hitCheck is { Allowed: false })
                {
                    return (Status: new SearchIndexersResponse.IndexerStatus
                    {
                        Name = x.Name,
                        Ok = false,
                        Error = IndexerHitTracker.FormatSkipReason(hitCheck, IndexerApiHit.HitType.Search),
                        ElapsedMs = sw.ElapsedMilliseconds,
                    }, Results: new List<SearchIndexersResponse.Result>());
                }

                var ua = IndexerConfig.PerIndexerSearchUserAgent(x) ?? configManager.GetSearchUserAgent();
                var proxy = string.IsNullOrWhiteSpace(x.ProxyUrl) ? globalProxy : x.ProxyUrl;
                var timeout = indexerConfig.GetEffectiveTimeoutSeconds(x);
                await rateLimiter.WaitAsync(x.Name, x.MaxRequestsPerMinute, ct).ConfigureAwait(false);
                var client = new NewznabClient(x.Url, x.ApiKey, ua, proxy, timeout, x.SkipTlsVerification, x.UseHealthProxy);
                var items = await client.SearchAsync(request.Query, request.Limit, ct).ConfigureAwait(false);
                _ = hitTracker.RecordAsync(x.Name, IndexerApiHit.HitType.Search, CancellationToken.None);
                var mapped = items
                    .Where(i => !x.EnableStrictMatching || FilenameMatcher.Matches(request.Query, i.Title))
                    .Select(i => new SearchIndexersResponse.Result
                    {
                        Indexer = x.Name,
                        Title = i.Title,
                        NzbUrl = i.NzbUrl,
                        Size = i.Size,
                        Posted = i.Posted,
                        SourceIndexer = i.SourceIndexerName,
                        Language = i.Language,
                        Subs = i.Subs,
                        InfoHash = i.InfoHash,
                    }).ToList();
                return (Status: new SearchIndexersResponse.IndexerStatus
                {
                    Name = x.Name,
                    Ok = true,
                    ResultCount = mapped.Count,
                    ElapsedMs = sw.ElapsedMilliseconds,
                }, Results: mapped);
            }
            catch (Exception e)
            {
                return (Status: new SearchIndexersResponse.IndexerStatus
                {
                    Name = x.Name,
                    Ok = false,
                    Error = e.Message,
                    ElapsedMs = sw.ElapsedMilliseconds,
                }, Results: new List<SearchIndexersResponse.Result>());
            }
        }).ToList()).ConfigureAwait(false);

        return Ok(new SearchIndexersResponse
        {
            Results = perIndexer.SelectMany(x => x.Results)
                .GroupBy(x => x.NzbUrl)
                .Select(g => g.First())
                .OrderByDescending(x => x.Posted ?? DateTimeOffset.MinValue)
                .ToList(),
            Indexers = perIndexer.Select(x => x.Status).ToList(),
        });
    }
}
