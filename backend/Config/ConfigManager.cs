using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Clients.Usenet.Concurrency;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Models;
using NzbWebDAV.Streams;
using NzbWebDAV.Utils;
using Serilog;

namespace NzbWebDAV.Config;

public class ConfigManager : IConfigReader, IConfigUpdater, IConfigChangeSource
{
    public static readonly string AppVersion = EnvironmentUtil.GetEnvironmentVariable("NZBDAV_VERSION") ?? "0.0.0";

    /// <summary>
    /// New config keys that inherit persisted or env values from a legacy name when unset.
    /// Resolution order: env(new) → env(legacy) → DB(new) → DB(legacy).
    /// </summary>
    private static readonly Dictionary<string, string> LegacyConfigKeyAliases =
        new(StringComparer.Ordinal)
        {
            [ConfigKeys.UsenetQueuePipeliningEnabled] = ConfigKeys.UsenetPipeliningEnabled,
            [ConfigKeys.UsenetQueuePipeliningDepth] = ConfigKeys.UsenetPipeliningDepth,
        };

    // Depth a background health check uses when none is configured.
    public const HealthCheckDepth DefaultHealthCheckDepth = HealthCheckDepth.Standard;

    private readonly Dictionary<string, string> _config = new();
    private readonly Dictionary<(string Name, Type Type), object?> _deserializedConfig = new();
    // Compiled exclude patterns are cached and rebuilt only when an exclude-related
    // config key changes (see UpdateValues / LoadConfig), rather than recompiled on
    // every search. Guarded by its own lock so searches and config writes don't
    // contend on _config.
    private readonly object _excludeLock = new();
    private IReadOnlyList<Regex>? _compiledExcludeCache;
    private ConfigEnvironmentOverlay _environmentOverlay = ConfigEnvironmentOverlay.Empty;
    public event EventHandler<ConfigEventArgs>? OnConfigChanged;

    public IDisposable Subscribe(EventHandler<ConfigEventArgs> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        OnConfigChanged += handler;
        return new ConfigChangeSubscription(() => OnConfigChanged -= handler);
    }

    private sealed class ConfigChangeSubscription(Action unsubscribe) : IDisposable
    {
        private Action? _unsubscribe = unsubscribe;

        public void Dispose()
        {
            var action = Interlocked.Exchange(ref _unsubscribe, null);
            action?.Invoke();
        }
    }

    public async Task LoadConfig()
    {
        await using var dbContext = new DavDatabaseContext();
        var configItems = await dbContext.ConfigItems.ToListAsync().ConfigureAwait(false);
        lock (_config)
        {
            _config.Clear();
            _deserializedConfig.Clear();
            foreach (var configItem in configItems)
            {
                _config[configItem.ConfigName] = configItem.ConfigValue;
            }
        }
        lock (_excludeLock) { _compiledExcludeCache = null; }
        SyncPathSanitizer();
    }

    /// <summary>
    /// Applies an authoritative <c>NZBDAV_CONFIG__...</c> overlay. Must run after
    /// <see cref="LoadConfig"/> so provider-ID normalization can see persisted JSON.
    /// ENV values stay out of SQLite and win over persisted rows at read time.
    /// </summary>
    public void ApplyEnvironmentOverlay(ConfigEnvironmentOverlay overlay)
    {
        ArgumentNullException.ThrowIfNull(overlay);
        lock (_config)
        {
            _environmentOverlay = overlay;
            _deserializedConfig.Clear();
        }
        lock (_excludeLock) { _compiledExcludeCache = null; }
        SyncPathSanitizer();
    }

    public bool IsEnvironmentManaged(string configName)
    {
        lock (_config)
        {
            if (_environmentOverlay.IsManaged(configName))
                return true;

            return LegacyConfigKeyAliases.TryGetValue(configName, out var legacyKey)
                   && _environmentOverlay.IsManaged(legacyKey);
        }
    }

    public string? GetEnvironmentVariableName(string configName)
    {
        lock (_config)
        {
            var envName = _environmentOverlay.GetEnvironmentVariableName(configName);
            if (envName is not null)
                return envName;

            return LegacyConfigKeyAliases.TryGetValue(configName, out var legacyKey)
                ? _environmentOverlay.GetEnvironmentVariableName(legacyKey)
                : null;
        }
    }

    /// <summary>
    /// Effective value for a key: ENV overlay wins, then SQLite. Used by the
    /// Settings API so the UI shows what the process is actually running.
    /// </summary>
    public string? GetEffectiveConfigValue(string configName) =>
        LegacyConfigKeyAliases.ContainsKey(configName)
            ? GetAliasedConfigValue(configName)
            : GetConfigValue(configName);

    /// <summary>Persisted SQLite value only (ignores ENV overlay). Used when normalizing provider IDs.</summary>
    public string? GetPersistedConfigValue(string configName)
    {
        lock (_config)
        {
            return _config.TryGetValue(configName, out string? value) ? value : null;
        }
    }

    /// <summary>
    /// Takes a consistent snapshot of the public Settings inputs for diagnostics
    /// exports. Callers must still redact values before exposing the snapshot.
    /// </summary>
    internal IReadOnlyList<ConfigDiagnosticSnapshot> GetDiagnosticSnapshot()
    {
        lock (_config)
        {
            return ConfigEnvMapping.PublicConfigKeys
                .OrderBy(key => key, StringComparer.Ordinal)
                .Select(key =>
                {
                    if (_environmentOverlay.TryGetValue(key, out var environmentValue))
                    {
                        return new ConfigDiagnosticSnapshot(
                            key,
                            environmentValue,
                            "NZBDAV_CONFIG",
                            _environmentOverlay.GetEnvironmentVariableName(key));
                    }

                    return _config.TryGetValue(key, out var persistedValue)
                        ? new ConfigDiagnosticSnapshot(key, persistedValue, "SQLite", null)
                        : new ConfigDiagnosticSnapshot(key, null, "default-or-unset", null);
                })
                .ToList();
        }
    }

    private string? GetConfigValue(string configName)
    {
        lock (_config)
        {
            if (_environmentOverlay.TryGetValue(configName, out var envValue))
                return envValue;
            return _config.TryGetValue(configName, out string? value) ? value : null;
        }
    }

    /// <summary>
    /// Resolves a key with a legacy alias: env(new) → env(legacy) → DB(new) → DB(legacy).
    /// Empty persisted values count as unset (matching every other config reader), so
    /// a cleared new-key row still falls back to the legacy one.
    /// </summary>
    private string? GetAliasedConfigValue(string configName)
    {
        if (!LegacyConfigKeyAliases.TryGetValue(configName, out var legacyConfigName))
            return GetConfigValue(configName);

        lock (_config)
        {
            if (_environmentOverlay.TryGetValue(configName, out var envNew))
                return envNew;
            if (_environmentOverlay.TryGetValue(legacyConfigName, out var envLegacy))
                return envLegacy;
            if (_config.TryGetValue(configName, out var dbNewRaw)
                && StringUtil.EmptyToNull(dbNewRaw) is { } dbNew)
                return dbNew;
            if (_config.TryGetValue(legacyConfigName, out var dbLegacyRaw)
                && StringUtil.EmptyToNull(dbLegacyRaw) is { } dbLegacy)
                return dbLegacy;
            return null;
        }
    }

    private T? GetConfigValue<T>(string configName)
    {
        string? rawValue;
        var cacheKey = (configName, typeof(T));

        lock (_config)
        {
            string? storedValue = null;
            if (_environmentOverlay.TryGetValue(configName, out var envValue))
                storedValue = envValue;
            else if (_config.TryGetValue(configName, out var dbValue))
                storedValue = dbValue;

            if (storedValue is null) return default;
            rawValue = StringUtil.EmptyToNull(storedValue);
            if (rawValue == null) return default;

            if (_deserializedConfig.TryGetValue(cacheKey, out var cachedValue))
                return cachedValue is null ? default : (T)cachedValue;
        }

        // Deserialize outside the lock so large JSON payloads (providers, indexers)
        // do not block other config readers on the hot path.
        var value = JsonSerializer.Deserialize<T>(rawValue);

        lock (_config)
        {
            if (_deserializedConfig.TryGetValue(cacheKey, out var cachedValue))
                return cachedValue is null ? default : (T)cachedValue;

            string? currentStored = null;
            if (_environmentOverlay.TryGetValue(configName, out var envValue))
                currentStored = envValue;
            else if (_config.TryGetValue(configName, out var dbValue))
                currentStored = dbValue;

            if (StringUtil.EmptyToNull(currentStored) == rawValue)
            {
                _deserializedConfig[cacheKey] = value;
            }

            return value;
        }
    }

