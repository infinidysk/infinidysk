using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Config;
using NzbWebDAV.Services;

namespace NzbWebDAV.Api.Controllers.GetWatchdogEntries;

[ApiController]
[Route("api/get-watchdog-entries")]
public partial class GetWatchdogEntriesController(
    WatchdogLog watchdogLog,
    ConfigManager configManager
) : BaseApiController
{
    [GeneratedRegex(@"\s*\(\d+%\)\s*$")] private static partial Regex PercentSuffixRegex();

    protected override async Task<IActionResult> HandleRequest()
    {
        var limitStr = HttpContext.Request.Query["limit"].ToString();
        var limit = int.TryParse(limitStr, out var n) ? Math.Clamp(n, 1, 500) : 200;

        // Resolve nicknames from current config rather than storing per-row, so
        // a rename retroactively updates older entries and we avoid a schema
        // migration. Case-insensitive on host.
        var nicknamesByHost = configManager.GetUsenetProviderConfig().Providers
            .Where(p => !string.IsNullOrWhiteSpace(p.Nickname))
            .GroupBy(p => p.Host, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Nickname, StringComparer.OrdinalIgnoreCase);

        var recent = await watchdogLog.GetRecentAsync(limit, HttpContext.RequestAborted).ConfigureAwait(false);
        var dtos = recent.Select(a => new GetWatchdogEntriesResponse.EntryDto
        {
            ClickId = a.ClickId.ToString(),
            AttemptedAtUnix = a.AttemptedAt.ToUnixTimeSeconds(),
            ContentType = a.ContentType,
            RequestedTitle = a.RequestedTitle,
            CandidateTitle = a.CandidateTitle,
            IndexerName = a.IndexerName,
            Size = a.Size,
            RankIndex = a.RankIndex,
            Outcome = a.Result,
            FailReason = a.FailReason,
            DurationMs = a.DurationMs,
            IsWinner = a.IsWinner,
            ProviderHost = a.ProviderHost,
            ProviderNickname = ResolveNickname(a.ProviderHost, nicknamesByHost),
        }).ToList();

        return Ok(new GetWatchdogEntriesResponse
        {
            Status = true,
            Entries = dtos,
        });
    }

    // ProviderHost can be a single host, or a comma-separated formatted string
    // like "news.eweka.nl (60%), news.frugal.com (40%)" — see QueueItemProcessor.FormatProviders.
    // Returns a joined display string where each host is replaced with its
    // nickname (or kept as-is when no nickname is configured). Returns null
    // when no part has a nickname, so the frontend can fall back to its own
    // formatProviderShort for the host string.
    private static string? ResolveNickname(string? providerHost, Dictionary<string, string?> nicknamesByHost)
    {
        if (string.IsNullOrWhiteSpace(providerHost) || nicknamesByHost.Count == 0) return null;
        var parts = providerHost.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var anyMatched = false;
        var rendered = parts.Select(part =>
        {
            var pctMatch = PercentSuffixRegex().Match(part);
            var host = pctMatch.Success ? part[..pctMatch.Index].Trim() : part;
            var suffix = pctMatch.Success ? pctMatch.Value : string.Empty;
            if (nicknamesByHost.TryGetValue(host, out var nick) && !string.IsNullOrWhiteSpace(nick))
            {
                anyMatched = true;
                return nick + suffix;
            }
            return part;
        });
        return anyMatched ? string.Join(", ", rendered) : null;
    }
}
