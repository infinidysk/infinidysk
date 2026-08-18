using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Services;
using Serilog;

namespace NzbWebDAV.Database;

internal static class StartupDatabaseMigrator
{
    public static Task RunAsync(
        DavDatabaseContext databaseContext,
        MetricsDbContext metricsContext,
        CancellationToken cancellationToken) =>
        RunAsync(
            databaseContext,
            metricsContext,
            static async (progress, ct) =>
                await MigrationStatusServer.StartAsync(progress, ct).ConfigureAwait(false),
            cancellationToken);

    internal static async Task RunAsync(
        DavDatabaseContext databaseContext,
        MetricsDbContext metricsContext,
        Func<MigrationProgress, CancellationToken, Task<IAsyncDisposable?>> startStatusServer,
        CancellationToken cancellationToken)
    {
        IAsyncDisposable? lease = null;
        if (!databaseContext.Database.IsNpgsql())
        {
            var databasePath = databaseContext.Database.GetDbConnection().DataSource;
            lease = await DatabaseMigrationLease
                .AcquireAsync(databasePath, cancellationToken)
                .ConfigureAwait(false);

            await databaseContext.Database
                .ExecuteSqlRawAsync("PRAGMA journal_mode = WAL;", cancellationToken)
                .ConfigureAwait(false);
            await DatabaseStartupGuards
                .ClearAbandonedMigrationLockAsync(databaseContext, cancellationToken)
                .ConfigureAwait(false);
        }

        await using var migrationLease = lease;
        await DatabaseStartupGuards
            .ClearAbandonedMigrationLockAsync(metricsContext, cancellationToken)
            .ConfigureAwait(false);

        var pendingMigrations = (await databaseContext.Database
            .GetPendingMigrationsAsync(cancellationToken)
            .ConfigureAwait(false))
            .ToList();
        var pendingMetricsMigrations = (await metricsContext.Database
            .GetPendingMigrationsAsync(cancellationToken)
            .ConfigureAwait(false))
            .ToList();

        if (pendingMigrations.Count == 0 && pendingMetricsMigrations.Count == 0)
        {
            Log.Information("No pending database migrations");
            await metricsContext.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        var steps = pendingMigrations
            .Select(id => new MigrationProgress.MigrationStep(
                id,
                MigrationProgress.FriendlyName(id),
                MigrationProgress.IsSlow(id)))
            .ToList();
        steps.Add(new MigrationProgress.MigrationStep(
            MigrationProgress.MetricsStepId,
            "Metrics database",
            false));

        var progress = new MigrationProgress();
        progress.Initialize(steps);
        await using var statusServer = await startStatusServer(progress, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            for (var i = 0; i < steps.Count; i++)
            {
                var step = steps[i];
                Log.Information(
                    "Startup database migration step {Index}/{Total}: {Name}",
                    i + 1,
                    steps.Count,
                    step.Name);
                progress.BeginStep(step.Id);

                if (step.Id == MigrationProgress.MetricsStepId)
                    await metricsContext.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
                else
                    await databaseContext.Database.MigrateAsync(step.Id, cancellationToken).ConfigureAwait(false);

                progress.CompleteStep(step.Id);
            }

            progress.Complete();
            Log.Information("Database migrations completed");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            progress.Fail(ex.Message);
            if (statusServer is not null)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(3), CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    // Best-effort grace period before the status server is disposed.
                }
            }

            throw;
        }
    }
}
