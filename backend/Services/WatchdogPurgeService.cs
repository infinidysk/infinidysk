using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using NzbWebDAV.Database;
using NzbWebDAV.Utils;
using Serilog;

namespace NzbWebDAV.Services;

/// <summary>
/// Trims old watchdog entries so the table doesn't grow unbounded.
/// Cascade-cleanup on queue/history removal handles "this content went away";
/// this handles "this content was never queued / never played and is now stale."
/// </summary>
public class WatchdogPurgeService(IDbContextFactory<DavDatabaseContext> dbContextFactory) : BackgroundService
{
    private static readonly TimeSpan RetentionWindow = TimeSpan.FromDays(30);
    private static readonly TimeSpan PurgeInterval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var dbContext = dbContextFactory.CreateDbContext();

                var cutoff = DateTimeOffset.UtcNow - RetentionWindow;
                var deleted = await dbContext.WatchdogEntries
                    .Where(x => x.AttemptedAt < cutoff)
                    .ExecuteDeleteAsync(stoppingToken)
                    .ConfigureAwait(false);

                if (deleted > 0)
                    Log.Information("Purged {Count} watchdog entries older than {Days} days", deleted, RetentionWindow.TotalDays);

                await Task.Delay(PurgeInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (SigtermUtil.IsSigtermTriggered())
            {
                return;
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                var retryDelay = BackgroundServiceErrorHandler.LogAndGetRetryDelay(
                    e,
                    "Error purging watchdog entries.",
                    TimeSpan.FromMinutes(5));
                await Task.Delay(retryDelay, stoppingToken).ConfigureAwait(false);
            }
        }
    }
}
