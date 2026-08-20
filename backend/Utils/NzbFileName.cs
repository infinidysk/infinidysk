namespace NzbWebDAV.Utils;

/// <summary>
/// Resolve the NZB filename from an optional SAB <c>nzbname</c> param and the
/// uploaded file name. Throws <see cref="ArgumentException"/> when neither is usable.
/// </summary>
public static class NzbFileName
{
    public static string Resolve(string? nzbName, string? formFileName)
    {
        var fileName = !string.IsNullOrWhiteSpace(nzbName) ? nzbName : formFileName;

        if (string.IsNullOrWhiteSpace(fileName))
            throw new ArgumentException("NZB filename could not be determined.", nameof(formFileName));

        return NzbStreamUtil.NormalizeFileName(fileName);
    }
}
