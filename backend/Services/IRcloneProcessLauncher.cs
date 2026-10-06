using System.ComponentModel;
using System.Diagnostics;
using Serilog;

namespace NzbWebDAV.Services;

/// <summary>A running rclone daemon process.</summary>
public interface IRcloneProcessHandle : IDisposable
{
    bool HasExited { get; }

    /// <summary>Asks the process to stop. Safe to call more than once.</summary>
    void Terminate();
}

/// <summary>
/// Starts the rclone daemon. Abstracted so the supervisor's decisions can be
/// tested without spawning real processes.
/// </summary>
public interface IRcloneProcessLauncher
{
    IRcloneProcessHandle Start(RcloneDaemonOptions options, Action<string> onOutput);
}

/// <summary>Launches the real <c>rclone rcd</c> process.</summary>
public sealed class RcloneProcessLauncher : IRcloneProcessLauncher
{
    public IRcloneProcessHandle Start(RcloneDaemonOptions options, Action<string> onOutput)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = RcloneCapabilityCheck.RcloneBinaryPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var argument in options.ToArguments())
            startInfo.ArgumentList.Add(argument);

        // The RC credentials go in the environment so they are not visible in
        // `ps` to anything else sharing the container.
        foreach (var (name, value) in options.ToEnvironment())
            startInfo.Environment[name] = value;

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        try
        {
            process.OutputDataReceived += (_, e) => Forward(e.Data, onOutput);
            process.ErrorDataReceived += (_, e) => Forward(e.Data, onOutput);

            Log.Information("Starting built-in rclone daemon: {Command}", options.Describe());

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            // Ownership passes to the handle, which disposes it.
            var handle = new ProcessHandle(process);
            process = null;
            return handle;
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static void Forward(string? line, Action<string> onOutput)
    {
        if (!string.IsNullOrWhiteSpace(line)) onOutput(line);
    }

    /// <summary>
    /// How long a terminating daemon is given to exit before it is killed.
    ///
    /// Deliberately well inside <c>HostOptions.ShutdownTimeout</c> (5s): a longer
    /// wait cannot finish, because the host tears the process down first and the
    /// mount is left behind — which is the failure this whole path exists to
    /// avoid. The mounts are released over the RC API before this runs, so
    /// reaching the kill is no longer destructive.
    /// </summary>
    internal static readonly TimeSpan GracefulShutdownTimeout = TimeSpan.FromSeconds(2);

    private sealed class ProcessHandle(Process process) : IRcloneProcessHandle
    {
        public bool HasExited
        {
            get
            {
                try
                {
                    return process.HasExited;
                }
                catch (InvalidOperationException)
                {
                    // The process was never started or has already been reaped.
                    return true;
                }
            }
        }

        /// <summary>
        /// SIGTERM first, SIGKILL only as a fallback. rclone unmounts everything it
        /// mounted when it is asked to terminate; killing it outright leaves the
        /// host with mount points that only <c>fusermount3 -uz</c> can clear.
        /// </summary>
        public void Terminate()
        {
            try
            {
                if (process.HasExited) return;

                if (NativeSignals.TrySendTerm(process.Id)
                    && process.WaitForExit((int)GracefulShutdownTimeout.TotalMilliseconds))
                {
                    return;
                }

                Log.Warning(
                    "The built-in rclone daemon did not exit within {Seconds}s of SIGTERM; killing it. " +
                    "Its mounts were released first, so this should not leave one behind.",
                    GracefulShutdownTimeout.TotalSeconds);

                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (Exception e) when (e is InvalidOperationException or NotSupportedException or Win32Exception)
            {
                // Already gone, already reaped, or the kill syscall was refused
                // because the process died between the check and the call.
            }
        }

        public void Dispose() => process.Dispose();
    }
}
