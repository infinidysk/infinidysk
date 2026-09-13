namespace NzbWebDAV.Api.Errors;

/// <summary>
/// Collects field-level input errors and throws <see cref="ApiValidationException"/>
/// once, before queue, database, filesystem, or provider work.
/// Enforces bounded message counts and text limits so validation failures cannot
/// amplify into unbounded responses or memory consumption.
/// </summary>
public sealed class ValidationErrors
{
    public const int MaxTotalMessages = 31;
    public const int MaxMessagesPerField = 7;
    public const int MaxFieldLength = 128;
    public const int MaxMessageLength = 512;
    public const int MaxTotalTextLength = 4096;
    public const int MarkerReserve = 512;
    public const string OmissionMarker = "Additional validation errors were omitted.";
    public const string DefaultField = "request";

    private readonly Dictionary<string, List<string>> _errors = new(StringComparer.Ordinal);
    private readonly HashSet<(string Field, string Message)> _seenPairs = new();
    private int _totalMessages;
    private int _retainedTextLength = MarkerReserve;
    private bool _omitted;

    public bool HasErrors => _errors.Count > 0 || _omitted;

    public void Add(string field, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(field);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        var normField = TruncateString(field, MaxFieldLength);
        var normMessage = TruncateString(message, MaxMessageLength);

        if (!_seenPairs.Add((normField, normMessage)))
        {
            return;
        }

        if (normField.Length != field.Length || normMessage.Length != message.Length)
        {
            _omitted = true;
        }

        if (!_errors.TryGetValue(normField, out var list))
        {
            list = [];
        }

        var fieldTextCost = _errors.ContainsKey(normField) ? 0 : normField.Length;
        var totalCost = fieldTextCost + normMessage.Length;

        if (_totalMessages >= MaxTotalMessages ||
            list.Count >= MaxMessagesPerField ||
            _retainedTextLength + totalCost > MaxTotalTextLength)
        {
            _omitted = true;
            return;
        }

        if (!_errors.ContainsKey(normField))
        {
            _errors[normField] = list;
        }

        list.Add(normMessage);
        _totalMessages++;
        _retainedTextLength += totalCost;
    }

    public IReadOnlyDictionary<string, string[]> ToDictionary()
    {
        var result = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (_errors.Count == 0)
        {
            if (_omitted)
            {
                result[DefaultField] = [OmissionMarker];
            }
            return result;
        }

        var firstField = true;
        foreach (var (key, list) in _errors)
        {
            if (firstField && _omitted)
            {
                var copy = new string[list.Count + 1];
                list.CopyTo(copy, 0);
                copy[^1] = OmissionMarker;
                result[key] = copy;
            }
            else
            {
                result[key] = list.ToArray();
            }
            firstField = false;
        }

        return result;
    }

    public void ThrowIfAny()
    {
        if (!HasErrors)
            return;

        var snapshot = NormalizeSnapshot(ToDictionary(), null);
        throw new ApiValidationException(snapshot.Errors, snapshot.Summary);
    }

    public static (IReadOnlyDictionary<string, string[]> Errors, string Summary) NormalizeSnapshot(
        IReadOnlyDictionary<string, string[]> errors,
        string? customMessage)
    {
        var collector = new ValidationErrors();
        if (errors is not null)
        {
            foreach (var (field, messages) in errors)
            {
                if (messages is null) continue;
                foreach (var msg in messages)
                {
                    if (string.IsNullOrWhiteSpace(msg)) continue;
                    if (msg == OmissionMarker)
                    {
                        collector._omitted = true;
                    }
                    else
                    {
                        collector.Add(field, msg);
                    }
                }
            }
        }

        var snapshot = collector.ToDictionary();
        string summary;
        if (!string.IsNullOrWhiteSpace(customMessage))
        {
            summary = TruncateString(customMessage.Trim(), MaxMessageLength);
        }
        else
        {
            var firstMsg = snapshot.SelectMany(s => s.Value).FirstOrDefault(m => m != OmissionMarker)
                ?? snapshot.SelectMany(s => s.Value).FirstOrDefault();
            summary = firstMsg ?? "One or more validation errors occurred.";
        }

        return (snapshot, summary);
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

    private static string TruncateString(string value, int maxLength)
    {
        if (value.Length <= maxLength) return value;
        var sub = value[..maxLength];
        if (char.IsHighSurrogate(sub[^1]))
        {
            sub = sub[..^1];
        }
        return sub;
    }
}
