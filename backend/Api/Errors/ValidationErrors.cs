namespace NzbWebDAV.Api.Errors;

/// <summary>
/// Collects field-level input errors and throws <see cref="ApiValidationException"/>
/// once, before queue, database, filesystem, or provider work.
/// InfiniDysk uses this manual collector (not FluentValidation / DataAnnotations)
/// so SAB quirks and existing typed request parsers stay in control.
/// </summary>
public sealed class ValidationErrors
{
    private readonly Dictionary<string, List<string>> _errors = new(StringComparer.Ordinal);

    public bool HasErrors => _errors.Count > 0;

    public void Add(string field, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(field);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        if (!_errors.TryGetValue(field, out var list))
        {
            list = [];
            _errors[field] = list;
        }

        list.Add(message);
    }

    public IReadOnlyDictionary<string, string[]> ToDictionary() =>
        _errors.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value.ToArray(),
            StringComparer.Ordinal);

    public void ThrowIfAny()
    {
        if (_errors.Count == 0)
            return;

        var summary = _errors.SelectMany(static pair => pair.Value).First();
        throw new ApiValidationException(ToDictionary(), summary);
    }

    public bool TryParseInt(string field, string? raw, string invalidMessage, out int value)
    {
        value = default;
        if (raw is null)
            return false;
        if (int.TryParse(raw, out value))
            return true;
        Add(field, invalidMessage);
        return false;
    }
}
