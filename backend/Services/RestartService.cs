using Microsoft.Extensions.Hosting;
using NzbWebDAV.Utils;
using Serilog;

namespace NzbWebDAV.Services;

/// <summary>
/// Schedules a graceful process exit after a restore has been staged so the
/// Docker/local restart loop can re-enter the maintenance phase.
/// </summary>
public sealed class RestartService(IHostApplicationLifetime lifetime)
{
    public void RequestRestartForRestore()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                Environment.ExitCode = RestartUtil.RestartForRestoreExitCode;
                Log.Information(
                    "Exiting with code {ExitCode} to apply staged database restore",
                    RestartUtil.RestartForRestoreExitCode);
                lifetime.StopApplication();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.Error(ex, "Failed to request restart for database restore");
            }
        });
    }
}
