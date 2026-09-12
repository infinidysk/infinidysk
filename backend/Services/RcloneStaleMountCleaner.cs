using System.Collections.Concurrent;
using System.Diagnostics;
using Serilog;

namespace NzbWebDAV.Services;

/// <summary>One entry from the kernel's mount table.</summary>
/// <param name="MountPoint">Where it is mounted.</param>
/// <param name="FilesystemType">The kernel's name for it, such as <c>fuse.rclone</c>.</param>
public sealed record MountTableEntry(string MountPoint, string FilesystemType);

/// <summary>What a sweep did, so the caller can report it.</summary>
/// <param name="Cleared">Mount points that were stale and have been released.</param>
/// <param name="LeftAlone">Mount points that are mounted and still responding.</param>
/// <param name="Failed">Mount points that are stale but could not be released.</param>
public sealed record RcloneStaleMountSweep(
    IReadOnlyList<string> Cleared,
    IReadOnlyList<string> LeftAlone,
    IReadOnlyList<string> Failed);

/// <summary>
/// Clears FUSE mounts left behind by a previous run before the daemon starts.
///
/// A FUSE mount outlives the process that created it, so a container that was
/// killed rather than stopped — SIGKILL, the OOM killer, a crash — leaves its
/// mount in the kernel's table. rclone then refuses to mount over it with
/// "directory already mounted", and the library stays offline until someone
/// clears it by hand.
///
/// The danger in automating this is unmounting something that is still working:
/// an external rclone container serving the same path looks identical from here
/// if you only check whether a path is mounted. So two conditions must both
/// hold before anything is touched:
///
/// 1. the path is one of our own configured mount points, and the kernel says a
///    <c>fuse</c> filesystem is mounted exactly there; and
/// 2. that mount is dead — reading it fails, which is what a FUSE mount whose
///    daemon has gone does (<c>ENOTCONN</c>, "transport endpoint is not
///    connected").
///
/// A mount that still answers is left alone and reported, because something is
/// serving it and that something is not ours to kill.
/// </summary>
public sealed class RcloneStaleMountCleaner
{
    /// <summary>How long the unmount helper is given before it is abandoned.</summary>
    internal static readonly TimeSpan UnmountTimeout = TimeSpan.FromSeconds(5);

    /// <summary>How long a mount is given to answer before it is assumed alive.</summary>
    internal static readonly TimeSpan ResponsivenessTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Where the kernel exposes this process's view of the mount table.</summary>
    private const string MountInfoPath = "/proc/self/mountinfo";

    /// <summary>Seam for tests; production reads the kernel's mount table.</summary>
    public Func<IReadOnlyList<MountTableEntry>> ReadMountTable { get; init; } = ReadProcMountInfo;

    /// <summary>Seam for tests; production reads the directory to see if it answers.</summary>
    public Func<string, bool> IsResponsive { get; init; } = DefaultIsResponsive;

    /// <summary>Seam for tests; production shells out to the FUSE unmount helper.</summary>
    public Func<string, bool> Unmount { get; init; } = DefaultUnmount;

    /// <summary>
    /// Releases any dead mount sitting on one of <paramref name="mountPoints"/>.
    /// </summary>
    public RcloneStaleMountSweep Sweep(IEnumerable<string> mountPoints)
    {
        var cleared = new List<string>();
        var leftAlone = new List<string>();
        var failed = new List<string>();

        IReadOnlyList<MountTableEntry> table;
        try
        {
            table = ReadMountTable();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Without the mount table there is no safe way to tell a stale mount
            // from a live one, so nothing is touched.
            Log.Warning(e, "Could not read the mount table; skipping the stale-mount sweep.");
            return new RcloneStaleMountSweep([], [], []);
        }

        var fuseMounts = table
            .Where(entry => entry.FilesystemType.StartsWith("fuse", StringComparison.Ordinal))
            .Select(entry => Normalize(entry.MountPoint))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var mountPoint in mountPoints.Distinct(StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(mountPoint)) continue;

            var normalized = Normalize(mountPoint);
            if (!fuseMounts.Contains(normalized)) continue;

            if (IsResponsive(mountPoint))
            {
                // Something is serving this path and answering for it. It is not
                // this process's to remove.
                leftAlone.Add(mountPoint);
                continue;
            }

            if (Unmount(mountPoint))
                cleared.Add(mountPoint);
            else
                failed.Add(mountPoint);
        }

