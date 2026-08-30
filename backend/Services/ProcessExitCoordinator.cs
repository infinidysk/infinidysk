using NzbWebDAV.Utils;

namespace NzbWebDAV.Services;

/// <summary>
/// Serializes process-exit reasons so a background-service fault cannot be
/// overwritten by a later staged-restore request, and vice versa cannot hide
/// a fault that arrives after restore was already requested.
/// </summary>
public sealed class ProcessExitCoordinator
{
    private readonly object _gate = new();
    private ProcessExitReason _reason;

    internal int RequestRestoreRestart() =>
        Promote(ProcessExitReason.RestoreRestart);

    internal int ReportBackgroundServiceFault() =>
        Promote(ProcessExitReason.BackgroundServiceFault);

    private int Promote(ProcessExitReason requested)
    {
        lock (_gate)
        {
            if ((int)requested > (int)_reason)
                _reason = requested;

            var exitCode = _reason switch
            {
                ProcessExitReason.RestoreRestart =>
                    RestartUtil.RestartForRestoreExitCode,
                ProcessExitReason.BackgroundServiceFault => 1,
                _ => throw new InvalidOperationException(
                    "An exit reason must be selected before promotion."),
            };

            Environment.ExitCode = exitCode;
            return exitCode;
        }
    }

    private enum ProcessExitReason
    {
        None = 0,
        RestoreRestart = 1,
        BackgroundServiceFault = 2,
    }
}
