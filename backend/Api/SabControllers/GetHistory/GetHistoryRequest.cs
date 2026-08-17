using Microsoft.AspNetCore.Http;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;

namespace NzbWebDAV.Api.SabControllers.GetHistory;

public class GetHistoryRequest
{
    public int Start { get; init; }
    public int Limit { get; init; } = int.MaxValue;
    public string? Category { get; init; }
    public List<Guid> NzoIds { get; init; } = [];
    public string? Search { get; init; }
    public HistoryItem.DownloadStatusOption? Status { get; init; }
    public bool HasUnsupportedStatus { get; init; }
    public bool FailedOnly { get; init; }
    public string? Sort { get; init; }
    public string? Direction { get; init; }
    public CancellationToken CancellationToken { get; set; }


    public GetHistoryRequest(HttpContext context, ConfigManager configManager)
    {
        var startParam = context.GetRequestParam("start");
        var limitParam = context.GetRequestParam("limit");
        var pageSizeParam = context.GetRequestParam("pageSize");
        var nzoIdsParam = context.GetRequestParam("nzo_ids");
        Category = SabCategoryResolver.GetCategory(context, configManager);
        Search = SabListQuery.NormalizeSearch(context.GetRequestParam("search"));
        FailedOnly = IsEnabled(context.GetRequestParam("failed_only"));
        var (parsedStatus, hasUnsupportedStatus) = ParseStatus(context.GetRequestParam("status"));
        Status = parsedStatus;
        HasUnsupportedStatus = !FailedOnly && hasUnsupportedStatus;
        if (FailedOnly) Status = HistoryItem.DownloadStatusOption.Failed;
        Sort = NormalizeSort(context.GetRequestParam("sort"));
        Direction = SabListQuery.NormalizeDirection(context.GetRequestParam("dir"));
        CancellationToken = context.RequestAborted;

        if (startParam is not null)
        {
            var isValidStartParam = int.TryParse(startParam, out int start);
            if (!isValidStartParam) throw new BadHttpRequestException("Invalid start parameter");
            Start = Math.Max(0, start);
        }

        // The official Sabnzbd api uses the `limit` param to specify the number of history items
        // that should be returned in the response. However, radarr/sonarr set this param to 60 items
        // which causes problems:
        //   * https://github.com/infinidysk/infinidysk/issues/48
        //   * https://github.com/Sonarr/Sonarr/issues/5452
        //
        // Because of this, NzbDAV added a setting to ignore the `limit` value specified by the Arrs.
        // When this setting is enabled, we skip the Arr limit and apply only the server-side ceiling
        // (see GetHistoryMaxPageSize) so responses stay bounded.
        if (limitParam is not null && !configManager.IsIgnoreSabHistoryLimitEnabled())
        {
            var isValidLimit = int.TryParse(limitParam, out var limit);
            if (!isValidLimit) throw new BadHttpRequestException("Invalid limit parameter");
            Limit = limit > 0 ? limit : int.MaxValue;
        }

        // Even though we may want to ignore the `limit` param from the Arrs, NzbDAV frontend
        // still needs a way to limit the pageSize for pagination. The `pageSize` param is used
        // for this, which takes precedence over the `limit` param. This param is not official to
        // the Sabnzbd api, and is intended to be used only by the NzbDAV frontend.
        if (pageSizeParam is not null)
        {
            var isValidPageSize = int.TryParse(pageSizeParam, out var pageSize);
            if (!isValidPageSize) throw new BadHttpRequestException("Invalid pageSize parameter");
            Limit = pageSize > 0 ? pageSize : int.MaxValue;
        }

        // Server-side ceiling: keep ignore-limit semantics for Arrs but never materialize
        // an unbounded history response (default Limit is int.MaxValue when omitted or ≤ 0).
        Limit = Math.Clamp(Limit, 0, configManager.GetHistoryMaxPageSize());

        if (nzoIdsParam is not null)
        {
            var nzoIds = new List<Guid>();
            var badTokens = new List<string>();
            foreach (var token in nzoIdsParam.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (Guid.TryParse(token, out var nzoId))
                {
                    nzoIds.Add(nzoId);
                }
                else
                {
                    badTokens.Add(token);
                }
            }

            if (badTokens.Count > 0)
            {
                const int maxBadTokensShown = 5;
                var shown = badTokens.Take(maxBadTokensShown).Select(t => $"'{t}'");
                var bad = string.Join(", ", shown);
                if (badTokens.Count > maxBadTokensShown)
                {
                    bad += $", ... ({badTokens.Count - maxBadTokensShown} more)";
                }

                throw new BadHttpRequestException($"Invalid nzo_ids parameter: {bad}");
            }

            NzoIds = nzoIds;
        }
    }

    private static bool IsEnabled(string? value) =>
        value is "1" or "true" or "True";

    private static (HistoryItem.DownloadStatusOption? status, bool unsupported) ParseStatus(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            null or "" => (null, false),
            "completed" => (HistoryItem.DownloadStatusOption.Completed, false),
            "failed" => (HistoryItem.DownloadStatusOption.Failed, false),
            _ => (null, true),
        };

    private static string? NormalizeSort(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "name" or "category" or "status" or "size" or "completed" => value.Trim().ToLowerInvariant(),
        _ => null,
    };
}
