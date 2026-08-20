namespace NzbWebDAV.Api.Errors;

public sealed class ApiValidationException : Exception
{
    public ApiValidationException(IReadOnlyDictionary<string, string[]> errors, string? message = null)
        : base(message ?? "One or more validation errors occurred.")
    {
        Errors = errors;
    }

    public IReadOnlyDictionary<string, string[]> Errors { get; }
}
