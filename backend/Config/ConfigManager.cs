using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Clients.Usenet.Concurrency;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Config;

public class ConfigManager
{
    public static readonly string AppVersion = EnvironmentUtil.GetEnvironmentVariable("NZBDAV_VERSION") ?? "unknown";

    private readonly Dictionary<string, string> _config = new();
    public event EventHandler<ConfigEventArgs>? OnConfigChanged;

    public async Task LoadConfig()
    {
        await using var dbContext = new DavDatabaseContext();
        var configItems = await dbContext.ConfigItems.ToListAsync().ConfigureAwait(false);
        lock (_config)
        {
            _config.Clear();
            foreach (var configItem in configItems)
            {
                _config[configItem.ConfigName] = configItem.ConfigValue;
            }
        }
    }

    private string? GetConfigValue(string configName)
    {
        lock (_config)
        {
            return _config.TryGetValue(configName, out string? value) ? value : null;
        }
    }

    private T? GetConfigValue<T>(string configName)
    {
        var rawValue = StringUtil.EmptyToNull(GetConfigValue(configName));
        return rawValue == null ? default : JsonSerializer.Deserialize<T>(rawValue);
    }

    public void UpdateValues(List<ConfigItem> configItems)
    {
        lock (_config)
        {
            foreach (var configItem in configItems)
            {
                _config[configItem.ConfigName] = configItem.ConfigValue;
            }
        }

        var changedConfig = configItems.ToDictionary(x => x.ConfigName, x => x.ConfigValue);
        OnConfigChanged?.Invoke(this, new ConfigEventArgs { ChangedConfig = changedConfig });
    }

    public string GetRcloneMountDir()
    {
        var mountDir = StringUtil.EmptyToNull(GetConfigValue("rclone.mount-dir"))
                       ?? EnvironmentUtil.GetEnvironmentVariable("MOUNT_DIR")
                       ?? "/mnt/nzbdav";
        if (mountDir.EndsWith('/')) mountDir = mountDir.TrimEnd('/');
        return mountDir;
    }

    public string GetApiKey()
    {
        return StringUtil.EmptyToNull(GetConfigValue("api.key"))
               ?? EnvironmentUtil.GetRequiredVariable("FRONTEND_BACKEND_API_KEY");
    }

    public string GetStrmKey()
    {
        return GetConfigValue("api.strm-key")
               ?? throw new InvalidOperationException("The `api.strm-key` config does not exist.");
    }

