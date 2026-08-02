using Serilog;

namespace NzbWebDAV.UsenetMigration.Symlinks;

/// <summary>
/// The minimal filesystem surface needed to rewrite, remove, and restore symlinks
/// while preserving backup-first, drift-guard, and idempotency behavior.
/// </summary>
public interface ISymlinkOps
{
    /// <summary>The current symlink target at <paramref name="path"/>, or null if the
    /// path is absent or not a symlink (even a broken symlink returns its target).
    /// The path must be contained by <paramref name="libraryRoot"/> without traversing
    /// a symlink or reparse point in its parent chain.</summary>
    string? ReadLink(string libraryRoot, string path);

    /// <summary>
    /// Check-and-swap: replace the symlink at <paramref name="path"/> only when its
    /// current target equals <paramref name="expectedOldTarget"/>, pointing it at
    /// <paramref name="newTarget"/>. Removes only the link inode — never the
    /// pointed-at content — and refuses to touch a path that is a real (non-symlink)
    /// file or directory. If create fails after delete, best-effort recreates the
    /// old link before rethrowing.
    /// </summary>
    void ReplaceSymlink(string libraryRoot, string path, string expectedOldTarget, string newTarget);

    /// <summary>
    /// Delete the symlink at <paramref name="path"/> only when its current target
    /// still equals <paramref name="expectedTarget"/>. Removes only the link inode,
    /// never its target, and refuses real files or directories.
    /// </summary>
    void DeleteSymlink(string libraryRoot, string path, string expectedTarget);

    /// <summary>
    /// Create a symlink at an entirely absent path. Refuses if anything already
    /// exists there (symlink or real file/dir). Used by restore when the link was
    /// stranded between delete and recreate.
    /// </summary>
    void CreateSymlink(string libraryRoot, string path, string target);
}

/// <summary>Production <see cref="ISymlinkOps"/> over the real filesystem.</summary>
public sealed class RealSymlinkOps : ISymlinkOps
{
    public static readonly RealSymlinkOps Instance = new();

    /// <summary>Test-only fault injection at the final leaf validation boundary.</summary>
    internal Action<string>? BeforeFinalLeafValidation { get; init; }

    /// <summary>Test-only fault injection immediately before creating the new link.</summary>
    internal Action<string>? BeforeCreateSymlink { get; init; }

    public string? ReadLink(string libraryRoot, string path)
    {
        var safePath = SymlinkPathGuard.RequireSafeParentChain(libraryRoot, path);
        return ReadLinkUnchecked(safePath);
    }

    public void ReplaceSymlink(
        string libraryRoot, string path, string expectedOldTarget, string newTarget)
    {
        var safePath = SymlinkPathGuard.RequireSafeParentChain(libraryRoot, path);
        var existing = ReadLinkUnchecked(safePath);

        // Re-check after inspecting the leaf so an already-swapped parent is
        // rejected before the following delete.
        safePath = SymlinkPathGuard.RequireSafeParentChain(libraryRoot, safePath);
        if (existing is not null)
        {
            BeforeFinalLeafValidation?.Invoke(safePath);
            var current = ReadLinkUnchecked(safePath);
            if (current is null)
            {
                throw new IOException(
                    $"Refusing to replace '{safePath}' because it is no longer the expected symlink.");
            }
            if (!string.Equals(current, expectedOldTarget, SymlinkPathGuard.PathComparison))
            {
                throw new IOException(
                    $"Refusing to replace '{safePath}' because its symlink target changed during replacement.");
            }

            // Delete only the link inode. Deleting a symlink never recurses into or
            // removes its target content.
            if (Directory.Exists(safePath) && new DirectoryInfo(safePath).LinkTarget is not null)
                Directory.Delete(safePath);
            else
                File.Delete(safePath);
        }
        else if (File.Exists(safePath) || Directory.Exists(safePath))
        {
            // A real file or directory lives here, so never replace it.
            throw new IOException($"Refusing to replace non-symlink at '{safePath}'.");
        }
        else
        {
            // Path entirely absent — only restore may create from scratch; apply
            // always expects an existing link matching expectedOldTarget.
            throw new IOException(
                $"Refusing to replace '{safePath}' because no symlink is present.");
        }

        // The delete/create boundary cannot be expressed as one managed filesystem
        // operation. Validate the still-existing parent again immediately before
        // creating the replacement link. On create failure, recreate the OLD link
        // so restore archives are not the only recovery path for a stranded hole.
        safePath = SymlinkPathGuard.RequireSafeParentChain(libraryRoot, safePath);
        try
        {
            BeforeCreateSymlink?.Invoke(safePath);
            File.CreateSymbolicLink(safePath, newTarget);
        }
        catch (Exception createError)
        {
            Exception? recreateError = null;
            try
            {
                if (ReadLinkUnchecked(safePath) is null
                    && !File.Exists(safePath)
                    && !Directory.Exists(safePath))
                {
                    File.CreateSymbolicLink(safePath, expectedOldTarget);
                }
            }
            catch (Exception e)
            {
                recreateError = e;
            }

            if (recreateError is not null)
            {
                Log.Error(
                    createError,
                    "Symlink create failed at {Path} and recreating the previous target also failed. " +
                    "Restore the link from the migration backup archive. Recreate error: {RecreateReason}",
                    safePath, recreateError.Message);
                Log.Debug(recreateError, "Symlink recreate failure stack at {Path}", safePath);
                throw new IOException(
                    $"Failed to create symlink at '{safePath}' and could not recreate the previous target; " +
                    "restore the link from the migration backup archive. " +
                    $"Create: {createError.Message}; Recreate: {recreateError.Message}",
                    createError);
            }

            Log.Warning(
                "Symlink create failed at {Path}; previous target was recreated. Reason: {Reason}",
                safePath, createError.Message);
            Log.Debug(createError, "Symlink create failure stack at {Path}", safePath);
            throw;
        }
    }

