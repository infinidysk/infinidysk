using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace NzbWebDAV.Config;

public sealed class ConfigSecretMasker(string signingKey)
{
    public const string MaskPrefix = "__NZBDAV_SECRET_MASK_V1__:";

    private static readonly HashSet<string> ScalarSecretConfigNames =
    [
        "api.strm-key",
        "prowlarr.api-key",
        "rclone.pass",
        "webdav.pass"
    ];

    private static readonly Dictionary<string, string> JsonSecretProperties = new(StringComparer.Ordinal)
    {
        ["arr.instances"] = "ApiKey",
        ["indexers.instances"] = "ApiKey",
        ["usenet.providers"] = "Pass"
    };

    private readonly byte[] _signingKey = Encoding.UTF8.GetBytes(signingKey);

    public string MaskForResponse(string configName, string configValue)
    {
        if (ScalarSecretConfigNames.Contains(configName))
            return CreateToken(configName, "value", configValue);

        if (!JsonSecretProperties.TryGetValue(configName, out var propertyName))
            return configValue;

        try
        {
            using var document = JsonDocument.Parse(configValue);
            ValidateJsonSecretShape(document.RootElement, propertyName);
            return TransformJson(writer =>
                WriteMaskedJson(writer, document.RootElement, configName, propertyName));
        }
        catch (JsonException)
        {
            return CreateToken(configName, "raw-json", configValue);
        }
    }

    public string ResolveForUpdate(string configName, string submittedValue, string? existingValue)
    {
        if (ScalarSecretConfigNames.Contains(configName))
        {
            if (!IsMaskToken(submittedValue))
                return submittedValue;

            if (existingValue != null &&
                TokenMatches(submittedValue, configName, "value", existingValue))
                return existingValue;

            throw InvalidMaskToken(configName);
        }

        if (!JsonSecretProperties.TryGetValue(configName, out var propertyName))
        {
            if (submittedValue.Contains(MaskPrefix, StringComparison.Ordinal))
                throw InvalidMaskToken(configName);

            return submittedValue;
        }

        if (IsMaskToken(submittedValue))
        {
            if (existingValue != null &&
                TokenMatches(submittedValue, configName, "raw-json", existingValue))
                return existingValue;

            throw InvalidMaskToken(configName);
        }

        try
        {
            using var submittedDocument = JsonDocument.Parse(submittedValue);
            var existingSecrets = GetExistingJsonSecrets(existingValue, propertyName);
            return TransformJson(writer =>
                WriteResolvedJson(
                    writer,
                    submittedDocument.RootElement,
                    configName,
                    propertyName,
                    existingSecrets,
                    false));
        }
        catch (JsonException)
        {
            if (submittedValue.Contains(MaskPrefix, StringComparison.Ordinal))
                throw InvalidMaskToken(configName);

            return submittedValue;
        }
    }

    public static bool IsMaskToken(string value)
    {
        return value.StartsWith(MaskPrefix, StringComparison.Ordinal);
    }

    /// <summary>
    /// Resolves a single masked JSON secret (e.g. a usenet provider <c>Pass</c>) against
    /// the stored config blob. Plaintext values are returned unchanged.
    /// </summary>
    public string ResolveMaskedJsonSecret(string configName, string submittedValue, string? existingValue)
    {
        if (!IsMaskToken(submittedValue))
            return submittedValue;

        if (!JsonSecretProperties.TryGetValue(configName, out var propertyName))
            throw InvalidMaskToken(configName);

        var existingSecrets = GetExistingJsonSecrets(existingValue, propertyName);
        var existingSecret = existingSecrets.FirstOrDefault(secret =>
            TokenMatches(submittedValue, configName, propertyName, secret));
        if (existingSecret == null)
            throw InvalidMaskToken(configName);

        return existingSecret;
    }