    public List<string> GetApiCategories()
    {
        var value = StringUtil.EmptyToNull(GetConfigValue("api.categories"))
                    ?? EnvironmentUtil.GetEnvironmentVariable("CATEGORIES")
                    ?? "audio,software,tv,movies";

        return value.Split(',')
            .Prepend(GetManualUploadCategory())
            .Select(x => x.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();
    }

    public string GetManualUploadCategory()
    {
        return StringUtil.EmptyToNull(GetConfigValue("api.manual-category"))
               ?? "uncategorized";
    }

    public string? GetWebdavUser()
    {
        return StringUtil.EmptyToNull(GetConfigValue("webdav.user"))
               ?? EnvironmentUtil.GetEnvironmentVariable("WEBDAV_USER")
               ?? "admin";
    }

    public string? GetWebdavPasswordHash()
    {
        var hashedPass = StringUtil.EmptyToNull(GetConfigValue("webdav.pass"));
        if (hashedPass != null) return hashedPass;
        var pass = EnvironmentUtil.GetEnvironmentVariable("WEBDAV_PASSWORD");
        if (pass != null) return PasswordUtil.Hash(pass);
        return null;
    }

    public bool IsEnsureImportableVideoEnabled()
    {
        var defaultValue = true;
        var configValue = StringUtil.EmptyToNull(GetConfigValue("api.ensure-importable-video"));
        return (configValue != null ? bool.Parse(configValue) : defaultValue);
    }

    public bool ShowHiddenWebdavFiles()
    {
        var defaultValue = false;
        var configValue = StringUtil.EmptyToNull(GetConfigValue("webdav.show-hidden-files"));
        return (configValue != null ? bool.Parse(configValue) : defaultValue);
    }

    public string? GetLibraryDir()
    {
        return StringUtil.EmptyToNull(GetConfigValue("media.library-dir"));
    }

    public int GetMaxDownloadConnections()
    {
        return int.Parse(
            StringUtil.EmptyToNull(GetConfigValue("usenet.max-download-connections"))
            ?? Math.Min(GetUsenetProviderConfig().TotalPooledConnections, 15).ToString()
        );
    }

    public int GetArticleBufferSize()
    {
        return int.Parse(
            StringUtil.EmptyToNull(GetConfigValue("usenet.article-buffer-size"))
            ?? "40"
        );
    }

    public SemaphorePriorityOdds GetStreamingPriority()
    {
        var stringValue = StringUtil.EmptyToNull(GetConfigValue("usenet.streaming-priority"));
        var numericalValue = int.Parse(stringValue ?? "80");
        return new SemaphorePriorityOdds() { HighPriorityOdds = numericalValue };
    }

    /// <summary>
    /// The maximum combined download rate (Mbit/s) NzbDav is allowed to use across all usenet
    /// providers, for both queue imports and webdav streaming. 0 (the default) means unlimited.
    /// </summary>
    public double GetBandwidthLimitMbps()
    {
        var stringValue = StringUtil.EmptyToNull(GetConfigValue("usenet.bandwidth-limit-mbps"));
        return double.Parse(stringValue ?? "0", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// When the bandwidth limit is contended by both streaming and queue downloads at the same
    /// time, how much of it should be preferentially reserved for streaming. This is not a hard
    /// sub-cap: unused capacity from either side remains available to the other.
    /// </summary>
    public SemaphorePriorityOdds GetBandwidthStreamingReserve()
    {
        var stringValue = StringUtil.EmptyToNull(GetConfigValue("usenet.bandwidth-streaming-reserve"));
        var numericalValue = int.Parse(stringValue ?? "80");
        return new SemaphorePriorityOdds() { HighPriorityOdds = numericalValue };
    }

    public bool IsEnforceReadonlyWebdavEnabled()
    {
        var defaultValue = true;
        var configValue = StringUtil.EmptyToNull(GetConfigValue("webdav.enforce-readonly"));
        return (configValue != null ? bool.Parse(configValue) : defaultValue);
    }

    public HashSet<string> GetEnsureArticleExistenceCategories()
    {
        var configValue = GetConfigValue("api.ensure-article-existence-categories");
        return (configValue ?? "").Split(',')
            .Select(x => x.Trim())
            .Select(x => x.ToLower())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToHashSet();
    }

    public bool IsPreviewPar2FilesEnabled()
    {
        var defaultValue = false;
        var configValue = StringUtil.EmptyToNull(GetConfigValue("webdav.preview-par2-files"));
        return (configValue != null ? bool.Parse(configValue) : defaultValue);
    }

    public bool IsIgnoreSabHistoryLimitEnabled()
    {
        var defaultValue = true;
        var configValue = StringUtil.EmptyToNull(GetConfigValue("api.ignore-history-limit"));
        return (configValue != null ? bool.Parse(configValue) : defaultValue);
    }

    public bool IsRepairJobEnabled()
    {
        var defaultValue = false;
        var configValue = StringUtil.EmptyToNull(GetConfigValue("repair.enable"));
        var isRepairJobEnabled = (configValue != null ? bool.Parse(configValue) : defaultValue);
        return isRepairJobEnabled
               && GetLibraryDir() != null
               && GetArrConfig().GetInstanceCount() > 0;
    }

    public ArrConfig GetArrConfig()
    {
        var defaultValue = new ArrConfig();
        return GetConfigValue<ArrConfig>("arr.instances") ?? defaultValue;
    }

    public UsenetProviderConfig GetUsenetProviderConfig()
    {
        var defaultValue = new UsenetProviderConfig();
        return GetConfigValue<UsenetProviderConfig>("usenet.providers") ?? defaultValue;
    }

    public string GetDuplicateNzbBehavior()
    {
        var defaultValue = "increment";
        return GetConfigValue("api.duplicate-nzb-behavior") ?? defaultValue;
    }

    public HashSet<string> GetBlocklistedFiles()
    {
        var defaultValue = "*.nfo, *.par2, *.sfv, *sample.mkv";
        return (GetConfigValue("api.download-file-blocklist") ?? defaultValue)
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.ToLower())
            .ToHashSet();
    }

    public string GetImportStrategy()
    {
        return GetConfigValue("api.import-strategy") ?? "symlinks";
    }

    public string GetStrmCompletedDownloadDir()
    {
        return GetConfigValue("api.completed-downloads-dir") ?? "/data/completed-downloads";
    }

    public string GetBaseUrl()
    {
        return GetConfigValue("general.base-url") ?? "http://localhost:3000";
    }

    public bool IsRcloneRemoteControlEnabled()
    {
        var defaultValue = false;
        var configValue = StringUtil.EmptyToNull(GetConfigValue("rclone.rc-enabled"));
        return (configValue != null ? bool.Parse(configValue) : defaultValue);
    }

    public string? GetRcloneHost()
    {
        return GetConfigValue("rclone.host");
    }

    public string? GetRcloneUser()
    {
        return GetConfigValue("rclone.user");
    }

    public string? GetRclonePass()
    {
        return GetConfigValue("rclone.pass");
    }

    public string GetUserAgent()
    {
        var defaultValue = $"nzbdav/{AppVersion}";
        return StringUtil.EmptyToNull(GetConfigValue("api.user-agent"))
               ?? EnvironmentUtil.GetEnvironmentVariable("NZB_GRAB_USER_AGENT")
               ?? defaultValue;
    }

    public bool IsDatabaseStartupVacuumEnabled()
    {
        var defaultValue = false;
        var configValue = StringUtil.EmptyToNull(GetConfigValue("db.is-startup-vacuum-enabled"));
        return (configValue != null ? bool.Parse(configValue) : defaultValue);
    }

    public bool IsNzbBackupEnabled()
    {
        var defaultValue = false;
        var configValue = StringUtil.EmptyToNull(GetConfigValue("api.nzb-backup-enabled"));
        return (configValue != null ? bool.Parse(configValue) : defaultValue);
    }

    public string? GetNzbBackupLocation()
    {
        return StringUtil.EmptyToNull(GetConfigValue("api.nzb-backup-location"));
    }

    public bool IsRemoveOrphanedFilesScheduleEnabled()
    {
        var defaultValue = false;
        var configValue = StringUtil.EmptyToNull(GetConfigValue("maintenance.remove-orphaned-schedule-enabled"));
        return (configValue != null ? bool.Parse(configValue) : defaultValue);
    }

    public TimeSpan RemoveOrphanedFilesSchedule()
    {
        var defaultValue = TimeSpan.Zero;
        var configValue = StringUtil.EmptyToNull(GetConfigValue("maintenance.remove-orphaned-schedule-time"));
        if (configValue == null) return defaultValue;
        if (!int.TryParse(configValue, out var totalMinutes)) return defaultValue;
        if (totalMinutes < 0 || totalMinutes >= 24 * 60) return defaultValue;
        return TimeSpan.FromMinutes(totalMinutes);
    }

    public bool IsPrefetchCacheEnabled()
    {
        var defaultValue = false;
        var configValue = StringUtil.EmptyToNull(GetConfigValue("cache.prefetch-enabled"));
        return (configValue != null ? bool.Parse(configValue) : defaultValue);
    }

    /// <summary>
    /// Directory where prefetched episodes are cached on local disk. Defaults to a
    /// subdirectory of CONFIG_PATH so it works out of the box without requiring the
    /// user to mount a separate volume.
    /// </summary>
    public string GetPrefetchCacheDir()
    {
        var configValue = StringUtil.EmptyToNull(GetConfigValue("cache.dir"));
        return configValue ?? Path.Join(DavDatabaseContext.ConfigPath, "cache");
    }

    public double GetPrefetchCacheMinFreeSpaceGb()
    {
        var stringValue = StringUtil.EmptyToNull(GetConfigValue("cache.min-free-space-gb"));
        return double.Parse(stringValue ?? "10", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The watch-progress percentage of the currently-playing episode at which
    /// NzbDav starts prefetching the next episode into the local cache.
    /// </summary>
    public int GetPrefetchCacheThresholdPercent()
    {
        var stringValue = StringUtil.EmptyToNull(GetConfigValue("cache.prefetch-threshold-percent"));
        return int.Parse(stringValue ?? "80");
    }

    public double GetPrefetchCacheMaxTimeHours()
    {
        var stringValue = StringUtil.EmptyToNull(GetConfigValue("cache.max-cache-time-hours"));
        return double.Parse(stringValue ?? "48", System.Globalization.CultureInfo.InvariantCulture);
    }

    public int GetPrefetchCacheMaxEpisodes()
    {
        var stringValue = StringUtil.EmptyToNull(GetConfigValue("cache.max-cache-episodes"));
        return int.Parse(stringValue ?? "5");
    }

    /// <summary>
    /// Shared secret embedded in the Jellyfin webhook URL (?apikey=...), kept separate
    /// from the general `api.key` so rotating it doesn't affect Sonarr/Radarr/SAB-style
    /// integrations. Auto-populated by a migration, same as `api.strm-key`.
    /// </summary>
    public string GetJellyfinWebhookToken()
    {
        return GetConfigValue("jellyfin.webhook-token")
               ?? throw new InvalidOperationException("The `jellyfin.webhook-token` config does not exist.");
    }

    public class ConfigEventArgs : EventArgs
    {
        public required Dictionary<string, string> ChangedConfig { get; init; }
    }
}