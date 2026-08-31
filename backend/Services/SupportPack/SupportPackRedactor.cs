using System.Buffers;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NzbWebDAV.Config;

namespace NzbWebDAV.Services.SupportPack;

/// <summary>
/// Export-only privacy filter. It is intentionally separate from Settings'
/// reversible masking tokens: support archives must not carry opaque tokens
/// that can be replayed to recover a secret.
/// </summary>
internal sealed partial class SupportPackRedactor
{
    private static readonly HashSet<string> ScalarSecretKeys =
    [
        ConfigKeys.ApiKey,
        ConfigKeys.ApiStrmKey,
        ConfigKeys.RclonePass,
        ConfigKeys.WebdavPass,
        ConfigKeys.WatchtowerProfileToken,
    ];

    private static readonly HashSet<string> SecretPropertyNames =
    [
        "apikey", "api_key", "authorization", "cookie", "pass", "password",
        "secret", "strmkey", "token", "downloadkey",
    ];

    private readonly List<string> _literalSecrets;
    private readonly Dictionary<string, string> _ipAliases = new(StringComparer.Ordinal);

    public SupportPackRedactor(IEnumerable<string?> literalSecrets)
    {
        _literalSecrets = literalSecrets
            .Where(value => !string.IsNullOrWhiteSpace(value) && value!.Length >= 4)
            .Select(value => value!)
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(value => value.Length)
            .ToList();
    }

    public int SecretsRedacted { get; private set; }
    public int AddressesPseudonymized { get; private set; }

    public string RedactConfigurationValue(string key, string? value)
    {
        if (value is null)
            return "";
        if (ScalarSecretKeys.Contains(key))
        {
            SecretsRedacted++;
            return "[REDACTED]";
        }

        if (key is ConfigKeys.UsenetProviders or ConfigKeys.ArrInstances
            or ConfigKeys.IndexersInstances or ConfigKeys.ProfilesInstances)
        {
            try
            {
                return RedactJson(value);
            }
            catch (JsonException)
            {
                SecretsRedacted++;
                return "[REDACTED_MALFORMED_STRUCTURED_VALUE]";
            }
        }

        return RedactText(value);
    }

    public string RedactJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
            WriteRedactedJson(writer, document.RootElement);
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    public string RedactText(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return value ?? "";

        var redacted = value;
        foreach (var secret in _literalSecrets)
        {
            redacted = Replace(redacted, secret, "[REDACTED]");
            var encoded = Uri.EscapeDataString(secret);
            if (!string.Equals(encoded, secret, StringComparison.Ordinal))
                redacted = Replace(redacted, encoded, "[REDACTED]");
        }

        redacted = SensitiveQueryRegex().Replace(redacted, match =>
        {
            SecretsRedacted++;
            return $"{match.Groups[1].Value}{match.Groups[2].Value}=[REDACTED]";
        });
        redacted = AuthorizationRegex().Replace(redacted, match =>
        {
            SecretsRedacted++;
            return $"{match.Groups[1].Value}[REDACTED]";
        });
        redacted = MaskTokenRegex().Replace(redacted, _ =>
        {
            SecretsRedacted++;
            return "[REDACTED_MASK_TOKEN]";
        });
        redacted = UrlUserInfoRegex().Replace(redacted, match =>
        {
            SecretsRedacted++;
            return $"{match.Groups[1].Value}[REDACTED]@";
        });
        redacted = Ipv4Regex().Replace(redacted, PseudonymizeIp);
        return Ipv6Regex().Replace(redacted, PseudonymizeIp);
    }

    private string Replace(string value, string secret, string replacement)
    {
        var count = 0;
        var result = value.Replace(secret, replacement, StringComparison.Ordinal);
        for (var index = 0; index <= value.Length - secret.Length; index++)
        {
            if (value.AsSpan(index, secret.Length).Equals(secret, StringComparison.Ordinal))
                count++;
        }
        SecretsRedacted += count;
        return result;
    }

    private string PseudonymizeIp(Match match)
    {
        var candidate = match.Value.Trim('[', ']');
        if (!IPAddress.TryParse(candidate, out _))
            return match.Value;

        if (!_ipAliases.TryGetValue(candidate, out var alias))
        {
            alias = $"[IP-{_ipAliases.Count + 1}]";
            _ipAliases[candidate] = alias;
            AddressesPseudonymized++;
        }
        return alias;
    }

    private void WriteRedactedJson(Utf8JsonWriter writer, JsonElement element, string? propertyName = null)
    {
        if (propertyName is not null && SecretPropertyNames.Contains(Normalize(propertyName)))
        {
            SecretsRedacted++;
            writer.WriteStringValue("[REDACTED]");
            return;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    WriteRedactedJson(writer, property.Value, property.Name);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                    WriteRedactedJson(writer, item);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(RedactText(element.GetString()));
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static string Normalize(string value) =>
        value.Replace("-", "", StringComparison.Ordinal)
            .Replace("_", "", StringComparison.Ordinal)
            .ToLowerInvariant();

    [GeneratedRegex(@"([?&])(apikey|api_key|token|pass|password|auth|downloadkey)=([^&\s""']+)", RegexOptions.IgnoreCase)]
    private static partial Regex SensitiveQueryRegex();

    [GeneratedRegex(@"(Authorization:\s*(?:Bearer|Basic)\s+)\S+", RegexOptions.IgnoreCase)]
    private static partial Regex AuthorizationRegex();

    [GeneratedRegex(@"__NZBDAV_SECRET_MASK_V1__:[A-Za-z0-9_.-]+")]
    private static partial Regex MaskTokenRegex();

    [GeneratedRegex(@"((?:https?|socks5?)://)[^/\s@]+@", RegexOptions.IgnoreCase)]
    private static partial Regex UrlUserInfoRegex();

    // Version strings are dotted quads too: "Chrome/140.0.0.0" parses as a valid
    // IPv4 and would otherwise be pseudonymized, destroying the player/browser
    // identity that playback triage depends on. Reject candidates that are glued
    // to a letter (v1.2.3.4) or follow a "product/" prefix, while still matching
    // real addresses after "://", whitespace, quotes, "=", "@" and brackets.
    [GeneratedRegex(@"(?<![A-Za-z\d.])(?<![A-Za-z]/)\d{1,3}(?:\.\d{1,3}){3}(?![\d.])")]
    private static partial Regex Ipv4Regex();

    // Avoid treating timestamps, version numbers, and counters as IPv6. Full
    // numeric IPv6 addresses are still recognized when they use compression.
    [GeneratedRegex(@"(?<![A-Za-z0-9])(?:\[[0-9A-Fa-f:.]*[A-Fa-f][0-9A-Fa-f:.]*\]|[0-9A-Fa-f]*::[0-9A-Fa-f:]*|[0-9A-Fa-f]*[A-Fa-f][0-9A-Fa-f]*(?::[0-9A-Fa-f]+)+)(?![A-Za-z0-9])")]
    private static partial Regex Ipv6Regex();
}