    private void WriteMaskedJson(
        Utf8JsonWriter writer,
        JsonElement element,
        string configName,
        string propertyName,
        bool isSecret = false)
    {
        if (isSecret && element.ValueKind == JsonValueKind.String)
        {
            writer.WriteStringValue(CreateToken(configName, propertyName, element.GetString()!));
            return;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    WriteMaskedJson(
                        writer,
                        property.Value,
                        configName,
                        propertyName,
                        property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase));
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                    WriteMaskedJson(writer, item, configName, propertyName);
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    private void WriteResolvedJson(
        Utf8JsonWriter writer,
        JsonElement element,
        string configName,
        string propertyName,
        IReadOnlyCollection<string> existingSecrets,
        bool isSecret)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            var submittedValue = element.GetString()!;
            if (IsMaskToken(submittedValue))
            {
                if (!isSecret)
                    throw InvalidMaskToken(configName);

                var existingSecret = existingSecrets.FirstOrDefault(secret =>
                    TokenMatches(submittedValue, configName, propertyName, secret));
                if (existingSecret == null)
                    throw InvalidMaskToken(configName);

                writer.WriteStringValue(existingSecret);
                return;
            }

            writer.WriteStringValue(submittedValue);
            return;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    WriteResolvedJson(
                        writer,
                        property.Value,
                        configName,
                        propertyName,
                        existingSecrets,
                        property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase));
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                    WriteResolvedJson(writer, item, configName, propertyName, existingSecrets, false);
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static List<string> GetExistingJsonSecrets(string? existingValue, string propertyName)
    {
        if (existingValue == null)
            return [];

        try
        {
            using var document = JsonDocument.Parse(existingValue);
            var secrets = new List<string>();
            CollectJsonSecrets(document.RootElement, propertyName, secrets);
            return secrets;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static void ValidateJsonSecretShape(
        JsonElement element,
        string propertyName,
        bool isSecret = false)
    {
        if (isSecret &&
            element.ValueKind != JsonValueKind.String &&
            element.ValueKind != JsonValueKind.Null)
            throw new JsonException($"`{propertyName}` must be a string or null.");

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                ValidateJsonSecretShape(
                    property.Value,
                    propertyName,
                    property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase));
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                ValidateJsonSecretShape(item, propertyName);
        }
    }

    private static void CollectJsonSecrets(
        JsonElement element,
        string propertyName,
        ICollection<string> secrets,
        bool isSecret = false)
    {
        if (isSecret && element.ValueKind == JsonValueKind.String)
        {
            secrets.Add(element.GetString()!);
            return;
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                CollectJsonSecrets(
                    property.Value,
                    propertyName,
                    secrets,
                    property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase));
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                CollectJsonSecrets(item, propertyName, secrets);
        }
    }

    private static string TransformJson(Action<Utf8JsonWriter> transform)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
            transform(writer);
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private string CreateToken(string configName, string secretKind, string secret)
    {
        var nonce = ToBase64Url(RandomNumberGenerator.GetBytes(16));
        var signature = Sign(configName, secretKind, nonce, secret);
        return $"{MaskPrefix}{nonce}.{ToBase64Url(signature)}";
    }

    private bool TokenMatches(string token, string configName, string secretKind, string secret)
    {
        if (!TryParseToken(token, out var nonce, out var suppliedSignature))
            return false;

        var expectedSignature = Sign(configName, secretKind, nonce, secret);
        return CryptographicOperations.FixedTimeEquals(suppliedSignature, expectedSignature);
    }

    private byte[] Sign(string configName, string secretKind, string nonce, string secret)
    {
        var message = Encoding.UTF8.GetBytes($"{configName}\0{secretKind}\0{nonce}\0{secret}");
        return HMACSHA256.HashData(_signingKey, message);
    }

    private static bool TryParseToken(string token, out string nonce, out byte[] signature)
    {
        nonce = "";
        signature = [];
        if (!IsMaskToken(token))
            return false;

        var parts = token[MaskPrefix.Length..].Split('.');
        if (parts.Length != 2 ||
            !TryFromBase64Url(parts[0], out var nonceBytes) ||
            nonceBytes.Length != 16 ||
            !TryFromBase64Url(parts[1], out signature) ||
            signature.Length != 32)
            return false;

        nonce = parts[0];
        return true;
    }

    private static string ToBase64Url(byte[] value)
    {
        return Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static bool TryFromBase64Url(string value, out byte[] decoded)
    {
        decoded = [];
        var base64 = value.Replace('-', '+').Replace('_', '/');
        base64 += new string('=', (4 - base64.Length % 4) % 4);
        try
        {
            decoded = Convert.FromBase64String(base64);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static BadHttpRequestException InvalidMaskToken(string configName)
    {
        return new BadHttpRequestException($"Invalid or unknown masked secret for `{configName}`.");
    }
}
