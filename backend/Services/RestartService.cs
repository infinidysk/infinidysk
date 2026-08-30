using Microsoft.Extensions.Hosting;
using NzbWebDAV.Utils;
using Serilog;

namespace NzbWebDAV.Services;

/// <summary>
/// Schedules a graceful process exit after a restore has been staged so the
/// Docker/local restart loop can re-enter the maintenance phase.
/// </summary>
public sealed class RestartService(
    IHostApplicationLifetime lifetime,
    ProcessExitCoordinator exitCoordinator)
{
    public void RequestRestartForRestore()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                StopForStagedRestore();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.Error(ex, "Failed to request restart for database restore");
            }
        });
    }

    internal int StopForStagedRestore()
    {
        var exitCode = exitCoordinator.RequestRestoreRestart();
        if (exitCode == RestartUtil.RestartForRestoreExitCode)
        {
            Log.Information(
                "Exiting with code {ExitCode} to apply staged database restore",
                exitCode);
        }
        else
        {
            Log.Information(
                "Preserving exit code {ExitCode} instead of staged restore restart",
                exitCode);
        }

        lifetime.StopApplication();
        return exitCode;
    }
}
