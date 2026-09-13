namespace NzbWebDAV.Api.Errors;

public sealed class ApiValidationException : Exception
{
    public const string HttpContextItemKey = "NzbWebDAV.ApiValidationException";

    public ApiValidationException(IReadOnlyDictionary<string, string[]> errors, string? message = null)
        : this(ValidationErrors.NormalizeSnapshot(errors, message))
    {
    }

    private ApiValidationException((IReadOnlyDictionary<string, string[]> Errors, string Summary) snapshot)
        : base(snapshot.Summary)
    {
        Errors = snapshot.Errors;
    }

    public IReadOnlyDictionary<string, string[]> Errors { get; }
}
