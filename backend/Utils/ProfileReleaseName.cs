namespace NzbWebDAV.Utils;

public static class ProfileReleaseName
{
    public static string SanitizeFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "untitled";
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return string.IsNullOrEmpty(clean) ? "untitled" : clean;
    }

    public static string ToNzbFileName(string? title) => $"{SanitizeFileName(title)}.nzb";
}
