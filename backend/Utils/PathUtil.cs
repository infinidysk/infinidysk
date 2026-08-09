namespace NzbWebDAV.Utils;

public static class PathUtil
{
    public static IEnumerable<string> GetAllParentDirectories(string path)
    {
        var directoryName = Path.GetDirectoryName(path);
        return !string.IsNullOrEmpty(directoryName)
            ? GetAllParentDirectories(directoryName).Append(directoryName)
            : [];
    }

    public static string ReplaceExtension(string path, string? newExtensions)
    {
        var directoryName = Path.GetDirectoryName(path);
        var filenameWithoutExtension = Path.GetFileNameWithoutExtension(path);
        var trimmed = newExtensions?.Trim().TrimStart('.');
        var newFilename = string.IsNullOrEmpty(trimmed)
            ? filenameWithoutExtension
            : $"{filenameWithoutExtension}.{trimmed}";
        return Path.Join(directoryName, newFilename);
    }
}