        return new RcloneStaleMountSweep(cleared, leftAlone, failed);
    }

    /// <summary>Writes what the sweep found to the log, in the operator's terms.</summary>
    public static void Report(RcloneStaleMountSweep sweep)
    {
        foreach (var mountPoint in sweep.Cleared)
        {
            Log.Warning(
                "Released a stale FUSE mount left on {MountPoint} by a previous run. This happens when " +
                "the container is killed rather than stopped.",
                mountPoint);
        }

        foreach (var mountPoint in sweep.LeftAlone)
        {
            Log.Warning(
                "{MountPoint} already has a working mount on it, so it was left alone. If that is an " +
                "external rclone container, stop it before the built-in mount can take over this path.",
                mountPoint);
        }

        foreach (var mountPoint in sweep.Failed)
        {
            Log.Warning(
                "A stale FUSE mount on {MountPoint} could not be released. Clear it on the host with " +
                "'fusermount3 -uz {MountPoint}' or 'umount -l {MountPoint}'.",
                mountPoint,
                mountPoint);
        }
    }

    /// <summary>
    /// Parses <c>/proc/self/mountinfo</c>. Its mount point is field 5, and the
    /// filesystem type follows the " - " separator, so neither is affected by the
    /// optional fields in between.
    /// </summary>
    internal static IReadOnlyList<MountTableEntry> ParseMountInfo(IEnumerable<string> lines)
    {
        var entries = new List<MountTableEntry>();

        foreach (var line in lines)
        {
            var separator = line.IndexOf(" - ", StringComparison.Ordinal);
            if (separator < 0) continue;

            var fields = line[..separator].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 5) continue;

            var after = line[(separator + 3)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (after.Length < 1) continue;

            // Paths are octal-escaped in mountinfo; spaces are the case that matters.
            entries.Add(new MountTableEntry(Unescape(fields[4]), after[0]));
        }

        return entries;
    }

    internal static string Unescape(string value) => value
        .Replace("\\040", " ", StringComparison.Ordinal)
        .Replace("\\011", "\t", StringComparison.Ordinal)
        .Replace("\\012", "\n", StringComparison.Ordinal)
        .Replace("\\134", "\\", StringComparison.Ordinal);

    private static IReadOnlyList<MountTableEntry> ReadProcMountInfo() =>
        ParseMountInfo(File.ReadAllLines(MountInfoPath));

    /// <summary>
    /// Whether the mount still answers. A FUSE mount whose daemon has gone fails
    /// every access with ENOTCONN, which is exactly what distinguishes a leftover
    /// from a mount that is still serving files.
    /// </summary>
    private static bool DefaultIsResponsive(string mountPoint) =>
        IsResponsiveWithin(mountPoint, () => CanEnumerate(mountPoint), ResponsivenessTimeout);

    /// <summary>
    /// The probe still running for a mount point, when one overran its budget.
    /// </summary>
    /// <remarks>
    /// A wedged mount's probe never returns, and sweeps repeat -- a daemon that
    /// exits keeps asking for one, and a crash loop asks on every restart. Each
    /// sweep would start another thread that blocks forever on the same path.
    /// Keeping the outstanding one means a wedged mount costs a single thread
    /// however many times it is swept, and the entry is dropped once the probe
    /// finally answers so a later sweep can ask again.
    /// </remarks>
    private static readonly ConcurrentDictionary<string, Task<bool>> OutstandingProbes = new(StringComparer.Ordinal);

    /// <summary>
    /// Runs a responsiveness probe under a deadline, reporting an unanswered
    /// probe as responsive.
    ///
    /// The sweep's evidence of staleness is a read that <em>fails</em>. A read
    /// that never returns is not that: a FUSE mount whose daemon is alive but
    /// wedged blocks in the kernel indefinitely, and this runs inline on the
    /// supervisor's loop. Waiting forever would stop the supervisor; assuming
    /// the mount is dead would detach something that may still be serving files.
    /// Leaving it alone is the recoverable choice of the two.
    /// </summary>
    internal static bool IsResponsiveWithin(string mountPoint, Func<bool> probe, TimeSpan budget)
    {
        // The probe runs on its own thread because a blocked FUSE read cannot be
        // cancelled; when it overruns, that thread stays blocked until the mount
        // is dealt with by hand. One outstanding probe per mount point, reused by
        // every later sweep, so a wedged mount cannot accumulate threads.
        var attempt = OutstandingProbes.GetOrAdd(mountPoint, _ => Task.Run(probe));

        if (!attempt.Wait(budget)) return true;

        // Answered, so the next sweep starts a fresh read rather than reusing
        // this verdict.
        OutstandingProbes.TryRemove(new KeyValuePair<string, Task<bool>>(mountPoint, attempt));

        // GetResult rather than Result so an unexpected failure surfaces
        // unwrapped, exactly as it did when the probe ran inline.
        return attempt.GetAwaiter().GetResult();
    }

    /// <summary>
    /// The errno a FUSE mount whose daemon has gone reports for every access.
    /// </summary>
    /// <remarks>
    /// Observed directly against rclone v1.75.1: mounting, killing the daemon,
    /// then enumerating gives <c>IOException</c> with <c>HResult</c> 107 and the
    /// message "Socket not connected". .NET puts the raw errno in HResult on
    /// Unix, and ENOTCONN is 107 on Linux.
    /// </remarks>
    private const int NotConnectedErrno = 107;

    internal static bool CanEnumerate(string mountPoint)
    {
        try
        {
            // Enumerating rather than existence-checking: a stale mount can still
            // satisfy Directory.Exists while every read fails.
            using var entries = Directory.EnumerateFileSystemEntries(mountPoint).GetEnumerator();
            entries.MoveNext();
            return true;
        }
        catch (IOException e) when (e.HResult == NotConnectedErrno)
        {
            // The one failure that actually proves the daemon is gone.
            return false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Anything else -- a permission error, a transient I/O fault -- says
            // nothing about whether something is still serving this path. Calling
            // it stale here would unmount a live library.
            Log.Debug(e, "Could not read {MountPoint}; treating it as live.", mountPoint);
            return true;
        }
    }

    /// <summary>
    /// Releases a mount with the FUSE helper. <c>fusermount3</c> rather than
    /// <c>umount</c> because it is setuid and works as the non-root user the
    /// backend runs as; <c>-z</c> detaches a mount whose daemon is already gone.
    /// </summary>
    private static bool DefaultUnmount(string mountPoint)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = RcloneCapabilityCheck.FusermountExecutable,
                ArgumentList = { "-u", "-z", mountPoint },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });

            if (process is null) return false;
            if (!process.WaitForExit((int)UnmountTimeout.TotalMilliseconds))
            {
                process.Kill(entireProcessTree: true);
                return false;
            }

            if (process.ExitCode == 0) return true;

            Log.Debug(
                "fusermount3 -uz {MountPoint} exited {ExitCode}: {Error}",
                mountPoint,
                process.ExitCode,
                process.StandardError.ReadToEnd().Trim());
            return false;
        }
        catch (Exception e)
        {
            Log.Debug(e, "Could not run fusermount3 for {MountPoint}.", mountPoint);
            return false;
        }
    }

    private static string Normalize(string path)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException)
        {
            return path;
        }
    }
}
