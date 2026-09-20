using Microsoft.AspNetCore.Http;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Config;

public enum CacheMode { Off, Segment, Native }

/// <summary>Single authority for the exclusive cache mode and its legacy segment alias.</summary>
public static class CacheModeResolver
{
    public static CacheMode Parse(string value) => value.Trim().ToLowerInvariant() switch
    {
        "off" => CacheMode.Off,
        "segment" => CacheMode.Segment,
        "native" => CacheMode.Native,
        _ => throw new ArgumentException("cache.mode must be 'off', 'segment', or 'native'."),
    };

    public static CacheMode Resolve(string? mode, string? legacy, bool legacyEnvironmentManaged)
    {
        if (string.IsNullOrWhiteSpace(mode))
            return !string.IsNullOrWhiteSpace(legacy) && bool.Parse(legacy) ? CacheMode.Segment : CacheMode.Off;
        var resolved = Parse(mode);
        if (legacyEnvironmentManaged &&
            (!string.IsNullOrWhiteSpace(legacy) && bool.Parse(legacy)) != (resolved == CacheMode.Segment))
            throw new ArgumentException(
                "cache.mode conflicts with environment-managed usenet.segment-cache.enabled. " +
                "Reconcile NZBDAV_CONFIG__CACHE__MODE and NZBDAV_CONFIG__USENET__SEGMENT_CACHE__ENABLED, then restart.");
        return resolved;
    }

    public static IReadOnlyCollection<ConfigItem> NormalizeUpdate(
        ConfigManager config, IReadOnlyCollection<ConfigItem> items)
    {
        var mode = items.SingleOrDefault(item => item.ConfigName == ConfigKeys.CacheMode);
        var legacy = items.SingleOrDefault(item => item.ConfigName == ConfigKeys.UsenetSegmentCacheEnabled);
        if (mode is null && legacy is null) return items;

        var legacyEnabled = legacy is not null && !string.IsNullOrWhiteSpace(legacy.ConfigValue)
            && bool.Parse(legacy.ConfigValue);
        var desired = mode is not null ? Parse(mode.ConfigValue)
            : legacyEnabled ? CacheMode.Segment : CacheMode.Off;
        if (mode is not null && legacy is not null && legacyEnabled != (desired == CacheMode.Segment))
            throw new BadHttpRequestException("cache.mode contradicts usenet.segment-cache.enabled in this update.");
        if (mode is null && config.IsEnvironmentManaged(ConfigKeys.CacheMode))
            throw new BadHttpRequestException(
                "Cannot update the legacy segment-cache alias while cache.mode is managed by " +
                "NZBDAV_CONFIG__CACHE__MODE. Change the environment and restart instead.");

        var modeValue = desired.ToString().ToLowerInvariant();
        try
        {
            Resolve(modeValue, config.GetEffectiveConfigValue(ConfigKeys.UsenetSegmentCacheEnabled),
                config.IsEnvironmentManaged(ConfigKeys.UsenetSegmentCacheEnabled));
        }
        catch (ArgumentException exception)
        {
            throw new BadHttpRequestException(exception.Message);
        }

        var normalized = items.Where(item => item.ConfigName != ConfigKeys.CacheMode &&
            item.ConfigName != ConfigKeys.UsenetSegmentCacheEnabled).ToList();
        normalized.Add(new ConfigItem { ConfigName = ConfigKeys.CacheMode, ConfigValue = modeValue });
        if (!config.IsEnvironmentManaged(ConfigKeys.UsenetSegmentCacheEnabled))
            normalized.Add(new ConfigItem
            {
                ConfigName = ConfigKeys.UsenetSegmentCacheEnabled,
                ConfigValue = desired == CacheMode.Segment ? "true" : "false",
            });
        return normalized;
    }
}
