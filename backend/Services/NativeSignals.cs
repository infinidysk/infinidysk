using System.Runtime.InteropServices;
using Serilog;

namespace NzbWebDAV.Services;

/// <summary>
/// Sends POSIX signals to a child process. .NET's <c>Process.Kill</c> only sends
/// SIGKILL, which is the wrong signal for a process that has cleanup to do — an
/// rclone daemon killed outright leaves its FUSE mounts behind.
/// </summary>
internal static class NativeSignals
{
    private const int Sigterm = 15;

    /// <summary>
    /// Sends SIGTERM to <paramref name="processId"/>. Returns false when the
    /// platform has no such call or the signal could not be delivered, so the
    /// caller can fall back to a hard kill.
    /// </summary>
    public static bool TrySendTerm(int processId)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return false;

        try
        {
            return Kill(processId, Sigterm) == 0;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            Log.Debug(e, "SIGTERM is unavailable on this platform; falling back to a hard kill.");
            return false;
        }
    }

    // DllImport rather than LibraryImport: the source generator requires the
    // project to allow unsafe blocks, which is not worth turning on for one call.
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int Kill(int pid, int sig);
}
