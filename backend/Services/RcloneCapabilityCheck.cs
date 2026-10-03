namespace NzbWebDAV.Services;

/// <summary>
/// The outcome of a built-in mount preflight: whether rclone can mount at all,
/// and if not, what the operator has to change.
/// </summary>
/// <param name="CanMount">True when nothing blocks mounting.</param>
/// <param name="BlockingReasons">
/// One actionable sentence per problem. Every reason names the concrete fix,
/// because the common failures are container flags the user must add, not bugs
/// they can debug from a stack trace.
/// </param>
public sealed record RcloneCapabilityResult(bool CanMount, IReadOnlyList<string> BlockingReasons);

/// <summary>
/// Checks whether this container can actually run a FUSE mount before the
/// daemon is started. Mounting fails for environmental reasons far more often
/// than for code reasons: the image ships rclone, but the container still needs
/// to be started with <c>--device /dev/fuse</c> and <c>--cap-add SYS_ADMIN</c>.
/// Reporting that plainly is the difference between a two-minute fix and a bug
/// report.
/// </summary>
public sealed class RcloneCapabilityCheck
{
    /// <summary>Where the Dockerfile installs rclone.</summary>
    public const string RcloneBinaryPath = "/usr/local/bin/rclone";

    /// <summary>The FUSE character device, present only when passed into the container.</summary>
    public const string FuseDevicePath = "/dev/fuse";

    /// <summary>The helper rclone shells out to when unmounting.</summary>
    public const string FusermountExecutable = "fusermount3";

    /// <summary>libfuse's configuration file, which gates <c>--allow-other</c>.</summary>
    public const string FuseConfPath = "/etc/fuse.conf";

    /// <summary>The setting every mount here needs, so other containers can read it.</summary>
    public const string AllowOtherSetting = "user_allow_other";

    /// <summary>The kernel's status file for this process, which lists its capability sets.</summary>
    public const string ProcessStatusPath = "/proc/self/status";

    /// <summary>CAP_SYS_ADMIN's bit in a capability mask, from linux/capability.h.</summary>
    private const int CapSysAdminBit = 21;

    /// <summary>The fix for a missing mount privilege, worded the same wherever it is reported.</summary>
    private const string SysAdminFix =
        "The container needs --cap-add SYS_ADMIN (and usually --security-opt apparmor:unconfined) in addition to " +
        "--device /dev/fuse.";

    /// <summary>Seam for tests; production checks the real filesystem.</summary>
    public Func<string, bool> FileExists { get; init; } = File.Exists;

    /// <summary>Seam for tests; production searches PATH.</summary>
    public Func<string, bool> ExecutableOnPath { get; init; } = IsOnPath;

    /// <summary>Seam for tests; production opens the real device.</summary>
    public Func<string, bool> CanOpenForWrite { get; init; } = IsWritable;

    /// <summary>Seam for tests; production reads the real file. Null means unreadable.</summary>
    public Func<string, string?> ReadFileText { get; init; } = ReadIfReadable;

    public RcloneCapabilityResult Evaluate()
    {
        var reasons = new List<string>();

        if (!FileExists(RcloneBinaryPath))
        {
            reasons.Add(
                $"The rclone binary is missing from {RcloneBinaryPath}. This image should ship it; " +
                "if you built a custom image, make sure the rclone install step ran.");
        }

        if (!FileExists(FuseDevicePath))
        {
            reasons.Add(
                $"{FuseDevicePath} is not available inside the container. Start InfiniDysk with " +
                "--device /dev/fuse and --cap-add SYS_ADMIN (most hosts also need " +
                "--security-opt apparmor:unconfined) so it can create a FUSE mount.");
        }
        else if (!CanOpenForWrite(FuseDevicePath))
        {
            // The device node can be present while the container still lacks the
            // privileges to use it, which otherwise fails at mount time with a
            // permission error that reads like a bug.
            reasons.Add($"{FuseDevicePath} exists but cannot be opened for writing. {SysAdminFix}");
        }
        else if (HasSysAdminCapability() == false)
        {
            // Opening the device is not permission to mount. That takes
            // CAP_SYS_ADMIN, which Docker withholds unless it is added, and
            // without it every mount fails with the same opaque permission error.
            reasons.Add($"The container does not have CAP_SYS_ADMIN, which a FUSE mount needs. {SysAdminFix}");
        }

        if (!ExecutableOnPath(FusermountExecutable))
        {
            reasons.Add(
                $"{FusermountExecutable} was not found on PATH. rclone needs it to unmount cleanly; " +
                "install the fuse3 package in your image.");
        }

        if (!AllowsOtherUsers())
        {
            // Every mount here is created with --allow-other, because the whole
            // point is that Plex and the Arr apps can read it. Without this line
            // libfuse refuses the option and every mount fails identically.
            reasons.Add(
                $"{FuseConfPath} does not enable '{AllowOtherSetting}'. Mounts are shared with your media " +
                $"server, which needs it. Add a line reading '{AllowOtherSetting}' to {FuseConfPath} in your " +
                "image; the official image already does this.");
        }

        return new RcloneCapabilityResult(reasons.Count == 0, reasons);
    }

    /// <summary>
    /// Whether libfuse is configured to let a non-root user pass
    /// <c>--allow-other</c>. Commented-out lines do not count.
    /// </summary>
    private bool AllowsOtherUsers()
    {
        var contents = ReadFileText(FuseConfPath);
        if (contents is null) return false;

        return contents
            .Split('\n')
            .Select(line => line.Trim())
            .Any(line => line.Equals(AllowOtherSetting, StringComparison.Ordinal));
    }

    /// <summary>
    /// Whether a mount made from this process can hold CAP_SYS_ADMIN, or null when
    /// the capability sets cannot be read -- off Linux, say -- in which case the
    /// check stays out of the way rather than guessing.
    /// </summary>
    private bool? HasSysAdminCapability() => ParseSysAdminCapability(ReadFileText(ProcessStatusPath));

    /// <remarks>
    /// The backend runs as the PUID user, whose effective set is empty even when
    /// the container was given SYS_ADMIN. rclone mounts through the setuid
    /// fusermount3, which gains the container's bounding set, so any other user
    /// is judged by that set and root by its effective one.
    /// </remarks>
    internal static bool? ParseSysAdminCapability(string? processStatus)
    {
        if (processStatus is null) return null;

        string[]? uids = null;
        string? effective = null;
        string? bounding = null;

        foreach (var line in processStatus.Split('\n'))
        {
            var separator = line.IndexOf(':', StringComparison.Ordinal);
            if (separator < 0) continue;

            var value = line[(separator + 1)..].Trim();
            switch (line[..separator])
            {
                case "Uid":
                    uids = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    break;
                case "CapEff":
                    effective = value;
                    break;
                case "CapBnd":
                    bounding = value;
                    break;
            }
        }

        // Uid lists the real, effective, saved and filesystem ids, in that order.
        if (uids is not { Length: >= 2 }) return null;

        var mask = uids[1] == "0" ? effective : bounding;
        if (!ulong.TryParse(
                mask,
                System.Globalization.NumberStyles.AllowHexSpecifier,
                System.Globalization.CultureInfo.InvariantCulture,
                out var bits))
        {
            return null;
        }

        return (bits & (1UL << CapSysAdminBit)) != 0;
    }

    private static bool IsOnPath(string executable)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path)) return false;

        return path
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(directory => File.Exists(Path.Combine(directory, executable)));
    }

    private static bool IsWritable(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    private static string? ReadIfReadable(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
