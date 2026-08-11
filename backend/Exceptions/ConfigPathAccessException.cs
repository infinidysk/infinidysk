using System.Runtime.InteropServices;

namespace NzbWebDAV.Exceptions;

/// <summary>
/// Fatal startup error when CONFIG_PATH or a state file beneath it cannot be read
/// or written by the process user. Carries one operator-actionable message (offending
/// paths plus the ownership fix) instead of an unhandled UnauthorizedAccessException
/// that core-dumps before anyone sees it.
/// </summary>
public sealed class ConfigPathAccessException(string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public static ConfigPathAccessException ForPath(
        string path,
        string configPath,
        Exception? innerException = null) =>
        ForPaths(configPath, [path], innerException);

    public static ConfigPathAccessException ForPaths(
        string configPath,
        IReadOnlyCollection<string> offendingPaths,
        Exception? innerException = null)
    {
        var paths = string.Join('\n', offendingPaths.Select(p => $"  - {p}"));
        var symlinkNote = OperatingSystem.IsWindows()
            ? ""
            : " If the config path is a symlink, apply the fix to its real target" +
              " (chown -R does not traverse symlinks; use a trailing slash).";
        return new ConfigPathAccessException(
            $"Cannot read or write configuration path(s) under '{configPath}':\n{paths}\n" +
            $"Fix ownership/permissions so {CurrentIdentity()} can read and write them " +
            $"(for example: chown -R <puid>:<pgid> '{configPath}').{symlinkNote}",
            innerException);
    }

    private static string CurrentIdentity()
    {
        if (OperatingSystem.IsWindows())
            return $"the account running NzbDAV ({Environment.UserName})";
        try
        {
            return $"the configured PUID/PGID (currently uid={geteuid()}, gid={getegid()})";
        }
        catch (Exception)
        {
            return $"the configured PUID/PGID (currently {Environment.UserName})";
        }
    }

    [DllImport("libc")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern uint geteuid();

    [DllImport("libc")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern uint getegid();
}
