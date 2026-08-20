using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models.Metrics;
using NzbWebDAV.Services;

namespace NzbWebDAV.Api.Controllers.GetArrHealth;

[ApiController]
[Route("api/get-arr-health")]
public class GetArrHealthController(ConfigManager configManager, ArrHealthService arrHealthService) : BaseApiController
{
    private const long OneHourMs = 60L * 60 * 1000;
    private const long OneDayMs = 24 * OneHourMs;
    internal const int AwaitingLimit = 10;

    internal Func<MetricsDbContext> MetricsContextFactory { get; set; } = static () => new MetricsDbContext();

    protected override async Task<IActionResult> HandleRequest()
    {
        var request = new GetArrHealthRequest(HttpContext);
        var response = await BuildResponseAsync(request.Window, DateTimeOffset.UtcNow, request.CancellationToken)
            .ConfigureAwait(false);
        return Ok(response);
    }

    internal async Task<GetArrHealthResponse> BuildResponseAsync(
        GetArrHealthRequest.ArrHealthWindow window,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var enabled = configManager.GetArrConfig().GetEnabledInstances().ToList();
        if (!configManager.IsArrHealthEnabled() || enabled.Count == 0)
            return new GetArrHealthResponse { Configured = false };

        var nowMs = now.ToUnixTimeMilliseconds();
        var windowStartMs = WindowStartMs(window, nowMs);
        var enabledKeys = enabled
            .Select(instance => ArrConfig.MakeInstanceKey(instance.AppType, instance.Details.Host))
            .ToHashSet(StringComparer.Ordinal);
        await using var db = MetricsContextFactory();
        var events = await db.ArrImportEvents.AsNoTracking()
            .Where(e => e.ImportedAtMs >= windowStartMs && enabledKeys.Contains(e.InstanceKey))
            .ToListAsync(ct).ConfigureAwait(false);

        return Build(
            events,
            arrHealthService.GetSnapshots(),
            enabled,
            now);
    }

    internal static bool IsStoreQueryEnabled(ConfigManager config) =>
        config.IsArrHealthEnabled() && config.GetArrConfig().GetEnabledInstances().Any();

    internal static long WindowStartMs(GetArrHealthRequest.ArrHealthWindow window, long nowMs) => window switch
    {
        GetArrHealthRequest.ArrHealthWindow.Last1Hour => nowMs - OneHourMs,
        GetArrHealthRequest.ArrHealthWindow.Last24Hours => nowMs - OneDayMs,
        GetArrHealthRequest.ArrHealthWindow.Last7Days => nowMs - 7 * OneDayMs,
        GetArrHealthRequest.ArrHealthWindow.Last30Days => nowMs - 30 * OneDayMs,
        GetArrHealthRequest.ArrHealthWindow.AllTime => 0,
        _ => nowMs - OneDayMs,
    };

    internal static GetArrHealthResponse Build(
        IReadOnlyList<ArrImportEvent> events,
        IReadOnlyList<ArrHealthSnapshot> snapshots,
        IReadOnlyList<(string AppType, ArrConfig.ConnectionDetails Details)> enabled,
        DateTimeOffset now)
    {
        var snapshotByKey = snapshots.ToDictionary(s => s.InstanceKey, StringComparer.Ordinal);
        var rows = new List<GetArrHealthResponse.ArrHealthInstanceRow>();
        var awaiting = new List<GetArrHealthResponse.ArrAwaitingItem>();

        foreach (var (appType, details) in enabled)
        {
            var key = ArrConfig.MakeInstanceKey(appType, details.Host);
            snapshotByKey.TryGetValue(key, out var snapshot);
            var name = snapshot?.DisplayName
                       ?? (string.IsNullOrWhiteSpace(details.Name) ? details.Host : details.Name);
            var instanceEvents = events.Where(e => e.InstanceKey == key).ToList();
            var handoffs = instanceEvents.Where(e => e.HandoffMs != null).Select(e => e.HandoffMs!.Value);
            rows.Add(new GetArrHealthResponse.ArrHealthInstanceRow
            {
                Key = key,
                Name = name,
                AppType = appType,
                Host = details.Host,
                Status = FormatStatus(snapshot?.Status ?? ArrInstanceHealthStatus.Pending),
                Imports = instanceEvents.Count,
                MedianHandoffMs = ArrHealthMath.Percentile(handoffs, 0.50),
                P95HandoffMs = ArrHealthMath.Percentile(handoffs, 0.95),
                QueueCount = snapshot?.QueueCount ?? 0,
                AwaitingCount = snapshot?.AwaitingCount ?? 0,
                LastImportAtMs = snapshot?.LastImportAtMs,
                LastError = snapshot?.LastError,
            });

            if (snapshot is null) continue;
            foreach (var item in snapshot.Awaiting)
            {
                var waitingMs = ArrHealthMath.ComputeWaitingMs(item.CreatedAt, now);
                awaiting.Add(new GetArrHealthResponse.ArrAwaitingItem
                {
                    Title = item.Title,
                    InstanceKey = key,
                    InstanceName = name,
                    WaitingMs = waitingMs,
                    IsUnusual = ArrHealthMath.IsUnusual(
                        waitingMs,
                        snapshot.MedianHandoffMs30d,
                        snapshot.MedianSampleCount30d),
                });
            }
        }

        var enabledKeys = rows.Select(r => r.Key).ToHashSet(StringComparer.Ordinal);
        var includedEvents = events.Where(e => enabledKeys.Contains(e.InstanceKey)).ToList();
        var includedHandoffs = includedEvents.Where(e => e.HandoffMs != null).Select(e => e.HandoffMs!.Value);

        return new GetArrHealthResponse
        {
            Configured = true,
            Summary = new GetArrHealthResponse.ArrHealthSummary
            {
                InstancesOnline = rows.Count(r => r.Status is "healthy" or "degraded"),
                InstancesTotal = rows.Count,
                ImportsCompleted = includedEvents.Count,
                MedianHandoffMs = ArrHealthMath.Percentile(includedHandoffs, 0.50),
                P95HandoffMs = ArrHealthMath.Percentile(includedHandoffs, 0.95),
                AwaitingImport = rows.Sum(r => r.AwaitingCount),
                Degraded = rows.Count(r => r.Status == "degraded"),
            },
            Instances = rows,
            Awaiting = awaiting
                .OrderByDescending(a => a.WaitingMs ?? long.MinValue)
                .Take(AwaitingLimit)
                .ToList(),
        };
    }

    internal static string FormatStatus(ArrInstanceHealthStatus status) => status switch
    {
        ArrInstanceHealthStatus.Healthy => "healthy",
        ArrInstanceHealthStatus.Degraded => "degraded",
        ArrInstanceHealthStatus.Offline => "offline",
        _ => "pending",
    };
}
