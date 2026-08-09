using System.Collections.Frozen;
using System.Reflection;

namespace NzbWebDAV.Config;

/// <summary>
/// Deterministic mapping between <see cref="ConfigKeys"/> values and the
/// <c>NZBDAV_CONFIG__...</c> environment-variable namespace used for headless
/// / infrastructure-as-code configuration.
/// </summary>
public static class ConfigEnvMapping
{
    public const string EnvPrefix = "NZBDAV_CONFIG__";

    /// <summary>
    /// Keys that must never be supplied via the ENV overlay: runtime-managed
    /// cache state and non-persisted prefix constants.
    /// </summary>
    public static readonly FrozenSet<string> ExcludedConfigKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        ConfigKeys.SearchExcludePrefix,
        ConfigKeys.SearchExcludeSyncCache,
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly Lazy<IReadOnlyDictionary<string, string>> ConfigKeyToEnvVar =
        new(BuildConfigKeyToEnvVarMap);

    private static readonly Lazy<IReadOnlyDictionary<string, string>> EnvVarToConfigKey =
        new(BuildEnvVarToConfigKeyMap);

    /// <summary>Every user-owned <c>ConfigItems</c> key eligible for ENV overlay.</summary>
    public static IReadOnlyCollection<string> PublicConfigKeys =>
        (IReadOnlyCollection<string>)ConfigKeyToEnvVar.Value.Keys;

    /// <summary>
    /// Maps a config key (<c>api.categories</c>) to its ENV name
    /// (<c>NZBDAV_CONFIG__API__CATEGORIES</c>). Returns null for excluded keys.
    /// </summary>
    public static string? ToEnvironmentVariableName(string configKey)
    {
        return ConfigKeyToEnvVar.Value.TryGetValue(configKey, out var envVar) ? envVar : null;
    }

    /// <summary>
    /// Maps an ENV name back to a config key. Matching is case-insensitive for
    /// the ENV name (process environments are case-insensitive on Windows).
    /// </summary>
    public static bool TryGetConfigKey(string environmentVariableName, out string configKey)
    {
        return EnvVarToConfigKey.Value.TryGetValue(environmentVariableName, out configKey!);
    }

    /// <summary>
    /// Pure transform used by tests and docs: dots → <c>__</c>, hyphens → <c>_</c>,
    /// uppercased, then prefixed with <see cref="EnvPrefix"/>.
    /// </summary>
    public static string FormatEnvironmentVariableName(string configKey)
    {
        var body = configKey
            .Replace('-', '_')
            .Replace(".", "__", StringComparison.Ordinal)
            .ToUpperInvariant();
        return EnvPrefix + body;
    }

    private static Dictionary<string, string> BuildConfigKeyToEnvVarMap()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var field in typeof(ConfigKeys).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.FieldType != typeof(string) || !field.IsLiteral) continue;
            var configKey = (string)field.GetRawConstantValue()!;
            if (ExcludedConfigKeys.Contains(configKey)) continue;
            map[configKey] = FormatEnvironmentVariableName(configKey);
        }

        return map;
    }

    private static Dictionary<string, string> BuildEnvVarToConfigKeyMap()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (configKey, envVar) in ConfigKeyToEnvVar.Value)
        {
            if (!map.TryAdd(envVar, configKey))
            {
                throw new InvalidOperationException(
                    $"ENV mapping collision for `{envVar}` between `{map[envVar]}` and `{configKey}`.");
            }
        }

        return map;
    }
}
