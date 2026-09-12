using System.Security.Cryptography;

namespace NzbWebDAV.Services;

/// <summary>
/// How the built-in rclone daemon is launched.
///
/// The daemon is an <c>rclone rcd</c> process bound to loopback. Everything else
/// (creating the WebDAV remote, mounting, unmounting) happens over its remote
/// control API rather than through extra processes, so a mount can be added or
/// changed without restarting anything.
///
/// The RC credentials are generated per process start and never persisted: they
/// exist only to stop anything else in the container from driving our daemon.
/// </summary>
public sealed class RcloneDaemonOptions
{
    public required int RcPort { get; init; }
    public required string ConfigFilePath { get; init; }
    public required string CacheDir { get; init; }
    public required string RcUser { get; init; }
    public required string RcPass { get; init; }

    /// <summary>Address the backend's RC client talks to.</summary>
    public string BaseUrl => $"http://127.0.0.1:{RcPort}";

    /// <summary>
    /// Binding to 127.0.0.1 rather than all interfaces keeps the RC API
    /// unreachable from outside the container, which matters because it can
    /// mount filesystems and read the rclone config.
    /// </summary>
    public IReadOnlyList<string> ToArguments() =>
    [
        "rcd",
        "--rc-addr", $"127.0.0.1:{RcPort}",
        "--config", ConfigFilePath,
        "--cache-dir", CacheDir,

        // rclone holds this much in memory for every transfer in flight, which
        // multiplies across concurrent streams. The VFS read-ahead buffers on
        // disk instead, so the memory buys nothing.
        "--buffer-size=0",

        // Keeps a session cookie jar, matching the sidecar configuration this
        // project documents.
        "--use-cookies",
    ];

    /// <summary>
    /// The RC credentials, passed through the child process environment rather
    /// than the command line. rclone reads every flag from <c>RCLONE_</c>-prefixed
    /// variables, and an argument would be readable in <c>ps</c> by anything else
    /// sharing the container.
    /// </summary>
    public IReadOnlyDictionary<string, string> ToEnvironment() =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["RCLONE_RC_USER"] = RcUser,
            ["RCLONE_RC_PASS"] = RcPass,
        };

    /// <summary>The command line, for logs and support packs.</summary>
    /// <remarks>
    /// Safe to log as-is: credentials travel in the environment, never in
    /// <see cref="ToArguments"/>.
    /// </remarks>
    public string Describe() => $"rclone {string.Join(' ', ToArguments())}";

    /// <summary>
    /// Whether a running daemon started with <paramref name="other"/> is already
    /// serving these settings. The generated password is deliberately excluded:
    /// it differs on every build and is not a reason to restart a healthy daemon.
    /// </summary>
    public bool HasSameSettingsAs(RcloneDaemonOptions? other) =>
        other is not null
        && RcPort == other.RcPort
        && string.Equals(ConfigFilePath, other.ConfigFilePath, StringComparison.Ordinal)
        && string.Equals(CacheDir, other.CacheDir, StringComparison.Ordinal)
        && string.Equals(RcUser, other.RcUser, StringComparison.Ordinal);

    public static string GenerateRcPassword() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
}
