using System.Reflection;
using NzbWebDAV.Config;

namespace NzbWebDAV.Tests.Config;

public class ConfigEnvMappingTests
{
    [Theory]
    [InlineData("api.categories", "NZBDAV_CONFIG__API__CATEGORIES")]
    [InlineData("usenet.segment-cache.enabled", "NZBDAV_CONFIG__USENET__SEGMENT_CACHE__ENABLED")]
    [InlineData("api.addurl-trusted-hosts", "NZBDAV_CONFIG__API__ADDURL_TRUSTED_HOSTS")]
    [InlineData("maintenance.remove-orphaned-schedule-time", "NZBDAV_CONFIG__MAINTENANCE__REMOVE_ORPHANED_SCHEDULE_TIME")]
    [InlineData("prowlarr.url", "NZBDAV_CONFIG__PROWLARR__URL")]
    [InlineData("prowlarr.api-key", "NZBDAV_CONFIG__PROWLARR__API_KEY")]
    [InlineData("prowlarr.sync-enabled", "NZBDAV_CONFIG__PROWLARR__SYNC_ENABLED")]
    [InlineData("prowlarr.sync-interval-minutes", "NZBDAV_CONFIG__PROWLARR__SYNC_INTERVAL_MINUTES")]
    public void FormatEnvironmentVariableName_UsesDeterministicRules(string configKey, string expected)
    {
        Assert.Equal(expected, ConfigEnvMapping.FormatEnvironmentVariableName(configKey));
    }

    [Fact]
    public void PublicConfigKeys_CoverAllNonExcludedConfigKeysLiterals()
    {
        var literals = typeof(ConfigKeys)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(string) && field.IsLiteral)
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var excluded in ConfigEnvMapping.ExcludedConfigKeys)
            Assert.Contains(excluded, literals);

        var expected = literals
            .Except(ConfigEnvMapping.ExcludedConfigKeys, StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();
        var actual = ConfigEnvMapping.PublicConfigKeys
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void PublicConfigKeys_MapToUniqueEnvironmentVariableNames()
    {
        var envNames = ConfigEnvMapping.PublicConfigKeys
            .Select(ConfigEnvMapping.ToEnvironmentVariableName)
            .ToList();

        Assert.All(envNames, name => Assert.False(string.IsNullOrWhiteSpace(name)));
        Assert.Equal(envNames.Count, envNames.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void ExcludedKeys_AreNotMapped()
    {
        Assert.Null(ConfigEnvMapping.ToEnvironmentVariableName(ConfigKeys.SearchExcludePrefix));
        Assert.Null(ConfigEnvMapping.ToEnvironmentVariableName(ConfigKeys.SearchExcludeSyncCache));
        Assert.Null(ConfigEnvMapping.ToEnvironmentVariableName(ConfigKeys.ProwlarrSyncStatus));
        Assert.False(ConfigEnvMapping.TryGetConfigKey(
            ConfigEnvMapping.FormatEnvironmentVariableName(ConfigKeys.SearchExcludeSyncCache),
            out _));
        Assert.False(ConfigEnvMapping.TryGetConfigKey(
            ConfigEnvMapping.FormatEnvironmentVariableName(ConfigKeys.ProwlarrSyncStatus),
            out _));
    }

    [Fact]
    public void TryGetConfigKey_IsCaseInsensitiveForEnvNames()
    {
        Assert.True(ConfigEnvMapping.TryGetConfigKey(
            "nzbdav_config__api__categories",
            out var configKey));
        Assert.Equal(ConfigKeys.ApiCategories, configKey);
    }

    [Fact]
    public void RoundTrip_EveryPublicKey()
    {
        foreach (var configKey in ConfigEnvMapping.PublicConfigKeys)
        {
            var envName = ConfigEnvMapping.ToEnvironmentVariableName(configKey);
            Assert.NotNull(envName);
            Assert.True(ConfigEnvMapping.TryGetConfigKey(envName!, out var roundTrip));
            Assert.Equal(configKey, roundTrip);
        }
    }
}
