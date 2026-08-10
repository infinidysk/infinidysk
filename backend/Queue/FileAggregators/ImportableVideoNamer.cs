using NzbWebDAV.Utils;

namespace NzbWebDAV.Queue.FileAggregators;

internal static class ImportableVideoNamer
{
    public static string Normalize(
        string leafName,
        string? sniffedVideoExtension,
        string mountName,
        bool allowBaseRename)
    {
        if (FilenameUtil.IsVideoFile(leafName))
            return leafName;

        if (sniffedVideoExtension is null)
            return leafName;

        var extWithoutDot = Path.GetExtension(leafName).TrimStart('.');
        if (extWithoutDot.Length > 0 && extWithoutDot.All(char.IsDigit))
            return leafName;

        var baseName = Path.GetFileNameWithoutExtension(leafName);
        if (string.IsNullOrEmpty(baseName))
            baseName = mountName;
        else if (allowBaseRename && ObfuscationUtil.IsProbablyObfuscated(leafName))
            baseName = mountName;

        return PathSanitizer.SanitizeComponent(baseName + sniffedVideoExtension);
    }
}
