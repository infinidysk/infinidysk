namespace NzbWebDAV.UsenetMigration.Source;

/// <summary>
/// Matches Altmount's inline category sanitizer in
/// <c>internal/importer/processor.go</c> / queue handlers (verified against
/// javi11/altmount main during the migration wizard development).
///
/// The entire transform: backslashes → forward slashes; trim leading/trailing
/// slashes; if any path segment is "." or "..", blank the WHOLE category. No
/// case-folding, no character stripping, no length limits, no whitespace
/// handling.
/// </summary>
public static class CategorySanitizer
{
    public static string Sanitize(string? category)
    {
        if (string.IsNullOrEmpty(category)) return "";

        var s = category.Replace('\\', '/').Trim('/');
        foreach (var part in s.Split('/'))
        {
            if (part is ".." or ".")
                return "";
        }

        return s;
    }
}