    public void CreateSymlink(string libraryRoot, string path, string target)
    {
        var safePath = SymlinkPathGuard.RequireSafeParentChain(libraryRoot, path);
        if (ReadLinkUnchecked(safePath) is not null
            || File.Exists(safePath)
            || Directory.Exists(safePath))
        {
            throw new IOException($"Refusing to create symlink over existing path at '{safePath}'.");
        }

        safePath = SymlinkPathGuard.RequireSafeParentChain(libraryRoot, safePath);
        File.CreateSymbolicLink(safePath, target);
    }

    public void DeleteSymlink(string libraryRoot, string path, string expectedTarget)
    {
        // First pass: resolve and validate the path, then classify what is on disk.
        var safePath = SymlinkPathGuard.RequireSafeParentChain(libraryRoot, path);
        var existing = ReadLinkUnchecked(safePath);

        // Second pass (TOCTOU hardening): re-validate the parent chain so a
        // concurrent path swap between the first check and the delete cannot turn
        // an approved link deletion into removal of an unrelated filesystem entry.
        safePath = SymlinkPathGuard.RequireSafeParentChain(libraryRoot, safePath);
        if (existing is null)
        {
            if (File.Exists(safePath) || Directory.Exists(safePath))
                throw new IOException($"Refusing to delete non-symlink at '{safePath}'.");
            throw new IOException($"Refusing to delete '{safePath}' because no symlink is present.");
        }

        BeforeFinalLeafValidation?.Invoke(safePath);
        var current = ReadLinkUnchecked(safePath);
        if (current is null)
        {
            throw new IOException(
                $"Refusing to delete '{safePath}' because it is no longer the expected symlink.");
        }
        if (!string.Equals(current, expectedTarget, SymlinkPathGuard.PathComparison))
        {
            throw new IOException(
                $"Refusing to delete '{safePath}' because its symlink target changed during removal.");
        }

        var attrs = File.GetAttributes(safePath);
        if ((attrs & FileAttributes.Directory) != 0)
            Directory.Delete(safePath);
        else
            File.Delete(safePath);
    }

    private static string? ReadLinkUnchecked(string path)
    {
        try
        {
            var attrs = File.GetAttributes(path);
            if ((attrs & FileAttributes.ReparsePoint) == 0)
                return null;
            return attrs.HasFlag(FileAttributes.Directory)
                ? new DirectoryInfo(path).LinkTarget
                : new FileInfo(path).LinkTarget;
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }
}

internal static class SymlinkPathGuard
{
    internal static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    internal static string RequireRealLibraryRoot(string libraryRoot)
    {
        if (string.IsNullOrWhiteSpace(libraryRoot))
            throw new IOException("The configured Library Root is missing.");

        var root = Path.GetFullPath(libraryRoot);
        EnsureRealDirectory(root, "The configured Library Root");
        return root;
    }

    internal static string RequireSafeParentChain(string libraryRoot, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new IOException("The symlink path is missing.");

        var root = RequireRealLibraryRoot(libraryRoot);
        var fullPath = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(root, fullPath);
        if (relative == "." || IsOutsideRoot(relative))
            throw new IOException($"Refusing to access symlink outside the configured Library Root: '{fullPath}'.");

        var parent = Path.GetDirectoryName(fullPath)
                     ?? throw new IOException($"The symlink path has no parent directory: '{fullPath}'.");
        var relativeParent = Path.GetRelativePath(root, parent);
        if (relativeParent != ".")
        {
            var current = root;
            foreach (var segment in relativeParent.Split(
                         [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                         StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                EnsureRealDirectory(current, "Symlink parent directory");
            }
        }

        return fullPath;
    }

    private static bool IsOutsideRoot(string relative) =>
        Path.IsPathRooted(relative)
        || relative == ".."
        || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        || relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);

    private static void EnsureRealDirectory(string path, string description)
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(path);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new IOException($"{description} does not exist: '{path}'.", e);
        }

        if (!attributes.HasFlag(FileAttributes.Directory))
            throw new IOException($"{description} is not a directory: '{path}'.");
        if (attributes.HasFlag(FileAttributes.ReparsePoint) || new DirectoryInfo(path).LinkTarget is not null)
            throw new IOException($"{description} is a symbolic link or reparse point: '{path}'.");
    }
}
