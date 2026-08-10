using Microsoft.Extensions.Hosting;
using NzbWebDAV.Config;
using NzbWebDAV.Utils;
using Serilog;

namespace NzbWebDAV.Services;

/// <summary>
/// Prunes aged on-disk NZB backup copies written when
/// <c>api.nzb-backup-enabled</c> is on. Only deletes <c>*.nzb</c> files under
/// the configured backup directory; never touches <c>/</c> or empty paths.
/// </summary>
public class NzbBackupRetentionService(ConfigManager configManager) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await SafeSweepAsync(stoppingToken).ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromHours(1), stoppingToken).ConfigureAwait(false);
                await SafeSweepAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (SigtermUtil.IsSigtermTriggered())
            {
                return;
            }
        }
    }

    internal static int SweepDirectory(string backupLocation, int retentionDays, DateTime utcNow)
    {
        if (retentionDays <= 0)
            return 0;

        if (string.IsNullOrWhiteSpace(backupLocation))
            return 0;

        var trimmed = backupLocation.Trim();
        if (trimmed is "/" or "\\")
            return 0;

        string backupRoot;
        try
        {
            backupRoot = Path.GetFullPath(trimmed);
        }
        catch
        {
            return 0;
        }

        if (backupRoot is "/" or "\\" || !Directory.Exists(backupRoot))
            return 0;

        var cutoff = utcNow.AddDays(-retentionDays);
        var deleted = 0;

        foreach (var path in Directory.EnumerateFiles(backupRoot, "*.nzb", SearchOption.AllDirectories))
        {
            try
            {
                var lastWrite = File.GetLastWriteTimeUtc(path);
                if (lastWrite > cutoff)
                    continue;

                File.Delete(path);
                deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Debug(ex, "Failed to prune NZB backup file {Path}", path);
            }
        }

        return deleted;
    }

    private async Task SafeSweepAsync(CancellationToken stoppingToken)
    {
        try
        {
            if (!configManager.IsNzbBackupEnabled())
                return;

            var retentionDays = configManager.GetNzbBackupRetentionDays();
            if (retentionDays <= 0)
                return;

            var location = configManager.GetNzbBackupLocation();
            if (location is null)
                return;

            var deleted = SweepDirectory(location, retentionDays, DateTime.UtcNow);
            if (deleted > 0)
                Log.Information("Pruned {Count} NZB backup file(s) older than {Days} day(s)", deleted, retentionDays);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error(ex, "Error pruning NZB backup directory: {Message}", ex.Message);
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }
}