    public bool IsWardenHideDeadEnabled()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.WardenHideDead));
        return v is null || v == "true";
    }

    public int GetWardenQuorum()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.WardenQuorum));
        return v is not null && int.TryParse(v, out var n) && n >= 1 ? n : 2;
    }

    public int GetWardenMaxSourceEntries()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.WardenMaxSourceEntries));
        return v is not null && int.TryParse(v, out var n) && n > 0 ? n : 2_000_000;
    }

    public bool IsWardenBackboneScopeEnabled()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.WardenBackboneScope));
        return v is null || v == "true";
    }

    public void UpdateValues(List<ConfigItem> configItems)
    {
        Dictionary<string, string> changedConfig;
        lock (_config)
        {
            foreach (var configItem in configItems)
            {
                // Always update the SQLite-backed map so removing an ENV overlay
                // on the next restart restores the last persisted value. ENV-managed
                // keys still resolve from the overlay at read time.
                _config[configItem.ConfigName] = configItem.ConfigValue;
                foreach (var cacheKey in _deserializedConfig.Keys
                             .Where(key => key.Name == configItem.ConfigName)
                             .ToArray())
                {
                    _deserializedConfig.Remove(cacheKey);
                }
            }

            // Only emit OnConfigChanged for keys whose *effective* value changed.
            // Writes to ENV-managed keys leave the running value unchanged.
            changedConfig = configItems
                .Where(item => !_environmentOverlay.IsManaged(item.ConfigName))
                .ToDictionary(x => x.ConfigName, x => x.ConfigValue);
        }

        if (configItems.Any(x => x.ConfigName.StartsWith(ConfigKeys.SearchExcludePrefix, StringComparison.Ordinal)
                                 || x.ConfigName == ConfigKeys.PlayExcludePatterns))
        {
            lock (_excludeLock) { _compiledExcludeCache = null; }
        }

        if (changedConfig.Count == 0) return;

        if (changedConfig.ContainsKey(ConfigKeys.WebdavWindowsSafePaths))
            SyncPathSanitizer();
        OnConfigChanged?.Invoke(this, new ConfigEventArgs { ChangedConfig = changedConfig });
    }

    private void SyncPathSanitizer() =>
        PathSanitizer.SetWindowsSafePathsEnabled(IsWindowsSafePathsEnabled());

    // Get-only computed properties are serialized by default and count as mapped,
    // so JsonSerializer.Serialize output for these types round-trips unchanged.
    private static readonly JsonSerializerOptions RejectUnknownPropertiesJsonOptions = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private static readonly JsonSerializerOptions CaseInsensitiveJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Validates incoming config values, failing fast for anything that would otherwise throw
    /// deep inside a request/background task at read time (non-numeric ints, non-boolean flags,
    /// malformed JSON). Empty values are treated as "unset" and always allowed, matching the
    /// getters' fallback-to-default behavior. With <paramref name="rejectUnknownJsonProperties"/>
    /// set, structured JSON values also reject unknown or miscased properties instead of silently
    /// dropping them.
    /// </summary>
    public static void ValidateConfigItems(IEnumerable<ConfigItem> configItems, bool rejectUnknownJsonProperties = false)
    {
        var jsonOptions = rejectUnknownJsonProperties ? RejectUnknownPropertiesJsonOptions : null;
        foreach (var item in configItems)
        {
            var value = StringUtil.EmptyToNull(item.ConfigValue);
            if (value == null) continue;

            switch (item.ConfigName)
            {
                case ConfigKeys.QueueMaxItems:
                case ConfigKeys.QueueResumeThreshold:
                case ConfigKeys.MetricsFetchRetentionHours:
                    RequireNonNegativeInt(item.ConfigName, value);
                    break;

                case ConfigKeys.UsenetMaxDownloadConnections:
                case ConfigKeys.UsenetMaxQueueConnections:
                case ConfigKeys.QueueWorkerCount:
                case ConfigKeys.UsenetQueuePipeliningDepth:
                case ConfigKeys.UsenetPipeliningDepth:
                case ConfigKeys.UsenetArticleBufferSize:
                case ConfigKeys.UsenetInFlightArticleBudgetMb:
                case ConfigKeys.UsenetIdleConnectionTimeoutSeconds:
                case ConfigKeys.UsenetWarmConnectionsFloor:
                case ConfigKeys.UsenetArticleMissCacheTtlSeconds:
                case ConfigKeys.UsenetArticleMissCacheMaxEntries:
                case ConfigKeys.UsenetSegmentCacheMaxGb:
                case ConfigKeys.UsenetStreamingPriority:
                case ConfigKeys.UsenetStreamingSegmentTimeoutSeconds:
                case ConfigKeys.UsenetStreamingSegmentRetries:
                case ConfigKeys.UsenetStreamingReadTimeoutSeconds:
                case ConfigKeys.UsenetStreamingWriteTimeoutSeconds:
                case ConfigKeys.WardenQuorum:
                case ConfigKeys.WardenMaxSourceEntries:
                case ConfigKeys.PlayTotalBudgetSeconds:
                case ConfigKeys.PlayHedgeDelaySeconds:
                case ConfigKeys.PlayMaxCandidates:
                case ConfigKeys.PlayMaxAttempts:
                case ConfigKeys.PlayVerifySampleCount:
                case ConfigKeys.PlayCandidateNegativeCacheMinutes:
                case ConfigKeys.PlayResolutionCacheTtlHours:
                case ConfigKeys.GrabStallFailoverWindowSeconds:
                case ConfigKeys.GrabStallFailoverCeilingSeconds:
                case ConfigKeys.SearchExcludeSyncRefreshMinutes:
                case ConfigKeys.VariantsTolerancePct:
                case ConfigKeys.VariantsMaxPerGroup:
                case ConfigKeys.VariantsEvictionActiveGraceSeconds:
                case ConfigKeys.VariantsSegmentDonorsMaxSiblings:
                case ConfigKeys.VariantsSegmentDonorsMaxPerSegment:
                case ConfigKeys.PreflightMaxAttempts:
                case ConfigKeys.PreflightTtlSeconds:
                case ConfigKeys.PreflightIndexerMaxWaitSeconds:
                case ConfigKeys.WatchtowerSizeFloorBytes:
                case ConfigKeys.WatchtowerSizeCeilingBytes:
                case ConfigKeys.WatchtowerMinGrabs:
                case ConfigKeys.WatchtowerShortlistDepth:
                case ConfigKeys.WatchtowerGrabCapPerResolve:
                case ConfigKeys.WatchtowerVerifySampleCount:
                case ConfigKeys.WatchtowerVerifyTimeoutSeconds:
                case ConfigKeys.WatchtowerActiveSetCap:
                case ConfigKeys.WatchtowerResolveConcurrency:
                case ConfigKeys.WatchtowerDailyResolveBudget:
                case ConfigKeys.WatchtowerSyncIntervalSeconds:
                case ConfigKeys.WatchtowerKeepfreshBaseSeconds:
                case ConfigKeys.WatchtowerKeepfreshMaxSeconds:
                case ConfigKeys.WatchtowerUnavailableRetrySeconds:
                case ConfigKeys.WatchtowerSeriesMaxEpisodes:
                case ConfigKeys.WatchtowerSeriesRecentCount:
                case ConfigKeys.WatchtowerSeasonBundleFallbackRecentCount:
                case ConfigKeys.WatchtowerSeasonBundleFallbackMaxEpisodes:
                case ConfigKeys.RepairHealthcheckConcurrency:
                case ConfigKeys.RepairAutoRemoveAfterFailures:
                case ConfigKeys.DatabaseHistoryRetentionDays:
                case ConfigKeys.DatabaseHealthcheckRetentionDays:
                case ConfigKeys.MaintenanceRemoveOrphanedScheduleTime:
                case ConfigKeys.BackupScheduleTime:
                case ConfigKeys.BackupRetentionCount:
                case ConfigKeys.ApiNzbBackupRetentionDays:
                case ConfigKeys.QueueSpeedLimitKbps:
                    RequireLong(item.ConfigName, value);
                    break;

                case ConfigKeys.ProwlarrSyncIntervalMinutes:
                    RequireLongInRange(item.ConfigName, value, 5, 10080);
                    break;

                case ConfigKeys.UsenetStreamingBodyBatchWidth:
                    RequireLongInRange(item.ConfigName, value, 1, 8);
                    break;

                case ConfigKeys.UsenetSharedStreamsMaxEntries:
                    RequireLongInRange(item.ConfigName, value, 1, 32);
                    break;

                case ConfigKeys.UsenetSharedStreamsMaxEntriesPerFile:
                    RequireLongInRange(item.ConfigName, value, 1, 8);
                    break;

                case ConfigKeys.UsenetSharedStreamsRingMb:
                    RequireLongInRange(item.ConfigName, value, 4, 256);
                    break;

                case ConfigKeys.UsenetSharedStreamsGraceSeconds:
                    RequireLongInRange(item.ConfigName, value, 0, 60);
                    break;

                case ConfigKeys.UsenetSharedStreamsSmallRangeMaxMb:
                    RequireLongInRange(item.ConfigName, value, 1, 256);
                    break;

                case ConfigKeys.ProwlarrUrl:
                    RequireHttpUrl(item.ConfigName, value);
                    break;

                case ConfigKeys.ApiEnsureImportableVideo:
                case ConfigKeys.ApiSampleFilterEnabled:
                case ConfigKeys.ApiIgnoreHistoryLimit:
                case ConfigKeys.ApiLazyRarParsing:
                case ConfigKeys.ApiNzbBackupEnabled:
                case ConfigKeys.ApiSkipNonVideoOnMissingArticles:
                case ConfigKeys.ApiStrmOutputEnabled:
                case ConfigKeys.ApiSymlinkOutputEnabled:
                case ConfigKeys.WebdavShowHiddenFiles:
                case ConfigKeys.WebdavEnforceReadonly:
                case ConfigKeys.WebdavPreviewPar2Files:
                case ConfigKeys.WebdavWindowsSafePaths:
                case ConfigKeys.UsenetMaxDownloadConnectionsPerStream:
                case ConfigKeys.UsenetQueuePipeliningEnabled:
                case ConfigKeys.UsenetPipeliningEnabled:
                case ConfigKeys.UsenetCascadeEnabled:
                case ConfigKeys.UsenetCascadeRetryPrimaryOnMiss:
                case ConfigKeys.UsenetWarmConnectionsEnabled:
                case ConfigKeys.UsenetContainerAwareFill:
                case ConfigKeys.UsenetPipelinedBodyRequests:
                case ConfigKeys.UsenetSegmentCacheEnabled:
                case ConfigKeys.UsenetSharedStreamsEnabled:
                case ConfigKeys.PlayWatchdogEnabled:
                case ConfigKeys.PlayPreferSubtitles:
                case ConfigKeys.GrabStallFailoverEnabled:
                case ConfigKeys.VariantsFallbackOnFailure:
                case ConfigKeys.VariantsSegmentDonorsEnabled:
                case ConfigKeys.WatchtowerEnabled:
                case ConfigKeys.WatchtowerAutoThroughput:
                case ConfigKeys.WatchtowerVerboseLogging:
                case ConfigKeys.WatchtowerSeasonBundles:
                case ConfigKeys.WatchtowerSeasonBundleFallback:
                case ConfigKeys.WardenHideDead:
                case ConfigKeys.WardenBackboneScope:
                case ConfigKeys.RepairEnable:
                case ConfigKeys.RepairPar2Enabled:
                case ConfigKeys.RepairPar2PreferredOverArr:
                case ConfigKeys.RepairHealthcheckAging:
                case ConfigKeys.RepairAutoRemoveUnlinkedOnly:
                case ConfigKeys.RcloneRcEnabled:
                case ConfigKeys.DbIsStartupVacuumEnabled:
                case ConfigKeys.MaintenanceRemoveOrphanedScheduleEnabled:
                case ConfigKeys.BackupScheduleEnabled:
                case ConfigKeys.QueuePaused:
                case ConfigKeys.ProwlarrSyncEnabled:
                case ConfigKeys.ArrHealthEnabled:
                    RequireBool(item.ConfigName, value);
                    break;

                case ConfigKeys.RepairHealthcheckDepth:
                    RequireOneOf(item.ConfigName, value, "standard", "enhanced", "deep", "complete");
                    break;

                case ConfigKeys.ApiArticleExistenceCheckMode:
                    RequireOneOf(item.ConfigName, value, "full", "sampled");
                    break;

                case ConfigKeys.ApiImportStrategy:
                    RequireOneOf(item.ConfigName, value, "symlinks", "strm");
                    break;

                case ConfigKeys.UsenetMaxQueueConnectionsPreset:
                    RequireOneOf(item.ConfigName, value, "low", "medium", "high", "max");
                    break;

                case ConfigKeys.UsenetProviders:
                    RequireValidUsenetProviders(item.ConfigName, value, jsonOptions);
                    break;

                case ConfigKeys.ArrInstances:
                    RequireJson<ArrConfig>(item.ConfigName, value, jsonOptions);
                    break;

                case ConfigKeys.IndexersInstances:
                    RequireJson<IndexerConfig>(item.ConfigName, value, jsonOptions);
                    break;

                case ConfigKeys.ProfilesInstances:
                    RequireJson<ProfileConfig>(item.ConfigName, value, jsonOptions);
                    break;

                case ConfigKeys.ProwlarrSyncStatus:
                    RequireJson<ProwlarrSyncStatus>(item.ConfigName, value, jsonOptions);
                    break;
            }
        }

        return;

        void RequireLong(string key, string value)
        {
            if (!long.TryParse(value, out _))
                throw new ArgumentException($"Config value for '{key}' must be a whole number, but was '{value}'.");
        }

        void RequireNonNegativeInt(string key, string value)
        {
            if (!int.TryParse(value, out var parsed) || parsed < 0)
                throw new ArgumentException(
                    $"Config value for '{key}' must be a non-negative whole number, but was '{value}'.");
        }

        void RequireLongInRange(string key, string value, long minimum, long maximum)
        {
            if (!long.TryParse(value, out var parsed) || parsed < minimum || parsed > maximum)
                throw new ArgumentException(
                    $"Config value for '{key}' must be a whole number from {minimum} through {maximum}.");
        }

        void RequireHttpUrl(string key, string value)
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                || !string.IsNullOrEmpty(uri.Query)
                || !string.IsNullOrEmpty(uri.Fragment)
                || !string.IsNullOrEmpty(uri.UserInfo))
                throw new ArgumentException(
                    $"Config value for '{key}' must be an absolute http(s) URL without credentials, query, or fragment.");
        }

        void RequireBool(string key, string value)
        {
            if (!bool.TryParse(value, out _))
                throw new ArgumentException($"Config value for '{key}' must be 'true' or 'false', but was '{value}'.");
        }

        void RequireOneOf(string key, string value, params string[] allowed)
        {
            if (!allowed.Contains(value, StringComparer.OrdinalIgnoreCase))
                throw new ArgumentException(
                    $"Config value for '{key}' must be one of '{string.Join("', '", allowed)}', but was '{value}'.");
        }

        void RequireJson<T>(string key, string value, JsonSerializerOptions? options)
        {
            try
            {
                JsonSerializer.Deserialize<T>(value, options);
            }
            catch (JsonException e)
            {
                throw DescribeJsonFailure<T>(key, value, options, e);
            }
        }

        // Parsing case-insensitively proves the payload is well formed and satisfies
        // the type, so a strict failure can only be property naming and the path is
        // safe to surface. One that fails both is malformed, and that message can
        // quote part of the value.
        ArgumentException DescribeJsonFailure<T>(
            string key, string value, JsonSerializerOptions? options, JsonException e)
        {
            if (options is null || !ParsesIgnoringCase<T>(value))
                return new ArgumentException($"Config value for '{key}' is not valid JSON: {e.Message}");

            return new ConfigUnmappedPropertyException(key, TruncatePath(e.Path));
        }

        bool ParsesIgnoringCase<T>(string value)
        {
            try
            {
                JsonSerializer.Deserialize<T>(value, CaseInsensitiveJsonOptions);
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        // A property name is operator-supplied text, so bound what reaches the log.
        string TruncatePath(string? path)
        {
            if (string.IsNullOrEmpty(path)) return "$";
            return path.Length <= 120 ? path : path[..120] + "...";
        }

        void RequireValidUsenetProviders(string key, string value, JsonSerializerOptions? options)
        {
            UsenetProviderConfig? config;
            try
            {
                config = JsonSerializer.Deserialize<UsenetProviderConfig>(value, options);
            }
            catch (JsonException e)
            {
                throw DescribeJsonFailure<UsenetProviderConfig>(key, value, options, e);
            }

            if (config?.Providers is null) return;
            for (var i = 0; i < config.Providers.Count; i++)
            {
                var p = config.Providers[i];
                var label = string.IsNullOrWhiteSpace(p.Nickname) ? p.Host : p.Nickname;
                if (string.IsNullOrWhiteSpace(p.Host))
                    throw new ArgumentException($"Provider #{i + 1}: host must not be empty.");
                if (ContainsControlChars(p.Host) || p.Host.Contains(' ', StringComparison.Ordinal))
                    throw new ArgumentException($"Provider '{label}': host contains whitespace or control characters.");
                if (ContainsControlChars(p.User))
                    throw new ArgumentException($"Provider '{label}': username contains control characters.");
                if (ContainsControlChars(p.Pass))
                    throw new ArgumentException($"Provider '{label}': password contains control characters.");
                if ((p.User?.Length ?? 0) > 400 || (p.Pass?.Length ?? 0) > 400)
                    throw new ArgumentException($"Provider '{label}': username/password exceeds 400 characters.");
                if (p.Port is < 1 or > 65535)
                    throw new ArgumentException($"Provider '{label}': port must be between 1 and 65535, but was {p.Port}.");
                if (p.MaxConnections < 1)
                    throw new ArgumentException($"Provider '{label}': max connections must be at least 1, but was {p.MaxConnections}.");
                if (p.ByteLimit is < 0)
                    throw new ArgumentException($"Provider '{label}': byte limit must not be negative.");
            }

            bool ContainsControlChars(string? s) =>
                !string.IsNullOrEmpty(s) && s.Any(c => c < 0x20 || c == 0x7F);
        }
    }

    public string GetRcloneMountDir()
    {
        var mountDir = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.RcloneMountDir))
                       ?? EnvironmentUtil.GetEnvironmentVariable("MOUNT_DIR")
                       ?? "/mnt/nzbdav";
        if (mountDir.EndsWith('/')) mountDir = mountDir.TrimEnd('/');
        return mountDir;
    }

    public string GetApiKey()
    {
        return StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.ApiKey))
               ?? EnvironmentUtil.GetRequiredVariable("FRONTEND_BACKEND_API_KEY");
    }

    public string GetStrmKey()
    {
        return GetConfigValue(ConfigKeys.ApiStrmKey)
               ?? throw new InvalidOperationException($"The `{ConfigKeys.ApiStrmKey}` config does not exist.");
    }

    public List<string> GetApiCategories()
    {
        var value = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.ApiCategories))
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
        return StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.ApiManualCategory))
               ?? "uncategorized";
    }

    public string? GetWebdavUser()
    {
        return StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.WebdavUser))
               ?? EnvironmentUtil.GetEnvironmentVariable("WEBDAV_USER")
               ?? "admin";
    }

    public string? GetWebdavPasswordHash()
    {
        var hashedPass = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.WebdavPass));
        if (hashedPass != null) return hashedPass;
        var pass = EnvironmentUtil.GetEnvironmentVariable("WEBDAV_PASSWORD");
        if (pass != null) return PasswordUtil.Hash(pass);
        return null;
    }

    /// <summary>
    /// When true (default), NZBs must contain at least one video or audio file
    /// to be accepted; otherwise the download fails so an alternate can be grabbed.
    /// </summary>
    public bool IsEnsureImportableMediaEnabled()
    {
        var defaultValue = true;
        var configValue = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.ApiEnsureImportableVideo));
        return (configValue != null ? bool.Parse(configValue) : defaultValue);
    }

    /// <summary>
    /// When true (default), non-media files with missing articles are skipped
    /// instead of failing the job. Media files (video and audio) always fail.
    /// </summary>
    public bool IsSkipNonMediaOnMissingArticlesEnabled()
    {
        var defaultValue = true;
        var configValue = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.ApiSkipNonVideoOnMissingArticles));
        return configValue != null ? bool.Parse(configValue) : defaultValue;
    }

    public bool ShowHiddenWebdavFiles()
    {
        var defaultValue = false;
        var configValue = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.WebdavShowHiddenFiles));
        return (configValue != null ? bool.Parse(configValue) : defaultValue);
    }

    public string? GetLibraryDir()
    {
        return StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.MediaLibraryDir));
    }

    // The total connection budget used for webdav streaming. "0" or empty means
    // "auto": use the combined connection limit of the primary Pool providers
    // (backups excluded), recomputed as providers change. Any positive value is
    // a manual override.
    public int GetMaxDownloadConnections()
    {
        var configured = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.UsenetMaxDownloadConnections));
        if (configured is null || !int.TryParse(configured, out var value) || value <= 0)
            return GetUsenetProviderConfig().TotalPooledConnections;
        return value;
    }

    // When true, the max-download-connections budget is applied per playback
    // stream (each concurrent stream gets its own budget) rather than as a
    // single pool shared across all streams. The provider's connection limit
    // still caps the actual number of open connections.
    public bool IsMaxDownloadConnectionsPerStream()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.UsenetMaxDownloadConnectionsPerStream));
        return v != null && bool.Parse(v);
    }

    // The per-stream streaming budget used when "per stream" mode is on. Each
    // stream is allowed a performance-preset fraction (Low/Medium/High/Max) of
    // the overall download-connection budget, at least 1.
    public int GetMaxDownloadConnectionsPerStreamCount()
    {
        var fraction = GetMaxDownloadConnectionsPerStreamFraction();
        return Math.Max(1, (int)Math.Round(GetMaxDownloadConnections() * fraction));
    }

    private double GetMaxDownloadConnectionsPerStreamFraction()
    {
        var preset = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.UsenetMaxDownloadConnectionsPerStreamPreset));
        return preset?.ToLowerInvariant() switch
        {
            "low" => 0.25,
            "medium" => 0.5,
            "high" => 0.75,
            "max" => 1.0,
            _ => 0.75,
        };
    }

    public int GetMaxQueueConnections()
    {
        var pool = Math.Max(1, GetUsenetProviderConfig().TotalPooledConnections);
        var configured = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.UsenetMaxQueueConnections));
        if (configured is not null && int.TryParse(configured, out var value))
            return Math.Clamp(value, 1, pool);

        var fraction = GetMaxQueueConnectionsFraction();
        return fraction is null ? pool : Math.Max(1, (int)Math.Round(pool * fraction.Value));
    }

    // Mirrors the per-stream preset: a fraction of the pooled-connection budget
    // rather than an absolute count, so one setting means the same thing whatever
    // a user's providers add up to. Null when unset, which keeps the historical
    // default of "the queue may use the whole pool". Unknown values are rejected
    // by ValidateConfigItems, so the fall-through only covers hand-edited rows.
    private double? GetMaxQueueConnectionsFraction()
    {
        var preset = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.UsenetMaxQueueConnectionsPreset));
        return preset?.ToLowerInvariant() switch
        {
            "low" => 0.25,
            "medium" => 0.5,
            "high" => 0.75,
            "max" => 1.0,
            _ => null,
        };
    }

    /// <summary>
    /// How many NZB queue items may process concurrently. Default 1 preserves
    /// historical single-item behavior. Clamped to 1–10.
    /// Workers share <see cref="GetMaxQueueConnections"/>.
    /// </summary>
    public int GetQueueWorkerCount()
    {
        var configured = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.QueueWorkerCount));
        if (configured is null || !int.TryParse(configured, out var value))
            return 1;
        return Math.Clamp(value, 1, 10);
    }

    public int GetQueueMaxItems()
    {
        var configured = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.QueueMaxItems));
        return configured is not null && int.TryParse(configured, out var value)
            ? Math.Max(0, value)
            : 0;
    }

    public int GetQueueResumeThreshold()
    {
        var maxItems = GetQueueMaxItems();
        if (maxItems == 0) return 0;

        var configured = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.QueueResumeThreshold));
        if (configured is null || !int.TryParse(configured, out var value) || value <= 0)
            return maxItems;
        return Math.Min(value, maxItems);
    }

    public void ValidateQueueAdmissionSettings(IEnumerable<ConfigItem> updates)
    {
        var patch = updates.ToDictionary(x => x.ConfigName, x => x.ConfigValue);
        var maxItems = ParsePatchedValue(ConfigKeys.QueueMaxItems, GetQueueMaxItems());
        var resumeThreshold = ParsePatchedValue(
            ConfigKeys.QueueResumeThreshold,
            GetQueueResumeThreshold());

        if (maxItems > 0 && resumeThreshold > maxItems)
            throw new ArgumentException(
                $"Config value for '{ConfigKeys.QueueResumeThreshold}' must be less than or equal to " +
                $"'{ConfigKeys.QueueMaxItems}'.");

        return;

        int ParsePatchedValue(string key, int fallback)
        {
            if (!patch.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw))
                return fallback;
            return int.TryParse(raw, out var parsed) ? parsed : fallback;
        }
    }

    /// <summary>
    /// SAB-compatible queue pause state (<c>mode=pause</c> / <c>mode=resume</c>).
    /// Blocks the queue coordinator from starting new downloads; items already
    /// in progress finish naturally and WebDAV keeps serving mounted content.
    /// </summary>
    public bool IsSabQueuePaused()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.QueuePaused));
        return v != null && bool.Parse(v);
    }

    /// <summary>
    /// SAB-compatible speed limit in KB/s set via <c>mode=speedlimit</c>. 0 means
    /// unlimited. Accepted and stored for Arr/API compatibility; actual byte/s
    /// throttling is tracked separately (see #375).
    /// </summary>
    public int GetSabSpeedLimitKbps()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.QueueSpeedLimitKbps));
        if (v == null) return 0;
        return int.TryParse(v, out var n) ? Math.Max(0, n) : 0;
    }

    public bool IsQueuePipeliningEnabled()
    {
        var v = StringUtil.EmptyToNull(GetAliasedConfigValue(ConfigKeys.UsenetQueuePipeliningEnabled));
        return v is not null && bool.TryParse(v, out var enabled) && enabled;
    }

    public bool IsCascadeEnabled()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.UsenetCascadeEnabled));
        return v != null && bool.Parse(v);
    }

    /// <summary>
    /// When true (default), a definitive batch miss (430/451) re-probes the primary once
    /// before cascading to backups — multi-node spool routing can miss on one connection.
    /// Disable to skip straight to backups after the initial miss.
    /// </summary>
    public bool IsCascadeRetryPrimaryOnMiss()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.UsenetCascadeRetryPrimaryOnMiss));
        return v == null || bool.Parse(v);
    }

    public int GetQueuePipeliningDepth()
    {
        var configured = StringUtil.EmptyToNull(GetAliasedConfigValue(ConfigKeys.UsenetQueuePipeliningDepth));
        if (configured is null || !int.TryParse(configured, out var value)) return 8;
        return Math.Clamp(value, 1, 64);
    }

    /// <summary>
    /// Maximum pipelined BODY batch width per streaming connection. Default 4;
    /// clamped to [1, 8]. The adaptive sizer narrows below this on starvation.
    /// </summary>
    public int GetStreamingBodyBatchWidth()
    {
        var configured = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.UsenetStreamingBodyBatchWidth));
        if (configured is null || !int.TryParse(configured, out var value)) return 4;
        return Math.Clamp(value, 1, 8);
    }

    public bool IsSharedStreamsEnabled()
    {
        var value = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.UsenetSharedStreamsEnabled));
        return value == null || bool.Parse(value);
    }

    public int GetSharedStreamsMaxEntries() =>
        GetClampedInt(ConfigKeys.UsenetSharedStreamsMaxEntries, 4, 1, 32);

    public int GetSharedStreamsMaxEntriesPerFile() =>
        GetClampedInt(ConfigKeys.UsenetSharedStreamsMaxEntriesPerFile, 3, 1, 8);

    public int GetSharedStreamsRingMb() =>
        GetClampedInt(ConfigKeys.UsenetSharedStreamsRingMb, 32, 4, 256);

    public long GetSharedStreamsRingBytes() =>
        (long)GetSharedStreamsRingMb() * 1024L * 1024L;

    public int GetSharedStreamsGraceSeconds() =>
        GetClampedInt(ConfigKeys.UsenetSharedStreamsGraceSeconds, 10, 0, 60);

    public int GetSharedStreamsSmallRangeMaxMb() =>
        GetClampedInt(ConfigKeys.UsenetSharedStreamsSmallRangeMaxMb, 16, 1, 256);

    public long GetSharedStreamsSmallRangeMaxBytes() =>
        (long)GetSharedStreamsSmallRangeMaxMb() * 1024L * 1024L;

    private int GetClampedInt(string key, int defaultValue, int min, int max)
    {
        var configured = StringUtil.EmptyToNull(GetConfigValue(key));
        if (configured is null || !int.TryParse(configured, out var value))
            return defaultValue;
        return Math.Clamp(value, min, max);
    }

    public int GetArticleBufferSize()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.UsenetArticleBufferSize));
        return int.TryParse(v, out var n) ? Math.Clamp(n, 0, 1000) : 40;
    }

    /// <summary>
    /// Host-wide cap on decoded article bytes retained in RAM across concurrent WebDAV
    /// streams. When unset, derived from the process heap limit (25%, clamped [64, 8192]).
    /// Explicit values keep the existing [64, 8192] clamp. Distinct from
    /// <see cref="GetArticleBufferSize"/>, which bounds per-stream segment count.
    /// </summary>
    public int GetInFlightArticleBudgetMb()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.UsenetInFlightArticleBudgetMb));
        return int.TryParse(v, out var n)
            ? Math.Clamp(n, 64, 8192)
            : MemoryBudget.DefaultInFlightArticleBudgetMb();
    }

    public long GetInFlightArticleBudgetBytes() =>
        (long)GetInFlightArticleBudgetMb() * 1024L * 1024L;

    /// <summary>
    /// Idle timeout for pooled NNTP connections. Default 60s; clamped to [15, 300].
    /// Takes effect on the next connection-pool rebuild (provider config change or restart).
    /// </summary>
    public int GetIdleConnectionTimeoutSeconds()
    {
        var configured = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.UsenetIdleConnectionTimeoutSeconds));
        if (configured is null || !int.TryParse(configured, out var value))
            return 60;
        return Math.Clamp(value, 15, 300);
    }

    /// <summary>
    /// Whether each NNTP provider keeps a small pool of pre-connected sockets ready
    /// after idle periods. Enabled by default to keep playback off the TLS ramp.
    /// </summary>
    public bool IsWarmConnectionsEnabled()
    {
        var configured = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.UsenetWarmConnectionsEnabled));
        return configured is null || bool.Parse(configured);
    }

    /// <summary>
    /// Idle NNTP sockets to keep ready per provider. An unset value derives a small
    /// floor from the provider width; explicit values are clamped to that width.
    /// </summary>
    public int GetWarmConnectionsFloor(int maxConnections)
    {
        maxConnections = Math.Max(1, maxConnections);
        var configured = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.UsenetWarmConnectionsFloor));
        if (configured is null || !int.TryParse(configured, out var value))
            return Math.Clamp(maxConnections / 6, 1, 8);
        return Math.Clamp(value, 1, maxConnections);
    }

    /// <summary>
    /// How long a definitive per-provider (or per-storage-group) article miss stays
    /// cached before it is re-probed. Default 300s; clamped to [30, 86400].
    /// </summary>
    public TimeSpan GetArticleMissCacheTtl()
    {
        var configured = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.UsenetArticleMissCacheTtlSeconds));
        if (configured is null || !int.TryParse(configured, out var seconds))
            return TimeSpan.FromSeconds(300);
        return TimeSpan.FromSeconds(Math.Clamp(seconds, 30, 86400));
    }

    /// <summary>
    /// Upper bound on the number of cached article-miss entries before the oldest
    /// are evicted. Default 10000; clamped to [100, 1000000].
    /// </summary>
    public int GetArticleMissCacheMaxEntries()
    {
        var configured = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.UsenetArticleMissCacheMaxEntries));
        if (configured is null || !int.TryParse(configured, out var value))
            return 10_000;
        return Math.Clamp(value, 100, 1_000_000);
    }

    public bool IsPipelinedBodyRequestsEnabled()
    {
        var configValue = StringUtil.EmptyToNull(
            GetConfigValue(ConfigKeys.UsenetPipelinedBodyRequests));
        return configValue == null || bool.Parse(configValue);
    }

    public bool IsContainerAwareFillEnabled()
    {
        var configValue = StringUtil.EmptyToNull(
            GetConfigValue(ConfigKeys.UsenetContainerAwareFill));
        return configValue == null || bool.Parse(configValue);
    }

    public bool IsSegmentCacheEnabled()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.UsenetSegmentCacheEnabled));
        return v == null || bool.Parse(v);
    }

    public string GetSegmentCachePath()
    {
        return StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.UsenetSegmentCachePath))
               ?? "/config/segment-cache";
    }

    public long GetSegmentCacheMaxBytes()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.UsenetSegmentCacheMaxGb));
        var gb = long.TryParse(v, out var n) ? n : 10;
        return Math.Max(1, gb) * 1024L * 1024L * 1024L;
    }

    public bool IsPar2RepairEnabled()
    {
        var repairValue = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.RepairEnable));
        if (repairValue == null || !bool.Parse(repairValue)) return false;
        var par2Value = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.RepairPar2Enabled));
        return par2Value == null || bool.Parse(par2Value);
    }

    public bool IsDegradedToleranceEnabled()
    {
        var repairValue = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.RepairEnable));
        if (repairValue == null || !bool.Parse(repairValue)) return false;
        var toleranceValue = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.RepairDegradedToleranceEnabled));
        return toleranceValue == null || bool.Parse(toleranceValue);
    }

    public bool IsCorruptionTrackingEnabled()
    {
        var repairValue = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.RepairEnable));
        if (repairValue == null || !bool.Parse(repairValue)) return false;
        var trackingValue = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.RepairCorruptionTrackingEnabled));
        return trackingValue == null || bool.Parse(trackingValue);
    }

    public int GetDegradedMaxConsecutiveMissing()
    {
        var value = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.RepairDegradedMaxConsecutiveMissing));
        return int.TryParse(value, out var number)
            ? Math.Clamp(number, 1, GapFillLimits.MaxConsecutiveZeroFills - 1)
            : 2;
    }

    public int GetDegradedMaxTotalMissing()
    {
        var value = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.RepairDegradedMaxTotalMissing));
        return int.TryParse(value, out var number) ? Math.Clamp(number, 1, 1000) : 5;
    }

    public double GetDegradedMaxMissingBytePercent()
    {
        var value = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.RepairDegradedMaxMissingBytePercent));
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            ? Math.Clamp(number, 0.01, 50)
            : 1;
    }

    public bool IsPar2PreferredOverArr()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.RepairPar2PreferredOverArr));
        return v == null || bool.Parse(v);
    }

    public int GetPar2MaxMissingSlices()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.RepairPar2MaxMissingSlices));
        return int.TryParse(v, out var n) ? Math.Clamp(n, 1, 64) : 8;
    }

    public int GetPar2MaxReleaseGb()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.RepairPar2MaxReleaseGb));
        return int.TryParse(v, out var n) ? Math.Clamp(n, 1, 200) : 16;
    }

    public int GetPar2MaxMemoryMb()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.RepairPar2MaxMemoryMb));
        return int.TryParse(v, out var n) ? Math.Clamp(n, 64, 2048) : 256;
    }

    public long GetPar2MaxPatchBytes()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.RepairPar2MaxPatchGb));
        var gb = int.TryParse(v, out var n) ? Math.Clamp(n, 1, 100) : 4;
        return gb * 1024L * 1024L * 1024L;
    }

    public int GetPar2FetchConcurrency()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.RepairPar2FetchConcurrency));
        return int.TryParse(v, out var n) ? Math.Clamp(n, 1, 8) : 2;
    }

    public int GetPar2FailureCooldownHours()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.RepairPar2FailureCooldownHours));
        return int.TryParse(v, out var n) ? Math.Clamp(n, 1, 168) : 6;
    }

    public string GetRepairPatchStorePath()
        => Path.Join(DavDatabaseContext.ConfigPath, "repair-segments");

    // When true, RAR archives are mounted instantly by parsing only the first
    // volume at import; trailing volumes are resolved on first read. Falls
    // back to eager parsing for archives that don't fit the supported shape
    // (multi-file, solid, encrypted, or compressed).
    public bool IsLazyRarParsingEnabled()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.ApiLazyRarParsing));
        return v == null || bool.Parse(v);
    }

    public SemaphorePriorityOdds GetStreamingPriority()
    {
        var stringValue = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.UsenetStreamingPriority));
        var numericalValue = int.TryParse(stringValue, out var n) ? Math.Clamp(n, 0, 100) : 80;
        return new SemaphorePriorityOdds { HighPriorityOdds = numericalValue };
    }

    public TimeSpan GetStreamingSegmentTimeout()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.UsenetStreamingSegmentTimeoutSeconds));
        var seconds = int.TryParse(v, out var n) ? Math.Clamp(n, 2, 40) : 8;
        return TimeSpan.FromSeconds(seconds);
    }

    public int GetStreamingSegmentRetries()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.UsenetStreamingSegmentRetries));
        return int.TryParse(v, out var n) ? Math.Clamp(n, 0, 5) : 3;
    }

    /// <summary>
    /// Total backend-wait budget for a single WebDAV/`/view` GET or range read —
    /// covers download-semaphore admission, the connection-pool gate, and segment
    /// delivery together. Distinct from (and larger than) the per-segment timeout:
    /// this bounds the whole read so a stuck provider fails the HTTP request
    /// instead of blocking until the client disconnects.
    /// </summary>
    public TimeSpan GetStreamingReadTimeout()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.UsenetStreamingReadTimeoutSeconds));
        var seconds = int.TryParse(v, out var n) ? Math.Clamp(n, 5, 120) : 30;
        return TimeSpan.FromSeconds(seconds);
    }

    /// <summary>
    /// Per-write deadline for streaming response bytes to the client. A write that has not
    /// completed within this window means the client stopped reading but kept the connection
    /// open (HTTP/2 flow control, tunnel, or proxy). The same window is also the aggregate
    /// reclaim interval: a stream that transfers less than 64 KB of write-side progress per
    /// window while other streams are waiting on Article RAM is cancelled so its leases
    /// release instead of wedging the host. 0 disables both the per-write and aggregate
    /// watchdogs.
    /// </summary>
    public TimeSpan GetStreamingWriteTimeout()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.UsenetStreamingWriteTimeoutSeconds));
        var seconds = int.TryParse(v, out var n) ? Math.Clamp(n, 0, 600) : 60;
        return TimeSpan.FromSeconds(seconds);
    }

    public bool IsEnforceReadonlyWebdavEnabled()
    {
        var defaultValue = true;
        var configValue = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.WebdavEnforceReadonly));
        return (configValue != null ? bool.Parse(configValue) : defaultValue);
    }

    public bool IsWindowsSafePathsEnabled()
    {
        var defaultValue = true;
        var configValue = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.WebdavWindowsSafePaths));
        return configValue != null ? bool.Parse(configValue) : defaultValue;
    }

    public HashSet<string> GetEnsureArticleExistenceCategories()
    {
        var configValue = GetConfigValue(ConfigKeys.ApiEnsureArticleExistenceCategories);
        return (configValue ?? "").Split(',')
            .Select(x => x.Trim())
            .Select(x => x.ToLowerInvariant())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToHashSet();
    }

    public string GetArticleExistenceCheckMode()
    {
        var configured = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.ApiArticleExistenceCheckMode));
        return configured?.ToLowerInvariant() == "sampled" ? "sampled" : "full";
    }

    public bool IsPlaybackWatchdogEnabled()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.PlayWatchdogEnabled));
        return v == null || bool.Parse(v);
    }

    public int GetPlayTotalBudgetSeconds()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.PlayTotalBudgetSeconds));
        if (v == null) return 30;
        return int.TryParse(v, out var n) ? Math.Clamp(n, 3, 180) : 30;
    }

    public int GetPlayHedgeDelaySeconds()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.PlayHedgeDelaySeconds));
        if (v == null) return 3;
        return int.TryParse(v, out var n) ? Math.Clamp(n, 1, 30) : 3;
    }

    public int GetPlayMaxCandidates()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.PlayMaxCandidates));
        if (v == null) return 3;
        return int.TryParse(v, out var n) ? Math.Clamp(n, 1, 10) : 3;
    }

    public int GetPlayMaxAttempts()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.PlayMaxAttempts));
        if (v == null) return 10;
        return int.TryParse(v, out var n) ? Math.Clamp(n, 1, 200) : 10;
    }

    public string GetPlayVerifyMode()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.PlayVerifyMode));
        return v switch
        {
            "body" => "body",
            "stat" => "stat",
            "none" => "none",
            _ => "none",
        };
    }

    public int GetPlayVerifySampleCount()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.PlayVerifySampleCount));
        if (v == null) return 3;
        return int.TryParse(v, out var n) ? Math.Clamp(n, 1, 10) : 3;
    }

    public TimeSpan GetPlayCandidateNegativeCacheTtl()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.PlayCandidateNegativeCacheMinutes));
        if (v == null) return TimeSpan.FromMinutes(5);
        return int.TryParse(v, out var n) ? TimeSpan.FromMinutes(Math.Clamp(n, 1, 60 * 24)) : TimeSpan.FromMinutes(5);
    }

    /// <summary>
    /// Lifetime of play tokens served in search results. Config key wins over
    /// RESOLUTION_CACHE_TTL_HOURS; default 7 days to outlive consumer search caches.
    /// </summary>
    public TimeSpan GetPlayResolutionCacheTtl()
    {
        var raw = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.PlayResolutionCacheTtlHours))
                  ?? EnvironmentUtil.GetEnvironmentVariable("RESOLUTION_CACHE_TTL_HOURS");
        if (int.TryParse(raw, out var hours)) return TimeSpan.FromHours(Math.Clamp(hours, 1, 720));
        return TimeSpan.FromHours(168);
    }

    public bool IsPlaySubtitlePreferenceEnabled()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.PlayPreferSubtitles));
        return v == null || !bool.TryParse(v, out var enabled) || enabled;
    }

    public bool IsGrabStallFailoverEnabled()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.GrabStallFailoverEnabled));
        return v == null || bool.Parse(v);
    }

    public int GetGrabStallFailoverWindowSeconds()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.GrabStallFailoverWindowSeconds));
        if (v == null) return 2;
        return int.TryParse(v, out var n) ? Math.Clamp(n, 2, 60) : 2;
    }

    public int GetGrabStallFailoverCeilingSeconds()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.GrabStallFailoverCeilingSeconds));
        if (v == null) return 5;
        return int.TryParse(v, out var n) ? Math.Clamp(n, 5, 120) : 5;
    }

    public const int DefaultExcludeSyncRefreshMinutes = 720;

    public IReadOnlyList<Regex> GetSearchExcludePatterns()
    {
        lock (_excludeLock)
        {
            return _compiledExcludeCache ??= BuildExcludePatterns();
        }
    }

    // Synced patterns (last-good cache, in configured-URL order) take precedence and are
    // emitted first; the manual textarea is appended after, with exact duplicates dropped
    // so a pattern present in both sources is compiled and evaluated only once.
    private List<Regex> BuildExcludePatterns()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var compiled = new List<Regex>();

        var cache = GetSearchExcludeSyncCache();
        foreach (var url in GetSearchExcludeSyncUrls())
        {
            if (!cache.Urls.TryGetValue(url, out var entry)) continue;
            foreach (var item in entry.Items)
                if (ExcludePatternParser.Parse(item) is { } p && seen.Add(p.Key))
                    compiled.Add(p.Regex);
        }

        var raw = GetConfigValue(ConfigKeys.SearchExcludePatterns);
        if (string.IsNullOrWhiteSpace(raw)) raw = GetConfigValue(ConfigKeys.PlayExcludePatterns);
        if (!string.IsNullOrWhiteSpace(raw))
        {
            foreach (var line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                if (ExcludePatternParser.Parse(line) is { } p && seen.Add(p.Key))
                    compiled.Add(p.Regex);
        }

        return compiled;
    }

    /// <summary>Configured synced-exclude source URLs (http/https only, de-duplicated, in order).</summary>
    public IReadOnlyList<string> GetSearchExcludeSyncUrls()
    {
        var raw = GetConfigValue(ConfigKeys.SearchExcludeSyncUrls);
        if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<string>();

        var urls = new List<string>();
        // Exact-match dedup: URL paths are case-sensitive, so two URLs that differ only
        // by path casing are distinct sources and must both be kept.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith('#')) continue;
            if (!Uri.TryCreate(line, UriKind.Absolute, out var uri)) continue;
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) continue;
            if (seen.Add(line)) urls.Add(line);
        }
        return urls;
    }

    public int GetSearchExcludeSyncRefreshMinutes()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.SearchExcludeSyncRefreshMinutes));
        if (v == null) return DefaultExcludeSyncRefreshMinutes;
        return int.TryParse(v, out var n) ? Math.Clamp(n, 15, 10080) : DefaultExcludeSyncRefreshMinutes;
    }

    public ExcludeSyncCache GetSearchExcludeSyncCache()
    {
        var raw = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.SearchExcludeSyncCache));
        if (raw == null) return new ExcludeSyncCache();
        try { return JsonSerializer.Deserialize<ExcludeSyncCache>(raw) ?? new ExcludeSyncCache(); }
        catch (JsonException) { return new ExcludeSyncCache(); }
    }

    public const int DefaultProwlarrSyncIntervalMinutes = 60;

    public string? GetProwlarrUrl()
    {
        return StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.ProwlarrUrl))?.TrimEnd('/');
    }

    public string? GetProwlarrApiKey()
    {
        return StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.ProwlarrApiKey));
    }

    public bool IsProwlarrSyncEnabled()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.ProwlarrSyncEnabled));
        return v is not null && bool.Parse(v);
    }

    public int GetProwlarrSyncIntervalMinutes()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.ProwlarrSyncIntervalMinutes));
        if (v == null) return DefaultProwlarrSyncIntervalMinutes;
        return int.TryParse(v, out var n) ? Math.Clamp(n, 5, 10080) : DefaultProwlarrSyncIntervalMinutes;
    }

    public ProwlarrSyncStatus GetProwlarrSyncStatus()
    {
        var raw = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.ProwlarrSyncStatus));
        if (raw == null) return new ProwlarrSyncStatus();
        try { return JsonSerializer.Deserialize<ProwlarrSyncStatus>(raw) ?? new ProwlarrSyncStatus(); }
        catch (JsonException) { return new ProwlarrSyncStatus(); }
    }

    public string GetVariantsMode()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.VariantsMode));
        return v switch
        {
            "smart" => "smart",
            "collect-all" => "collect-all",
            _ => "off",
        };
    }

    public int GetVariantsTolerancePct()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.VariantsTolerancePct));
        if (v == null) return 25;
        return int.TryParse(v, out var n) ? Math.Clamp(n, 0, 100) : 25;
    }

    public int GetVariantsMaxPerGroup()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.VariantsMaxPerGroup));
        if (v == null) return 3;
        return int.TryParse(v, out var n) ? Math.Max(0, n) : 3;
    }

    public string GetVariantsReplayStrategy()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.VariantsReplayStrategy));
        return v switch
        {
            "largest" => "largest",
            "smallest" => "smallest",
            _ => "closest-to-click",
        };
    }

    public bool IsVariantsFallbackOnFailureEnabled()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.VariantsFallbackOnFailure));
        return v == null || bool.Parse(v);
    }

    public bool IsVariantsSegmentDonorsEnabled()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.VariantsSegmentDonorsEnabled));
        return v == null || bool.Parse(v);
    }

    public int GetVariantsSegmentDonorsMaxSiblings()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.VariantsSegmentDonorsMaxSiblings));
        if (v == null) return 3;
        return int.TryParse(v, out var n) ? Math.Clamp(n, 0, 10) : 3;
    }

    public int GetVariantsSegmentDonorsMaxPerSegment()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.VariantsSegmentDonorsMaxPerSegment));
        if (v == null) return 6;
        return int.TryParse(v, out var n) ? Math.Clamp(n, 1, 32) : 6;
    }

    public string GetVariantsEvictionStrategy()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.VariantsEvictionStrategy));
        return v switch
        {
            "largest-first" => "largest-first",
            "smallest-first" => "smallest-first",
            "never" => "never",
            _ => "lru",
        };
    }

    public int GetVariantsEvictionActiveGraceSeconds()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.VariantsEvictionActiveGraceSeconds));
        if (v == null) return 60;
        return int.TryParse(v, out var n) ? Math.Clamp(n, 0, 300) : 60;
    }

    public string GetPreflightMode()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.PreflightMode));
        return v switch
        {
            "light" => "light",
            "standard" => "standard",
            "full" => "full",
            _ => "off",
        };
    }

    public int GetPreflightMaxAttempts()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.PreflightMaxAttempts));
        if (v == null) return 20;
        return int.TryParse(v, out var n) ? Math.Clamp(n, 1, 50) : 20;
    }

    public int GetPreflightTtlSeconds()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.PreflightTtlSeconds));
        if (v == null) return 120;
        return int.TryParse(v, out var n) ? Math.Clamp(n, 10, 1800) : 120;
    }

    public int GetPreflightIndexerMaxWaitSeconds()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.PreflightIndexerMaxWaitSeconds));
        if (v == null) return 5;
        return int.TryParse(v, out var n) ? Math.Clamp(n, 0, 120) : 5;
    }


    public bool IsWatchtowerEnabled()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.WatchtowerEnabled));
        return v != null && bool.Parse(v);
    }

    public bool IsWatchtowerAutoThroughput()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.WatchtowerAutoThroughput));
        return v != null && bool.Parse(v);
    }

    public bool IsWatchtowerVerboseLoggingEnabled()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.WatchtowerVerboseLogging));
        return v != null && bool.Parse(v);
    }

    public string GetWatchtowerProfileToken()
    {
        return StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.WatchtowerProfileToken)) ?? "";
    }

    public string GetWatchtowerRanking()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.WatchtowerRanking));
        return v == "largest" ? "largest" : "watchdog";
    }

    public long GetWatchtowerSizeFloorBytes()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.WatchtowerSizeFloorBytes));
        if (v == null) return 524288000L;
        return long.TryParse(v, out var n) ? Math.Max(0, n) : 524288000L;
    }

    public long GetWatchtowerSizeCeilingBytes()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.WatchtowerSizeCeilingBytes));
        if (v == null) return 0L;
        return long.TryParse(v, out var n) ? Math.Max(0, n) : 0L;
    }

    public int GetWatchtowerMinGrabs()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.WatchtowerMinGrabs));
        if (v == null) return 0;
        return int.TryParse(v, out var n) ? Math.Max(0, n) : 0;
    }

    public int GetWatchtowerShortlistDepth()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.WatchtowerShortlistDepth));
        if (v == null) return 2;
        return int.TryParse(v, out var n) ? Math.Clamp(n, 1, 5) : 2;
    }

    public int GetWatchtowerGrabCapPerResolve()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.WatchtowerGrabCapPerResolve));
        if (v == null) return 3;
        return int.TryParse(v, out var n) ? Math.Clamp(n, 1, 10) : 3;
    }

    public int GetWatchtowerVerifySampleCount()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.WatchtowerVerifySampleCount));
        if (v == null) return 3;
        return int.TryParse(v, out var n) ? Math.Clamp(n, 1, 20) : 3;
    }

    public int GetWatchtowerVerifyTimeoutSeconds()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.WatchtowerVerifyTimeoutSeconds));
        if (v == null) return 10;
        return int.TryParse(v, out var n) ? Math.Clamp(n, 2, 120) : 10;
    }

    public int GetWatchtowerActiveSetCap()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.WatchtowerActiveSetCap));
        if (v == null) return 100;
        return int.TryParse(v, out var n) ? Math.Clamp(n, 1, 100000) : 100;
    }

    public int GetWatchtowerResolveConcurrency()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.WatchtowerResolveConcurrency));
        if (v == null) return 3;
        return int.TryParse(v, out var n) ? Math.Clamp(n, 1, 16) : 3;
    }

    public int GetWatchtowerDailyResolveBudget()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.WatchtowerDailyResolveBudget));
        if (v == null) return 60;
        return int.TryParse(v, out var n) ? Math.Max(0, n) : 60;
    }

    public int GetWatchtowerSyncIntervalSeconds()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.WatchtowerSyncIntervalSeconds));
        if (v == null) return 3600;
        return int.TryParse(v, out var n) ? Math.Clamp(n, 60, 86400) : 3600;
    }

    public int GetWatchtowerKeepFreshBaseSeconds()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.WatchtowerKeepfreshBaseSeconds));
        if (v == null) return 21600;
        return int.TryParse(v, out var n) ? Math.Clamp(n, 300, 604800) : 21600;
    }

    public int GetWatchtowerKeepFreshMaxSeconds()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.WatchtowerKeepfreshMaxSeconds));
        if (v == null) return 604800;
        return int.TryParse(v, out var n) ? Math.Clamp(n, 600, 2592000) : 604800;
    }

    public int GetWatchtowerUnavailableRetrySeconds()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.WatchtowerUnavailableRetrySeconds));
        if (v == null) return 21600;
        return int.TryParse(v, out var n) ? Math.Clamp(n, 600, 604800) : 21600;
    }

    public string GetWatchtowerSeriesScope()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.WatchtowerSeriesScope));
        return NormalizeSeriesScope(v) ?? "latest-season";
    }

    public static string? NormalizeSeriesScope(string? value)
    {
        return StringUtil.EmptyToNull(value) switch
        {
            "latest-season" => "latest-season",
            "first-season" => "first-season",
            "all-aired" => "all-aired",
            "recent" => "recent",
            "off" => "off",
            _ => null,
        };
    }

    public int GetWatchtowerSeriesMaxEpisodes()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.WatchtowerSeriesMaxEpisodes));
        if (v == null) return 50;
        return int.TryParse(v, out var n) ? Math.Clamp(n, 0, 1000) : 50;
    }

    public string GetWatchtowerSeriesCapKeep()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.WatchtowerSeriesCapKeep));
        return v == "oldest" ? "oldest" : "newest";
    }

    public int GetWatchtowerSeriesRecentCount()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.WatchtowerSeriesRecentCount));
        if (v == null) return 3;
        return int.TryParse(v, out var n) ? Math.Clamp(n, 1, 100) : 3;
    }

    public bool IsWatchtowerSeasonBundlesEnabled()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.WatchtowerSeasonBundles));
        return v == null || bool.Parse(v);
    }

    public bool IsWatchtowerSeasonBundleFallbackEnabled()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.WatchtowerSeasonBundleFallback));
        return v != null && bool.Parse(v);
    }

    public string GetWatchtowerSeasonBundleFallbackScope()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.WatchtowerSeasonBundleFallbackScope));
        return v switch
        {
            "all" => "all",
            "recent" => "recent",
            _ => "latest-season",
        };
    }

    public int GetWatchtowerSeasonBundleFallbackRecentCount()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.WatchtowerSeasonBundleFallbackRecentCount));
        if (v == null) return 2;
        return int.TryParse(v, out var n) ? Math.Clamp(n, 1, 100) : 2;
    }

    public int GetWatchtowerSeasonBundleFallbackMaxEpisodes()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.WatchtowerSeasonBundleFallbackMaxEpisodes));
        if (v == null) return 50;
        return int.TryParse(v, out var n) ? Math.Clamp(n, 1, 1000) : 50;
    }

    public bool IsPreviewPar2FilesEnabled()
    {
        var defaultValue = false;
        var configValue = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.WebdavPreviewPar2Files));
        return (configValue != null ? bool.Parse(configValue) : defaultValue);
    }

    public bool IsIgnoreSabHistoryLimitEnabled()
    {
        var defaultValue = true;
        var configValue = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.ApiIgnoreHistoryLimit));
        return (configValue != null ? bool.Parse(configValue) : defaultValue);
    }

    /// <summary>
    /// Server-side ceiling for SAB history <c>slots</c> returned in one response.
    /// Default 10_000 — far above a typical Arr import pass — removes the unbounded
    /// <c>int.MaxValue</c> worst case when ignore-history-limit is enabled.
    /// </summary>
    public int GetHistoryMaxPageSize()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.ApiHistoryMaxPageSize));
        if (v == null) return 10_000;
        return int.TryParse(v, out var n) ? Math.Clamp(n, 1, 100_000) : 10_000;
    }

    public bool IsRepairJobEnabled()
    {
        return GetRepairDisabledReason() == null;
    }

    /// <summary>
    /// When repairs are disabled, returns a human-readable reason.
    /// </summary>
    public string? GetRepairDisabledReason()
    {
        var configValue = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.RepairEnable));
        var isRepairJobEnabled = configValue != null && bool.Parse(configValue);
        return GetRepairDisabledReason(isRepairJobEnabled);
    }

    internal static string? GetRepairDisabledReason(bool isRepairEnabled)
    {
        if (!isRepairEnabled)
            return "Enable Background Repairs is off";

        return null;
    }

    /// <summary>
    /// Max concurrent NNTP STAT connections for health checks.
    /// Capped at the configured provider pool size to avoid pool starvation.
    /// </summary>
    public int GetHealthCheckConcurrency()
    {
        var poolSize = GetUsenetProviderConfig().TotalPooledConnections;
        var configured = int.Parse(
            StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.RepairHealthcheckConcurrency))
            ?? "50"
        );
        return Math.Clamp(configured, 1, Math.Max(1, poolSize));
    }

    /// <summary>
    /// How much of each file a health check reads. Unrecognized values fall back to the
    /// default rather than throwing, so a hand-edited database cannot stop health checks.
    /// </summary>
    public HealthCheckDepth GetHealthCheckDepth()
    {
        var configured = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.RepairHealthcheckDepth));
        return configured?.ToLowerInvariant() switch
        {
            "enhanced" => HealthCheckDepth.Enhanced,
            "deep" => HealthCheckDepth.Deep,
            "complete" => HealthCheckDepth.Complete,
            _ => DefaultHealthCheckDepth,
        };
    }

    /// <summary>
    /// Whether coverage tapers as a release ages. Off by default, so every release is
    /// checked at the depth its size earns regardless of how long the post has survived.
    /// </summary>
    public bool IsHealthCheckAgingEnabled()
    {
        var configValue = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.RepairHealthcheckAging));
        return configValue != null && bool.Parse(configValue);
    }

    /// <summary>
    /// Number of streaming failures before urgent repair starts.
    /// 0 (default) preserves immediate-repair behavior.
    /// </summary>
    public int GetAutoRemoveAfterFailures()
    {
        var configValue = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.RepairAutoRemoveAfterFailures));
        if (configValue == null) return 0;
        return int.TryParse(configValue, out var n) ? Math.Max(0, n) : 0;
    }

    /// <summary>
    /// When true (default), library-linked items are removed and blocklisted through *Arr at the
    /// failure threshold. When false, linked items are force-deleted after the threshold as well.
    /// </summary>
    public bool IsAutoRemoveUnlinkedOnly()
    {
        var configValue = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.RepairAutoRemoveUnlinkedOnly));
        return configValue == null || bool.Parse(configValue);
    }

    public ArrConfig GetArrConfig()
    {
        var defaultValue = new ArrConfig();
        return GetConfigValue<ArrConfig>(ConfigKeys.ArrInstances) ?? defaultValue;
    }

    /// <summary>
    /// Master switch for Arr Health polling and the Overview widget.
    /// Defaults true; the feature is dormant until at least one instance is enabled.
    /// </summary>
    public bool IsArrHealthEnabled()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.ArrHealthEnabled));
        return v == null || bool.Parse(v);
    }

    public UsenetProviderConfig GetUsenetProviderConfig()
    {
        var defaultValue = new UsenetProviderConfig();
        return GetConfigValue<UsenetProviderConfig>(ConfigKeys.UsenetProviders) ?? defaultValue;
    }

    public IndexerConfig GetIndexerConfig()
    {
        return GetConfigValue<IndexerConfig>(ConfigKeys.IndexersInstances) ?? new IndexerConfig();
    }

    public ProfileConfig GetProfileConfig()
    {
        return (GetConfigValue<ProfileConfig>(ConfigKeys.ProfilesInstances) ?? new ProfileConfig()).Normalized();
    }

    public string GetDuplicateNzbBehavior()
    {
        var defaultValue = "increment";
        return GetConfigValue(ConfigKeys.ApiDuplicateNzbBehavior) ?? defaultValue;
    }

    public HashSet<string> GetBlocklistedFiles()
    {
        // *sample.mkv is intentionally omitted: sample detection is handled by
        // IsSampleFilterEnabled / FileFilterUtil (name + size ratio).
        var defaultValue = "*.nfo, *.par2, *.sfv, *unpack.mkv, *.unpack.mp4";
        return (GetConfigValue(ConfigKeys.ApiDownloadFileBlocklist) ?? defaultValue)
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.ToLowerInvariant())
            .ToHashSet();
    }

    public bool IsSampleFilterEnabled()
    {
        var defaultValue = true;
        var configValue = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.ApiSampleFilterEnabled));
        return configValue != null ? bool.Parse(configValue) : defaultValue;
    }

    public string GetImportStrategy()
    {
        return StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.ApiImportStrategy))?.ToLowerInvariant()
               ?? "symlinks";
    }

    /// <summary>
    /// Whether STRM files should be emitted at queue completion. Existing installations
    /// without an explicit output toggle retain their legacy import-strategy behavior.
    /// </summary>
    public bool IsStrmOutputEnabled()
    {
        return GetImportStrategy() == "strm"
               || GetConfiguredOutputEnabled(ConfigKeys.ApiStrmOutputEnabled);
    }

    /// <summary>
    /// Whether the symlink output is enabled. The virtual completed-symlinks tree
    /// remains available independently; a configured output directory additionally
    /// enables filesystem symlink creation at queue completion.
    /// </summary>
    public bool IsSymlinkOutputEnabled()
    {
        return GetImportStrategy() == "symlinks"
               || GetConfiguredOutputEnabled(ConfigKeys.ApiSymlinkOutputEnabled);
    }

    public string? GetSymlinkOutputDirectory()
    {
        return StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.ApiSymlinkOutputDir));
    }

    public string GetSymlinkCompletedDownloadDir()
    {
        var outputDirectory = GetSymlinkOutputDirectory();
        return outputDirectory is not null
            ? Path.GetFullPath(outputDirectory)
            : Path.Join(GetRcloneMountDir(), "completed-symlinks");
    }

    private bool GetConfiguredOutputEnabled(string configKey)
    {
        var configured = StringUtil.EmptyToNull(GetConfigValue(configKey));
        return configured is not null && bool.TryParse(configured, out var enabled) && enabled;
    }

    public string GetStrmCompletedDownloadDir()
    {
        return GetConfigValue(ConfigKeys.ApiCompletedDownloadsDir) ?? "/data/completed-downloads";
    }

    public string GetBaseUrl()
    {
        return GetConfigValue(ConfigKeys.GeneralBaseUrl) ?? "http://localhost:3000";
    }

    public bool IsRcloneRemoteControlEnabled()
    {
        var defaultValue = false;
        var configValue = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.RcloneRcEnabled));
        return (configValue != null ? bool.Parse(configValue) : defaultValue);
    }

    public string? GetRcloneHost()
    {
        return GetConfigValue(ConfigKeys.RcloneHost);
    }

    public string? GetRcloneUser()
    {
        return GetConfigValue(ConfigKeys.RcloneUser);
    }

    public string? GetRclonePass()
    {
        return GetConfigValue(ConfigKeys.RclonePass);
    }

    public string GetUserAgent()
    {
        var defaultValue = "SABnzbd/5.1.0";
        return StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.ApiUserAgent))
               ?? EnvironmentUtil.GetEnvironmentVariable("NZB_GRAB_USER_AGENT")
               ?? defaultValue;
    }

    /// <summary>
    /// Comma/whitespace-separated hostnames, IP literals, CIDRs, or <c>*</c>
    /// exempted from the addurl SSRF private-address guard. Falls back to
    /// <c>TRUSTED_INTERNAL_HOSTS</c> when the config value is empty.
    /// </summary>
    public string? GetAddUrlTrustedHosts()
    {
        return StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.ApiAddUrlTrustedHosts))
               ?? EnvironmentUtil.GetEnvironmentVariable("TRUSTED_INTERNAL_HOSTS");
    }

    public string GetSearchUserAgent()
    {
        var defaultValue = $"nzbdav/{AppVersion}";
        return StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.ApiSearchUserAgent))
               ?? EnvironmentUtil.GetEnvironmentVariable("NZB_SEARCH_USER_AGENT")
               ?? defaultValue;
    }

    public bool IsDatabaseStartupVacuumEnabled()
    {
        var defaultValue = false;
        var configValue = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.DbIsStartupVacuumEnabled));
        return (configValue != null ? bool.Parse(configValue) : defaultValue);
    }

    /// <summary>
    /// Days to keep SAB history rows. 0 disables age-based pruning.
    /// Config key wins over DATABASE_HISTORY_RETENTION_DAYS; default is 90.
    /// Automatic retention never deletes mounted WebDAV content.
    /// </summary>
    public int GetHistoryRetentionDays()
    {
        return GetRetentionDaysSetting(
            ConfigKeys.DatabaseHistoryRetentionDays,
            "DATABASE_HISTORY_RETENTION_DAYS",
            defaultValue: 90);
    }

    /// <summary>
    /// Days to keep health-check result rows. 0 disables age-based pruning.
    /// Config key wins over DATABASE_HEALTHCHECK_RETENTION_DAYS; default is 30.
    /// </summary>
    public int GetHealthResultRetentionDays()
    {
        return GetRetentionDaysSetting(
            ConfigKeys.DatabaseHealthcheckRetentionDays,
            "DATABASE_HEALTHCHECK_RETENTION_DAYS",
            defaultValue: 30);
    }

    /// <summary>
    /// Hours to keep raw per-fetch metrics rows (<c>SegmentFetches</c>, <c>FailoverMisses</c>).
    /// Rollups retain aggregates longer. 0 requests rollup-only retention; the sweep enforces
    /// a one-hour floor. Default is 24; max is 8760 (one year).
    /// </summary>
    public int GetMetricsFetchRetentionHours()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.MetricsFetchRetentionHours))
                ?? EnvironmentUtil.GetEnvironmentVariable("METRICS_FETCH_RETENTION_HOURS");
        return int.TryParse(v, out var n) ? Math.Clamp(n, 0, 8760) : 24;
    }

    private int GetRetentionDaysSetting(string configKey, string? environmentVariable, int defaultValue)
    {
        var rawValue = StringUtil.EmptyToNull(GetConfigValue(configKey))
                       ?? (environmentVariable != null ? EnvironmentUtil.GetEnvironmentVariable(environmentVariable) : null);
        return int.TryParse(rawValue, out var parsed) && parsed >= 0 ? parsed : defaultValue;
    }

    public bool IsNzbBackupEnabled()
    {
        var defaultValue = false;
        var configValue = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.ApiNzbBackupEnabled));
        return (configValue != null ? bool.Parse(configValue) : defaultValue);
    }

    public string? GetNzbBackupLocation()
    {
        return StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.ApiNzbBackupLocation));
    }

    /// <summary>
    /// Days to keep on-disk NZB backup files (written by <c>AddFileController</c> when
    /// <see cref="IsNzbBackupEnabled"/> is on). 0 disables age-based pruning. Default is 30.
    /// </summary>
    public int GetNzbBackupRetentionDays()
    {
        return GetRetentionDaysSetting(
            ConfigKeys.ApiNzbBackupRetentionDays,
            environmentVariable: null,
            defaultValue: 30);
    }

    public bool IsRemoveOrphanedFilesScheduleEnabled()
    {
        var defaultValue = false;
        var configValue = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.MaintenanceRemoveOrphanedScheduleEnabled));
        return (configValue != null ? bool.Parse(configValue) : defaultValue);
    }

    public TimeSpan RemoveOrphanedFilesSchedule()
    {
        var defaultValue = TimeSpan.Zero;
        var configValue = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.MaintenanceRemoveOrphanedScheduleTime));
        if (configValue == null) return defaultValue;
        if (!int.TryParse(configValue, out var totalMinutes)) return defaultValue;
        if (totalMinutes < 0 || totalMinutes >= 24 * 60) return defaultValue;
        return TimeSpan.FromMinutes(totalMinutes);
    }

    public bool IsDatabaseBackupScheduleEnabled()
    {
        var defaultValue = false;
        var configValue = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.BackupScheduleEnabled));
        return configValue != null ? bool.Parse(configValue) : defaultValue;
    }

    public TimeSpan DatabaseBackupSchedule()
    {
        var defaultValue = TimeSpan.Zero;
        var configValue = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.BackupScheduleTime));
        if (configValue == null) return defaultValue;
        if (!int.TryParse(configValue, out var totalMinutes)) return defaultValue;
        if (totalMinutes < 0 || totalMinutes >= 24 * 60) return defaultValue;
        return TimeSpan.FromMinutes(totalMinutes);
    }

    public int GetDatabaseBackupRetentionCount()
    {
        var defaultValue = 5;
        var configValue = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.BackupRetentionCount));
        if (configValue == null) return defaultValue;
        if (!int.TryParse(configValue, out var count) || count < 0) return defaultValue;
        return count;
    }

    public class ConfigEventArgs : EventArgs
    {
        public required Dictionary<string, string> ChangedConfig { get; init; }
    }
}

internal sealed record ConfigDiagnosticSnapshot(
    string Key,
    string? Value,
    string Source,
    string? EnvironmentVariableName);

/// <summary>
/// A structured config value carried a property that maps to nothing on the target
/// type. <see cref="JsonPath"/> holds property names and array indices only, never a
/// value. It derives from <see cref="ArgumentException"/>, so widening strict
/// validation beyond the environment overlay would put the path in API responses.
/// </summary>
public sealed class ConfigUnmappedPropertyException(string configName, string jsonPath)
    : ArgumentException($"Config value for '{configName}' has an unknown or miscased property at '{jsonPath}'.")
{
    public string JsonPath { get; } = jsonPath;
}
