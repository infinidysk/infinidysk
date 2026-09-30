using NzbWebDAV.Utils;

namespace NzbWebDAV.Services.Library;

/// <summary>One filename rule for discovered links and catalog rows.</summary>
internal static class MediaLibraryVideoFilter
{
    public static bool IsVideoLink(SymlinkAndStrmUtil.ISymlinkOrStrmInfo info) => info switch
    {
        SymlinkAndStrmUtil.SymlinkInfo link => IsVideoLink(link.SymlinkPath, link.TargetPath),
        SymlinkAndStrmUtil.StrmInfo strm => IsVideoLink(strm.StrmPath, strm.TargetUrl),
        _ => false,
    };

    public static bool IsVideoLink(string linkPath, string targetText)
    {
        if (!linkPath.EndsWith(".strm", StringComparison.OrdinalIgnoreCase))
            return FilenameUtil.IsVideoFile(linkPath);
        var mediaPath = linkPath[..^".strm".Length];
        if (FilenameUtil.IsVideoFile(mediaPath)) return true;
        if (Uri.TryCreate(targetText, UriKind.Absolute, out var targetUri))
            return FilenameUtil.IsVideoFile(targetUri.AbsolutePath);
        return FilenameUtil.IsVideoFile(targetText);
    }
}
