using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;
using Serilog;

namespace NzbWebDAV.Services;

/// <summary>
/// Counts permanent streaming failures and, once the configured threshold is reached,
/// marks the item for an urgent health check (PAR2 first, then Arr replacement).
/// </summary>
public class StreamingRepairScheduler(
    ConfigManager configManager,
    StreamingFailureTracker failureTracker,
    IDbContextFactory<DavDatabaseContext>? dbContextFactory = null)
{
    // Static so the middleware and the playback zero-fill path share one dedup window.
    private static readonly ConcurrentDictionary<Guid, RepairScheduleReservation> RecentRepairTriggers = new();
    private static readonly ConcurrentDictionary<Guid, (DateTime LastLogged, int SuppressedCount)> RecentSkippedRepairs = new();
    private static readonly TimeSpan RepairDedupeWindow = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan SkippedLogWindow = TimeSpan.FromSeconds(60);
    private static int _callCount;

    internal Func<Guid, Task>? CompletionHook { get; set; }

    public void ScheduleRepair(DavItem davItem, string? segmentId = null)
    {
        CleanupStaleEntries();
        var davItemId = davItem.Id;
        var repairDisabledReason = configManager.GetRepairDisabledReason();
        if (repairDisabledReason != null)
        {
            LogRepairSkipped(davItem, repairDisabledReason);
            return;
        }

        // Count every distinct streaming failure before applying either threshold or deduplication.
        // Repeated failures must still advance the repair threshold while duplicate DB scheduling
        // writes remain suppressed below.
        var failureCount = string.IsNullOrEmpty(segmentId)
            ? failureTracker.RecordUnattributedFailure(davItemId).Count
            : failureTracker.RecordAttributedFailure(davItemId, segmentId).Count;
        var threshold = configManager.GetAutoRemoveAfterFailures();
        if (!ShouldScheduleUrgentRepair(threshold, failureCount))
        {
            Log.Information(
                "Deferring dynamic repair for DavItem {DavItemId} until streaming failure {FailureCount}/{FailureThreshold}",
                davItemId, failureCount, threshold);
            return;
        }

        var reservation = new RepairScheduleReservation(DateTime.UtcNow, false);
        if (RecentRepairTriggers.TryAdd(davItemId, reservation))
        {
            // This request owns the pending scheduling attempt.
        }
        else if (RecentRepairTriggers.TryGetValue(davItemId, out var existing)
             && existing is not null
             && (!existing.Committed || DateTime.UtcNow - existing.Timestamp < RepairDedupeWindow))
        {
            return;
        }
        else if (existing is null || !RecentRepairTriggers.TryUpdate(davItemId, reservation, existing))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await using var mutationGate = await failureTracker
                    .AcquireMutationGateAsync(davItemId, CancellationToken.None)
                    .ConfigureAwait(false);
                await using var dbContext = dbContextFactory is null
                    ? new DavDatabaseContext()
                    : await dbContextFactory.CreateDbContextAsync().ConfigureAwait(false);
                var item = await dbContext.Items.FindAsync(davItemId).ConfigureAwait(false);
                if (item == null)
                {
                    RecentRepairTriggers.TryRemove(
                        new KeyValuePair<Guid, RepairScheduleReservation>(davItemId, reservation));
                    return;
                }

                // UnixEpoch sorts first in HealthCheckService (non-null before null, then ascending).
                // Only skip if already urgent — overdue items must still be bumped (Pukabyte#4).
                var urgent = DateTimeOffset.UnixEpoch;
                if (item.NextHealthCheck == urgent)
                {
                    if (item.UrgentRepairFailures is null || item.UrgentRepairFailures < failureCount)
                    {
                        item.UrgentRepairFailures = failureCount;
                        await dbContext.SaveChangesAsync().ConfigureAwait(false);
                    }
                    RecentRepairTriggers.TryUpdate(
                        davItemId,
                        reservation with { Committed = true },
                        reservation);
                    return;
                }

                item.NextHealthCheck = urgent;
                item.UrgentRepairFailures = failureCount;
                await dbContext.SaveChangesAsync().ConfigureAwait(false);
                RecentRepairTriggers.TryUpdate(
                    davItemId, reservation with { Committed = true }, reservation);
                Log.Information(
                    "Scheduled dynamic repair for {FilePath} (streaming failures {FailureCount}/{FailureThreshold})",
                    item.Path, failureCount, threshold);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                RecentRepairTriggers.TryRemove(
                    new KeyValuePair<Guid, RepairScheduleReservation>(davItemId, reservation));
                if (ex.TryGetKnownErrorMessage(out var reason))
                {
                    Log.Warning("Dynamic repair scheduling deferred. Reason: {Reason}", reason);
                    Log.Debug(ex, "Dynamic repair scheduling known failure stack");
                }
                else
                {
                    Log.Warning(ex, "Failed to schedule dynamic repair for DavItem {DavItemId}", davItemId);
                }
            }
            finally
            {
                if (CompletionHook is { } completionHook)
                {
                    try
                    {
                        await completionHook(davItemId).ConfigureAwait(false);
                    }
                    catch (Exception e) when (e is not OutOfMemoryException)
                    {
                        Log.Debug(e, "Dynamic repair scheduling completion hook failed for DavItem {DavItemId}", davItemId);
                    }
                }
            }
        });
    }

    internal static void InvalidateDedup(Guid davItemId)
    {
        if (!RecentRepairTriggers.TryGetValue(davItemId, out var reservation) || !reservation.Committed)
            return;

        RecentRepairTriggers.TryRemove(
            new KeyValuePair<Guid, RepairScheduleReservation>(davItemId, reservation));
    }

    internal static bool ShouldScheduleUrgentRepair(int threshold, int failureCount)
    {
        return threshold <= 0 || failureCount >= threshold;
    }

    private static void LogRepairSkipped(DavItem davItem, string reason)
    {
        var now = DateTime.UtcNow;
        var suppressed = 0;
        var shouldLog = false;
        RecentSkippedRepairs.AddOrUpdate(
            davItem.Id,
            _ =>
            {
                shouldLog = true;
                return (now, 0);
            },
            (_, existing) =>
            {
                if (now - existing.LastLogged < SkippedLogWindow)
                    return (existing.LastLogged, existing.SuppressedCount + 1);
                shouldLog = true;
                suppressed = existing.SuppressedCount;
                return (now, 0);
            });
        if (!shouldLog)
            return;

        if (suppressed > 0)
            Log.Warning(
                "Streaming failure for {FilePath} will not trigger repair: {Reason}. Configure Settings > Health & Repairs. (suppressed {SuppressedCount} duplicates in last 60s)",
                davItem.Path,
                reason,
                suppressed);
        else
            Log.Warning(
                "Streaming failure for {FilePath} will not trigger repair: {Reason}. Configure Settings > Health & Repairs.",
                davItem.Path,
                reason);
    }

    private static void CleanupStaleEntries()
    {
        if (Interlocked.Increment(ref _callCount) % 100 != 0)
            return;

        var cutoff = DateTime.UtcNow - TimeSpan.FromMinutes(5);
        foreach (var kvp in RecentRepairTriggers.Where(kvp => kvp.Value.Timestamp < cutoff))
            RecentRepairTriggers.TryRemove(kvp.Key, out _);
        foreach (var kvp in RecentSkippedRepairs.Where(kvp => kvp.Value.LastLogged < cutoff))
            RecentSkippedRepairs.TryRemove(kvp.Key, out _);
    }

    private sealed record RepairScheduleReservation(DateTime Timestamp, bool Committed);
}
