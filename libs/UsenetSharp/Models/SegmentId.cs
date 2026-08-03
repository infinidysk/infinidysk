namespace UsenetSharp.Models;

public readonly struct SegmentId(string? value)
{
    private readonly string _value = value ?? string.Empty;

    public ReadOnlySpan<char> Value
    {
        get
        {
            if (string.IsNullOrEmpty(_value))
                return ReadOnlySpan<char>.Empty;

            var span = _value.AsSpan();

            // Remove leading '<' if present
            if (span.Length > 0 && span[0] == '<')
                span = span[1..];

            // Remove trailing '>' if present
            if (span.Length > 0 && span[^1] == '>')
                span = span[..^1];

            return span;
        }
    }

    public static implicit operator SegmentId(string value) => new(value);

    public static implicit operator string(SegmentId segmentId)
    {
        var value = segmentId.Value;
        return value.IsEmpty ? string.Empty : new string(value);
    }

    public override string ToString()
    {
        var value = Value;
        return value.IsEmpty ? string.Empty : new string(value);
    }
}
