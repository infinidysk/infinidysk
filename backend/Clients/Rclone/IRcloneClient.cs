using NzbWebDAV.Clients.Rclone.Models;

namespace NzbWebDAV.Clients.Rclone;

/// <summary>
/// rclone remote-control client. Implementations must not log credentials.
/// </summary>
public interface IRcloneClient
{
    string? Host { get; }
    bool IsRemoteControlEnabled { get; }
    (string Message, DateTimeOffset At)? LastForgetError { get; }

    Task<RcloneResponse> RefreshVfsPaths(IEnumerable<string> paths, bool recursive = false);
    Task<VfsForgetResponse> ForgetVfsPaths(IEnumerable<string> paths);
    Task<VfsStatsResponse> GetVfsStats(string? fs = null);
    Task<CoreVersionResponse> GetVersion();
    Task<RcloneResponse> NoOp();
    Task<bool> IsAvailable();
}
