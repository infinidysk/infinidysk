namespace NzbWebDAV.Clients.Usenet.Contexts;

/// <summary>
/// Diagnostic-only file attribution for NNTP fetches. Unlike
/// <see cref="MultiProviderNntpClient.AttributionContext"/>, a non-null value here
/// must not change cache, repair-patch, or other fetch behavior.
/// </summary>
public sealed class FetchAttributionContext : IDisposable
{
    private static readonly AsyncLocal<FetchAttributionContext?> CurrentLocal = new();
    private readonly FetchAttributionContext? _previous;

    public FetchAttributionContext(string? fileName, string? category = null)
    {
        FileName = SanitizeFileName(fileName);
        Category = string.IsNullOrWhiteSpace(category) ? null : category.Trim();
        _previous = CurrentLocal.Value;
        CurrentLocal.Value = this;
    }

    public string? FileName { get; }
    public string? Category { get; }

    public static FetchAttributionContext? Current => CurrentLocal.Value;

    public static IDisposable Begin(string? fileName, string? category = null) =>
        new FetchAttributionContext(fileName, category);

    public void Dispose() => CurrentLocal.Value = _previous;

    private static string? SanitizeFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return null;

        var name = Path.GetFileName(fileName.Trim());
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }
}
