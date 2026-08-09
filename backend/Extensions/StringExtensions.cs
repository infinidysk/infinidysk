using System.Security.Cryptography;
using System.Text;

namespace NzbWebDAV.Extensions;

public static class StringExtensions
{
    public static bool IsAny(this string value, params string[] acceptedValues)
    {
        return acceptedValues.Any(value.FixedTimeEquals);
    }

    public static bool FixedTimeEquals(this string? value, string? acceptedValue)
    {
        if (value is null || acceptedValue is null) return false;

        var valueBytes = Encoding.UTF8.GetBytes(value);
        var acceptedValueBytes = Encoding.UTF8.GetBytes(acceptedValue);
        return CryptographicOperations.FixedTimeEquals(valueBytes, acceptedValueBytes);
    }

    public static string RemovePrefix(this string value, string prefix)
    {
        return value.StartsWith(prefix, StringComparison.Ordinal) ? value[prefix.Length..] : value;
    }
}
