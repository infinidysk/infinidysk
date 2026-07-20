namespace NzbWebDAV.UsenetMigration.Symlinks;

/// <summary>
/// The minimal filesystem surface needed to apply symlink rewrites while preserving
/// backup-first, drift-guard, idempotency, and never-delete behavior.
/// </summary>
public interface ISymlinkOps
{
    /// <summary>The current symlink target at <paramref name="path"/>, or null if the
    /// path is absent or not a symlink (even a broken symlink returns its target).</summary>
    string? ReadLink(string path);

    /// <summary>
    /// Point the symlink at <paramref name="path"/> to <paramref name="target"/>,
    /// replacing an existing symlink there. Removes only the link inode — never the
    /// pointed-at content — and refuses to touch a path that is a real (non-symlink)
    /// file or directory.
    /// </summary>
    void CreateOrReplaceSymlink(string path, string target);
}

/// <summary>Production <see cref="ISymlinkOps"/> over the real filesystem.</summary>
public sealed class RealSymlinkOps : ISymlinkOps
{
    public static readonly RealSymlinkOps Instance = new();

    public string? ReadLink(string path)
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

    public void CreateOrReplaceSymlink(string path, string target)
    {
        var existing = ReadLink(path);
        if (existing is not null)
        {
            // Delete only the link inode. Deleting a symlink never recurses into or
            // removes its target content.
            if (Directory.Exists(path) && new DirectoryInfo(path).LinkTarget is not null)
                Directory.Delete(path);
            else
                File.Delete(path);
        }
        else if (File.Exists(path) || Directory.Exists(path))
        {
            // A real file or directory lives here, so never replace it.
            throw new IOException($"Refusing to replace non-symlink at '{path}'.");
        }

        File.CreateSymbolicLink(path, target);
    }
}
