using NzbWebDAV.Clients.Rclone;
using Serilog;

namespace NzbWebDAV.Services;

/// <summary>
/// Drops rclone's directory cache for paths InfiniDysk has just added or
/// removed, on every rclone that is serving them.
/// </summary>
/// <remarks>
/// Two things make this more than one call.
///
/// An installation can be serving through two rclones at once: the built-in
/// daemon and an external sidecar that is still mounted while the operator
/// moves across. Telling only one of them leaves the other showing files that
/// are gone and hiding files that are not, until its directory cache expires --
/// a week, with polling switched off.
///
/// And one rclone can hold several VFS instances, one per distinct remote it
/// mounts. rclone refuses an unqualified <c>vfs/forget</c> as soon as there is
/// more than one, so each has to be named, and paths have to be rewritten
/// relative to that VFS's own root: a VFS rooted at <c>remote:/content</c> knows
/// <c>/content/Movies</c> as <c>Movies</c>.
/// </remarks>
public static class RcloneVfsInvalidator
{
    /// <summary>
    /// Every rclone that may be serving InfiniDysk's tree right now.
    ///
    /// The built-in daemon does not replace the external one the moment it
    /// starts: the sidecar can still be mounted and read from all through the
    /// enable-and-import workflow.
    /// </summary>
    public static IReadOnlyList<IRcloneClient> Targets()
    {
        var targets = new List<IRcloneClient>();

        if (RcloneClient.Builtin is { Host: not null } builtin) targets.Add(builtin);

        if (RcloneClient.Current is { IsRemoteControlEnabled: true, Host: not null } external &&
            !ReferenceEquals(external, RcloneClient.Builtin))
        {
            targets.Add(external);
        }

        return targets;
    }

    public static async Task ForgetAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken)
    {
        if (paths.Count == 0) return;

        // Started together rather than one after the other. The targets are
        // independent rclones, and vfs/forget retries with a backoff that reaches
        // a minute, so a sidecar that has gone away would otherwise hold up the
        // daemon that is actually serving the library.
        var pending = Targets()
            .Select(target => ForgetOnAsync(target, paths, cancellationToken))
            .ToList();

        await Task.WhenAll(pending).ConfigureAwait(false);
    }

    private static async Task ForgetOnAsync(
        IRcloneClient client,
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken)
    {
        try
        {
            var roots = await ActiveRootsAsync(client, cancellationToken).ConfigureAwait(false);

            // One VFS, or a mount list we could not read: send the request
            // unqualified, which is what rclone documents for a single VFS and
            // what every single-mount install has always used. Naming it would
            // add a way for the call to fail -- an fs string rclone does not
            // match -- for no benefit. The paths are still translated: a lone
            // subtree mount reads a whole-tree path as one inside its own root.
            if (roots.Count <= 1)
            {
                var scoped = roots.Count == 1 ? Relativize(paths, roots[0].RemotePath) : paths;
                if (scoped.Count > 0)
                    await client.ForgetVfsPaths(scoped, fs: null, cancellationToken).ConfigureAwait(false);
                return;
            }

            foreach (var root in roots)
            {
                var scoped = Relativize(paths, root.RemotePath);
                if (scoped.Count == 0) continue;

                await client.ForgetVfsPaths(scoped, root.Fs, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Call sites are fire-and-forget; do not surface cancellation as
            // UnobservedTaskException.
        }
    }

    /// <summary>The distinct VFS instances an rclone is serving, by fs string.</summary>
    private static async Task<List<VfsRoot>> ActiveRootsAsync(
        IRcloneClient client,
        CancellationToken cancellationToken)
    {
        var mounts = await client.ListMounts(cancellationToken).ConfigureAwait(false);
        if (!mounts.Success)
        {
            // Not an answer, so it must not be read as "one VFS". The caller
            // falls back to the unqualified call, which is right for the single
            // mount an unreadable listing most often means.
            Log.Debug(
                "Could not list rclone's mounts before invalidating its directory cache: {Error}",
                mounts.Error ?? "unknown error");
            return [];
        }

        return (mounts.MountPoints ?? [])
            .Select(mount => mount.Fs)
            .Where(fs => !string.IsNullOrWhiteSpace(fs))
            .Distinct(StringComparer.Ordinal)
            .Select(fs => new VfsRoot(fs!, RcloneImportTranslator.ExtractRemotePath(fs)))
            .ToList();
    }

    /// <summary>
    /// Rewrites whole-tree paths into one VFS's own namespace, dropping the ones
    /// it cannot see.
    /// </summary>
    /// <remarks>
    /// rclone joins whatever it is given onto the VFS root after stripping
    /// leading slashes, and never reports a path it could not find. Sending
    /// <c>/content/Movies</c> to a VFS rooted at <c>remote:/content</c> therefore
    /// forgets <c>/content/content/Movies</c> -- silently, while the directory
    /// that actually changed stays cached. A path outside the root, such as
    /// <c>/.ids</c> for that same VFS, is dropped for the same reason.
    /// </remarks>
    internal static List<string> Relativize(IReadOnlyList<string> paths, string remotePath)
    {
        var root = remotePath.TrimEnd('/');
        if (root.Length == 0) return [.. paths];

        var prefix = root + "/";
        var scoped = new List<string>();

        foreach (var path in paths)
        {
            var normalized = path.StartsWith('/') ? path : "/" + path;

            if (string.Equals(normalized, root, StringComparison.Ordinal))
            {
                scoped.Add("");
                continue;
            }

            if (normalized.StartsWith(prefix, StringComparison.Ordinal))
                scoped.Add(normalized[prefix.Length..]);
        }

        return scoped;
    }

    private sealed record VfsRoot(string Fs, string RemotePath);
}
