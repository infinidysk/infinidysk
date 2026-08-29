using System.Collections.Concurrent;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;
using NzbWebDAV.Queue.PostProcessors;

namespace NzbWebDAV.Utils;

/// <summary>
/// Note: In this class, a `Link` refers to either a symlink or strm file.
/// </summary>
public static class OrganizedLinksUtil
{
    // Concurrent because health-check repairs and maintenance tasks can walk the library at the same time.
    private static readonly ConcurrentDictionary<Guid, string> Cache = new();

    /// <summary>
    /// Searches organized media library for a symlink or strm pointing to the given target
    /// </summary>
    /// <param name="targetDavItem">The given target</param>
    /// <param name="configManager">The application config</param>
    /// <returns>The path to a symlink or strm in the organized media library that points to the given target.</returns>
    public static string? GetLink(DavItem targetDavItem, ConfigManager configManager)
    {
        if (configManager.GetLibraryDir() == null)
            return null;

        return !TryGetLinkFromCache(targetDavItem, configManager, out var linkFromCache)
            ? SearchForLink(targetDavItem, configManager)
            : linkFromCache;
    }

    /// <summary>
    /// Enumerates all DavItemLinks within the organized media library that point to nzbdav dav-items.
    /// </summary>
    /// <param name="configManager">The application config</param>
    /// <returns>All DavItemLinks within the organized media library that point to nzbdav dav-items.</returns>
    public static IEnumerable<DavItemLink> GetLibraryDavItemLinks(ConfigManager configManager)
    {
        var libraryRoot = configManager.GetLibraryDir();
        if (libraryRoot == null)
            return [];

        var allSymlinksAndStrms = SymlinkAndStrmUtil.GetAllSymlinksAndStrms(libraryRoot);
        return GetDavItemLinks(allSymlinksAndStrms, configManager);
    }

    private static bool TryGetLinkFromCache
    (
        DavItem targetDavItem,
        ConfigManager configManager,
        out string? linkFromCache
    )
    {
        return Cache.TryGetValue(targetDavItem.Id, out linkFromCache)
               && Verify(linkFromCache, targetDavItem, configManager);
    }

    private static bool Verify(string linkFromCache, DavItem targetDavItem, ConfigManager configManager)
    {
        var mountDir = configManager.GetRcloneMountDir();
        var fileInfo = new FileInfo(linkFromCache);
        var symlinkOrStrmInfo = SymlinkAndStrmUtil.GetSymlinkOrStrmInfo(fileInfo);
        if (symlinkOrStrmInfo == null) return false;
        var davItemLink = GetDavItemLink(symlinkOrStrmInfo, mountDir);
        return davItemLink?.DavItemId == targetDavItem.Id;
    }

    internal static bool StillTargets(DavItemLink expected, ConfigManager configManager)
        => PathStillTargets(expected.LinkPath, expected.DavItemId, configManager);

    internal static bool PathStillTargets(
        string linkPath,
        Guid davItemId,
        ConfigManager configManager)
    {
        try
        {
            var libraryRoot = configManager.GetLibraryDir();
            if (string.IsNullOrWhiteSpace(libraryRoot))
                return false;

            var fullRoot = Path.GetFullPath(libraryRoot);
            var fullPath = Path.GetFullPath(linkPath);
            var relativePath = Path.GetRelativePath(fullRoot, fullPath);
            if (relativePath == ".."
                || relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || Path.IsPathRooted(relativePath))
            {
                return false;
            }

            var current = SymlinkAndStrmUtil.GetSymlinkOrStrmInfo(new FileInfo(fullPath));
            return current is not null
                   && GetDavItemLink(current, configManager.GetRcloneMountDir())?.DavItemId
                   == davItemId;
        }
        catch (Exception e) when (e is FileNotFoundException
                                      or DirectoryNotFoundException
                                      or ArgumentException
                                      or NotSupportedException
                                      or PathTooLongException)
        {
            return false;
        }
    }

    internal static QuarantinedLink QuarantineIfStillTargets(
        DavItemLink expected,
        ConfigManager configManager)
    {
        var libraryRoot = configManager.GetLibraryDir();
        if (string.IsNullOrWhiteSpace(libraryRoot))
            throw new InvalidOperationException("Library Directory is not configured.");

        var fullRoot = Path.GetFullPath(libraryRoot);
        var fullPath = Path.GetFullPath(expected.LinkPath);
        if (!StillTargets(expected, configManager))
            throw new InvalidOperationException($"Library link '{fullPath}' changed after it was scanned.");
        if (HasSymlinkedAncestorBelowRoot(fullPath, fullRoot))
        {
            throw new InvalidOperationException(
                $"Library link '{fullPath}' has a symlinked ancestor and cannot be removed safely.");
        }

        var extension = Path.GetExtension(fullPath);
        var quarantinePath = Path.Join(
            Path.GetDirectoryName(fullPath),
            $".{Path.GetFileNameWithoutExtension(fullPath)}.infinidysk-cleanup-{Guid.NewGuid():N}{extension}");
        File.Move(fullPath, quarantinePath);

        try
        {
            var current = SymlinkAndStrmUtil.GetSymlinkOrStrmInfo(new FileInfo(quarantinePath));
            if (current is null
                || GetDavItemLink(current, configManager.GetRcloneMountDir())?.DavItemId
                != expected.DavItemId)
            {
                throw new InvalidOperationException(
                    $"Library link '{fullPath}' changed while it was being quarantined.");
            }
        }
        catch
        {
            _ = TryRestoreQuarantinedLink(
                new QuarantinedLink(expected, fullPath, quarantinePath));
            throw;
        }

        Cache.TryRemove(expected.DavItemId, out _);
        return new QuarantinedLink(expected, fullPath, quarantinePath);
    }

