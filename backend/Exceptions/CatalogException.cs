namespace NzbWebDAV.Exceptions;

/// <summary>
/// Transport-neutral failure raised by the catalog/metadata stack. <see cref="SafeMessage"/>
/// is safe to return to the browser verbatim: provider responses, URLs and credentials
/// never reach it, so the API layer can map the exception without re-sanitizing.
/// </summary>
public sealed class CatalogException : Exception
{
    public const int ValidationFailed = 400;
    public const int TitleNotFound = 404;
    public const int Unprocessable = 422;
    public const int ProviderFailed = 502;
    public const int NotConfigured = 503;

    public CatalogException(int statusCode, string safeMessage, TimeSpan? retryAfter = null)
        : base(safeMessage)
    {
        StatusCode = statusCode;
        SafeMessage = safeMessage;
        RetryAfter = retryAfter;
    }

    public int StatusCode { get; }

    public string SafeMessage { get; }

    /// <summary>Optional <c>Retry-After</c> hint for transient provider failures.</summary>
    public TimeSpan? RetryAfter { get; }

    public static CatalogException Validation(string message)
        => new(ValidationFailed, message);

    public static CatalogException NotFound(string message = "Title not found.")
        => new(TitleNotFound, message);

    public static CatalogException UnprocessableEntity(string message)
        => new(Unprocessable, message);

    public static CatalogException Provider(string message, TimeSpan? retryAfter = null)
        => new(ProviderFailed, message, retryAfter);

    public static CatalogException NotConfiguredProvider(string message)
        => new(NotConfigured, message);
}
