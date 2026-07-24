using System.Collections;
using System.Text.Json;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Utils;
using Serilog;

namespace NzbWebDAV.Config;

/// <summary>
/// Snapshot of authoritative <c>NZBDAV_CONFIG__...</c> values loaded at startup.
/// Values are kept out of SQLite; they overlay persisted <c>ConfigItems</c> at
/// read time and make matching Settings controls read-only.
/// </summary>
public sealed class ConfigEnvironmentOverlay
{
    public static ConfigEnvironmentOverlay Empty { get; } = new(new Dictionary<string, string>(StringComparer.Ordinal));

    private readonly Dictionary<string, string> _values;

    private ConfigEnvironmentOverlay(Dictionary<string, string> values)
    {
        _values = values;
    }

    public int Count => _values.Count;

    public bool IsEmpty => _values.Count == 0;

    public bool TryGetValue(string configKey, out string value) =>
        _values.TryGetValue(configKey, out value!);

    public bool IsManaged(string configKey) => _values.ContainsKey(configKey);

    public string? GetEnvironmentVariableName(string configKey) =>
        IsManaged(configKey) ? ConfigEnvMapping.ToEnvironmentVariableName(configKey) : null;

    public IReadOnlyDictionary<string, string> Values => _values;

    /// <summary>
    /// Loads, validates, and normalizes every <c>NZBDAV_CONFIG__...</c> variable
    /// from the process environment. Unknown / colliding / invalid values throw
    /// <see cref="ConfigEnvironmentException"/> without including secret values.
    /// </summary>
    public static ConfigEnvironmentOverlay LoadFromEnvironment(
        IDictionary? environment = null,
        string? existingUsenetProvidersJson = null)
    {
        environment ??= Environment.GetEnvironmentVariables();
        var supplied = new Dictionary<string, string>(StringComparer.Ordinal);
        var seenEnvVars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (DictionaryEntry entry in environment)
        {
            var envName = entry.Key?.ToString();
            if (envName is null ||
                !envName.StartsWith(ConfigEnvMapping.EnvPrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!ConfigEnvMapping.TryGetConfigKey(envName, out var configKey))
            {
                throw new ConfigEnvironmentException(
                    $"Unknown configuration environment variable `{envName}`.");
            }

            if (seenEnvVars.TryGetValue(envName, out var previousConfigKey) &&
                !string.Equals(previousConfigKey, configKey, StringComparison.Ordinal))
            {
                throw new ConfigEnvironmentException(
                    $"Environment variable `{envName}` maps to multiple config keys.");
            }

            seenEnvVars[envName] = configKey;
            // Last-writer wins for case variants of the same ENV name on
            // case-sensitive platforms; the canonical key is unique.
            supplied[configKey] = entry.Value?.ToString() ?? "";
        }

        if (supplied.Count == 0)
            return Empty;

        // Empty ENV values are treated as unset (same as ValidateConfigItems).
        var items = supplied
            .Where(pair => StringUtil.EmptyToNull(pair.Value) != null)
            .Select(pair => new ConfigItem { ConfigName = pair.Key, ConfigValue = pair.Value })
            .ToList();

        if (items.Count == 0)
            return Empty;

        // Validate one key at a time so failures name the ENV variable without
        // echoing the submitted value (ValidateConfigItems includes values).
        // Structured values reject unknown properties here because a declarative
        // config has nobody watching a Settings page to notice a dropped key.
        foreach (var item in items)
        {
            try
            {
                ConfigManager.ValidateConfigItems([item], rejectUnknownJsonProperties: true);
            }
            catch (ConfigUnmappedPropertyException e)
            {
                var envName = ConfigEnvMapping.ToEnvironmentVariableName(item.ConfigName)
                              ?? item.ConfigName;
                throw new ConfigEnvironmentException(
                    $"Invalid configuration environment variable `{envName}`. " +
                    $"Unknown or miscased property at `{e.JsonPath}`.");
            }
            catch (ArgumentException)
            {
                var envName = ConfigEnvMapping.ToEnvironmentVariableName(item.ConfigName)
                              ?? item.ConfigName;
                throw new ConfigEnvironmentException(
                    $"Invalid configuration environment variable `{envName}`.");
            }
        }

        var normalized = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            var value = item.ConfigValue;
            if (item.ConfigName == ConfigKeys.WebdavPass)
                value = PasswordUtil.Hash(value);
            else if (item.ConfigName == ConfigKeys.UsenetProviders)
                value = NormalizeUsenetProviderIds(value, existingUsenetProvidersJson);

            normalized[item.ConfigName] = value;
        }

        Log.Information(
            "Loaded {Count} authoritative configuration environment variable(s): {Keys}",
            normalized.Count,
            string.Join(", ", normalized.Keys.OrderBy(x => x, StringComparer.Ordinal)));

        return new ConfigEnvironmentOverlay(normalized);
    }

    private static string NormalizeUsenetProviderIds(string incomingJson, string? existingJson)
    {
        var incoming = JsonSerializer.Deserialize<UsenetProviderConfig>(incomingJson)
                       ?? new UsenetProviderConfig();
        UsenetProviderConfig? existing = null;
        if (!string.IsNullOrWhiteSpace(existingJson))
        {
            try
            {
                existing = JsonSerializer.Deserialize<UsenetProviderConfig>(existingJson);
            }
            catch (JsonException)
            {
                existing = null;
            }
        }

        UsenetProviderIdentity.NormalizeProviderIdsOnSave(incoming, existing);
        return JsonSerializer.Serialize(incoming);
    }
}

/// <summary>
/// Fatal startup error for malformed or unknown <c>NZBDAV_CONFIG__...</c> values.
/// Message must never include secret values.
/// </summary>
public sealed class ConfigEnvironmentException(string message) : Exception(message);
