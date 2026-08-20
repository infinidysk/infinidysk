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

    Task<RcloneResponse> RefreshVfsPaths(
        IEnumerable<string> paths,
        bool recursive = false,
        CancellationToken cancellationToken = default);
    Task<VfsForgetResponse> ForgetVfsPaths(
        IEnumerable<string> paths,
        CancellationToken cancellationToken = default);
    Task<VfsStatsResponse> GetVfsStats(
        string? fs = null,
        CancellationToken cancellationToken = default);
    Task<CoreVersionResponse> GetVersion(CancellationToken cancellationToken = default);
    Task<RcloneResponse> NoOp(CancellationToken cancellationToken = default);
    Task<bool> IsAvailable(CancellationToken cancellationToken = default);
}
