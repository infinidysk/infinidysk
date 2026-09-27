using NzbWebDAV.Utils;

namespace NzbWebDAV.MediaLibrary;

/// <summary>
/// Pure classifier: maps one discovered library link to internal (InfiniDysk
/// <c>/.ids</c> target) or external. No filesystem IO — the scanner reads the
/// target text with no-follow APIs and passes it in.
/// </summary>
public static class LibraryLinkClassifier
{
    public static ClassifiedLink Classify(
        string linkPath,
        string targetText,
        bool isStrm,
        string mountDir,
        string libraryRoot)
    {
        var fullRoot = Path.GetFullPath(libraryRoot);
        var fullPath = Path.GetFullPath(linkPath);
        var relative = Path.GetRelativePath(fullRoot, fullPath);
        if (relative == ".."
            || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || Path.IsPathRooted(relative))
        {
            throw new ArgumentException($"Link '{linkPath}' escapes the library root.", nameof(linkPath));
        }

        Guid? davItemId;
        if (isStrm)
        {
            davItemId = OrganizedLinksUtil.GetDavItemLink(
                new SymlinkAndStrmUtil.StrmInfo { StrmPath = fullPath, TargetUrl = targetText })?.DavItemId;
        }
        else
        {
            davItemId = OrganizedLinksUtil.GetDavItemLink(
                new SymlinkAndStrmUtil.SymlinkInfo { SymlinkPath = fullPath, TargetPath = targetText },
                mountDir)?.DavItemId;
        }

        return new ClassifiedLink(
            relative,
            targetText,
            davItemId.HasValue ? LibraryMappingType.Internal : LibraryMappingType.External,
            davItemId);
    }
}
