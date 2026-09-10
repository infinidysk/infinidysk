using NzbWebDAV.Clients.Rclone;
using NzbWebDAV.Config;

namespace NzbWebDAV.Services;

/// <summary>
/// What an import would do. Nothing is written until the user applies it.
/// </summary>
public sealed record RcloneImportPreview(
    bool Success,
    IReadOnlyList<RcloneMountConfig> Mounts,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Builds a built-in mount configuration from an rclone instance the user is
/// already running.
///
/// The mount settings are read from the live daemon over its remote-control API,
/// so the import reflects what rclone is actually running with rather than what a
/// Compose file claims. Nothing about the external instance is modified: it stays
/// exactly as it was, which is what makes rolling back simply a matter of turning
/// built-in mode off again.
///
/// Credentials are deliberately out of scope. The external daemon can hand over
/// its obscured WebDAV password, but nothing here applies it yet, and reading a
/// credential that is never used only widens what a bug could leak. The operator
/// enters the WebDAV password once instead.
/// </summary>
public sealed class RcloneImportService
{
    public async Task<RcloneImportPreview> PreviewAsync(
        IRcloneClient externalClient,
        string configPath,
        CancellationToken cancellationToken)
    {
        var warnings = new List<string>();

        var live = await externalClient.ListMounts(cancellationToken).ConfigureAwait(false);
        if (!live.Success)
        {
            warnings.Add(
                $"Could not read the external rclone's mounts: {live.Error ?? "unknown error"}. " +
                "Check the Rclone Server settings on this page, or import by pasting the mount command instead.");
            return new RcloneImportPreview(false, [], warnings);
        }

        // Both halves are needed: the mount point to reproduce, and the fs to ask
        // rclone about. A record missing either cannot be translated, and is
        // reported rather than dropped in silence.
        var usable = new List<Clients.Rclone.Models.RcloneMountPoint>();
        foreach (var candidate in live.MountPoints ?? [])
        {
            if (candidate.MountPoint is not null && candidate.Fs is not null)
            {
                usable.Add(candidate);
                continue;
            }

            warnings.Add(
                "The external rclone reported a mount without "
                + (candidate.MountPoint is null ? "a mount point" : "a remote")
                + ", so it was skipped.");
        }

        var mountPoints = usable;
        if (mountPoints.Count == 0)
        {
            warnings.Add(
                "The external rclone reports no mounts, so there is nothing to import. " +
                "Start its mount first, or configure the built-in mount manually.");
        }

        var mounts = new List<RcloneMountConfig>();
        foreach (var mountPoint in mountPoints)
        {
            var stats = await externalClient
                .GetVfsStats(mountPoint.Fs, cancellationToken)
                .ConfigureAwait(false);

            if (!stats.Success)
            {
                warnings.Add(
                    $"Could not read the VFS settings for '{mountPoint.MountPoint}'; " +
                    "InfiniDysk's defaults were used for it instead.");
            }

            mounts.Add(RcloneImportTranslator.Translate(mountPoint, stats.Success ? stats.Options : null));
        }

        // Surface the same rules the save path enforces, at preview time, so the
        // user is not told "invalid" only after committing to the cutover.
        warnings.AddRange(RcloneMountConfig.Validate(mounts, configPath));

        if (mounts.Count > 0)
        {
            warnings.Add(
                "Stop the external rclone container before applying this import. Two rclone " +
                "instances cannot serve the same mount point, and the mount path is kept " +
                "identical on purpose so existing symlinks keep resolving.");

            warnings.Add(
                "The WebDAV password is not copied across. Enter it once above after applying, " +
                "and the mount comes up.");
        }

        return new RcloneImportPreview(true, mounts, warnings);
    }
}