    private static bool HasSymlinkedAncestorBelowRoot(string path, string root)
    {
        var normalizedRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var directoryPath = Path.GetDirectoryName(Path.GetFullPath(path));
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        while (directoryPath is not null)
        {
            var normalizedDirectory = directoryPath
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            // The scanner deliberately supports a configured library root that is itself
            // a symlink (-H on Linux). Only descendants below that trusted root are rejected.
            if (string.Equals(normalizedDirectory, normalizedRoot, comparison))
                return false;
            if (new DirectoryInfo(directoryPath).LinkTarget is not null)
                return true;

            directoryPath = Path.GetDirectoryName(directoryPath);
        }

        return true;
    }

    internal static bool TryRestoreQuarantinedLink(QuarantinedLink quarantined)
    {
        if (!PathEntryExists(quarantined.QuarantinePath))
            return false;
        if (PathEntryExists(quarantined.OriginalPath))
            return false;

        File.Move(quarantined.QuarantinePath, quarantined.OriginalPath);
        return true;
    }

    internal static bool QuarantinedLinkExists(QuarantinedLink quarantined) =>
        PathEntryExists(quarantined.QuarantinePath);

    internal static void DeleteQuarantinedLink(
        QuarantinedLink quarantined,
        ConfigManager configManager)
    {
        var current = SymlinkAndStrmUtil.GetSymlinkOrStrmInfo(
            new FileInfo(quarantined.QuarantinePath));
        if (current is null
            || GetDavItemLink(current, configManager.GetRcloneMountDir())?.DavItemId
            != quarantined.Expected.DavItemId)
        {
            throw new InvalidOperationException(
                $"Quarantined library link '{quarantined.QuarantinePath}' changed before deletion.");
        }

        File.Delete(quarantined.QuarantinePath);
    }

    internal static bool PathEntryExists(string path)
    {
        try
        {
            _ = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }

    private static string? SearchForLink(DavItem targetDavItem, ConfigManager configManager)
    {
        string? result = null;
        foreach (var davItemLink in GetLibraryDavItemLinks(configManager))
        {
            // cache every link found during the walk under its own dav-item id,
            // so subsequent lookups for other items skip the full library scan.
            Cache[davItemLink.DavItemId] = davItemLink.LinkPath;
            if (davItemLink.DavItemId == targetDavItem.Id)
                result = davItemLink.LinkPath;
        }

        return result;
    }

    private static IEnumerable<DavItemLink> GetDavItemLinks
    (
        IEnumerable<SymlinkAndStrmUtil.ISymlinkOrStrmInfo> symlinkOrStrmInfos,
        ConfigManager configManager
    )
    {
        var mountDir = configManager.GetRcloneMountDir();
        return symlinkOrStrmInfos
            .Select(x => GetDavItemLink(x, mountDir))
            .Where(x => x != null)
            .Select(x => x!.Value);
    }

    private static DavItemLink? GetDavItemLink
    (
        SymlinkAndStrmUtil.ISymlinkOrStrmInfo symlinkOrStrmInfo,
        string mountDir
    )
    {
        return symlinkOrStrmInfo switch
        {
            SymlinkAndStrmUtil.SymlinkInfo symlinkInfo => GetDavItemLink(symlinkInfo, mountDir),
            SymlinkAndStrmUtil.StrmInfo strmInfo => GetDavItemLink(strmInfo),
            _ => throw new InvalidOperationException("Unknown link type")
        };
    }

    internal static DavItemLink? GetDavItemLink(SymlinkAndStrmUtil.SymlinkInfo symlinkInfo, string mountDir)
    {
        var targetPath = symlinkInfo.TargetPath;
        if (!targetPath.StartsWith(mountDir, StringComparison.Ordinal)) return null;
        targetPath = targetPath.RemovePrefix(mountDir);
        targetPath = targetPath.StartsWith('/') ? targetPath : $"/{targetPath}";
        if (!targetPath.StartsWith("/.ids/", StringComparison.Ordinal)) return null;
        var guid = Path.GetFileNameWithoutExtension(targetPath);
        // a foreign/hand-made symlink under the mount dir must not abort the library walk
        if (!Guid.TryParse(guid, out var davItemId)) return null;
        return new DavItemLink()
        {
            LinkPath = symlinkInfo.SymlinkPath,
            DavItemId = davItemId,
            SymlinkOrStrmInfo = symlinkInfo
        };
    }

    internal static DavItemLink? GetDavItemLink(SymlinkAndStrmUtil.StrmInfo strmInfo)
    {
        var targetUrl = strmInfo.TargetUrl;
        // a malformed strm file must not abort the library walk
        if (!Uri.TryCreate(targetUrl, UriKind.Absolute, out var uri)) return null;
        var absolutePath = uri.AbsolutePath;
        if (!absolutePath.StartsWith("/view/.ids/", StringComparison.Ordinal)) return null;
        var guid = Path.GetFileNameWithoutExtension(absolutePath);
        if (!Guid.TryParse(guid, out var davItemId)) return null;
        return new DavItemLink()
        {
            LinkPath = strmInfo.StrmPath,
            DavItemId = davItemId,
            SymlinkOrStrmInfo = strmInfo
        };
    }

    public struct DavItemLink
    {
        public string LinkPath; // Path to either a symlink or strm file.
        public Guid DavItemId;
        public SymlinkAndStrmUtil.ISymlinkOrStrmInfo SymlinkOrStrmInfo;
    }

    internal sealed record QuarantinedLink(
        DavItemLink Expected,
        string OriginalPath,
        string QuarantinePath);

}
