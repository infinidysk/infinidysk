namespace NzbWebDAV.Config;

/// <summary>
/// The single source of truth for every configuration key persisted in the
/// <c>ConfigItems</c> table. Using constants instead of scattered string literals
/// means a key can be found (and renamed) from one place, and that the getters in
/// <see cref="ConfigManager"/> and the <c>OnConfigChanged</c> subscribers cannot
/// silently drift apart.
/// </summary>
public static class ConfigKeys
{
    // api
    public const string ApiCategories = "api.categories";
    public const string ApiCompletedDownloadsDir = "api.completed-downloads-dir";
    public const string ApiDownloadFileBlocklist = "api.download-file-blocklist";
    public const string ApiSampleFilterEnabled = "api.sample-filter-enabled";
    public const string ApiDuplicateNzbBehavior = "api.duplicate-nzb-behavior";
    public const string ApiArticleExistenceCheckMode = "api.article-existence-check-mode";
    public const string ApiEnsureArticleExistenceCategories = "api.ensure-article-existence-categories";
    public const string ApiEnsureImportableVideo = "api.ensure-importable-video";
    public const string ApiIgnoreHistoryLimit = "api.ignore-history-limit";
    public const string ApiHistoryMaxPageSize = "api.history-max-page-size";
    public const string ApiImportStrategy = "api.import-strategy";
    public const string ApiKey = "api.key";
    public const string ApiLazyRarParsing = "api.lazy-rar-parsing";
    public const string ApiManualCategory = "api.manual-category";
    public const string ApiNzbBackupEnabled = "api.nzb-backup-enabled";
    public const string ApiNzbBackupLocation = "api.nzb-backup-location";
    public const string ApiNzbBackupRetentionDays = "api.nzb-backup-retention-days";
    public const string ApiSearchUserAgent = "api.search-user-agent";
    public const string ApiSkipNonVideoOnMissingArticles = "api.skip-non-video-on-missing-articles";
    public const string ApiStrmKey = "api.strm-key";
    public const string ApiUserAgent = "api.user-agent";
    public const string ApiAddUrlTrustedHosts = "api.addurl-trusted-hosts";
    public const string ApiRenameSingleVideoToRelease = "api.rename-single-video-to-release";

    // usenet
    public const string UsenetArticleBufferSize = "usenet.article-buffer-size";
    public const string UsenetArticleMissCacheTtlSeconds = "usenet.article-miss-cache-ttl-seconds";
    public const string UsenetArticleMissCacheMaxEntries = "usenet.article-miss-cache-max-entries";
    public const string UsenetContainerAwareFill = "usenet.container-aware-fill";
    public const string UsenetInFlightArticleBudgetMb = "usenet.in-flight-article-budget-mb";
    public const string UsenetCascadeEnabled = "usenet.cascade.enabled";
    public const string UsenetCascadeRetryPrimaryOnMiss = "usenet.cascade.retry-primary-on-miss";
    public const string UsenetIdleConnectionTimeoutSeconds = "usenet.idle-connection-timeout-seconds";
    public const string UsenetNntpReadTimeoutSeconds = "usenet.nntp-read-timeout-seconds";
    public const string UsenetReconnectDelayMilliseconds = "usenet.reconnect-delay-milliseconds";
    public const string UsenetWarmConnectionsEnabled = "usenet.warm-connections.enabled";
    public const string UsenetWarmConnectionsFloor = "usenet.warm-connections.floor";
    public const string UsenetCircuitBreakerInitialCooldownSeconds = "usenet.circuit-breaker.initial-cooldown-seconds";
    public const string UsenetCircuitBreakerMaxCooldownSeconds = "usenet.circuit-breaker.max-cooldown-seconds";
    public const string UsenetMaxDownloadConnections = "usenet.max-download-connections";
    public const string UsenetMaxDownloadConnectionsPerStream = "usenet.max-download-connections-per-stream";
    public const string UsenetMaxDownloadConnectionsPerStreamPreset = "usenet.max-download-connections-per-stream-preset";
    public const string UsenetMaxQueueConnections = "usenet.max-queue-connections";
    public const string UsenetMaxQueueConnectionsPreset = "usenet.max-queue-connections-preset";
    public const string QueueMaxItems = "queue.max-items";
    public const string QueueResumeThreshold = "queue.resume-threshold";
    public const string QueueWorkerCount = "queue.worker-count";
    public const string QueuePaused = "queue.paused";
    public const string QueueSpeedLimitKbps = "queue.speed-limit-kbps";
    public const string UsenetPipelinedBodyRequests = "usenet.pipelined-body-requests";
    public const string UsenetQueuePipeliningEnabled = "usenet.queue-pipelining.enabled";
    public const string UsenetQueuePipeliningDepth = "usenet.queue-pipelining.depth";
    // Deprecated: legacy alias of usenet.queue-pipelining.*; still read as fallback and env-mapped.
    public const string UsenetPipeliningDepth = "usenet.pipelining.depth";
    // Deprecated: legacy alias of usenet.queue-pipelining.*; still read as fallback and env-mapped.
    public const string UsenetPipeliningEnabled = "usenet.pipelining.enabled";
    public const string UsenetStreamingBodyBatchWidth = "usenet.streaming-body-batch-width";
    public const string UsenetProviders = "usenet.providers";
    public const string UsenetSegmentCacheEnabled = "usenet.segment-cache.enabled";
    public const string UsenetSegmentCacheMaxGb = "usenet.segment-cache.max-gb";
    public const string UsenetSegmentCachePath = "usenet.segment-cache.path";
    public const string UsenetStreamingPriority = "usenet.streaming-priority";
    public const string UsenetStreamingSegmentTimeoutSeconds = "usenet.streaming-segment-timeout-seconds";
    public const string UsenetStreamingSegmentRetries = "usenet.streaming-segment-retries";
    public const string UsenetStreamingReadTimeoutSeconds = "usenet.streaming-read-timeout-seconds";
    public const string UsenetStreamingWriteTimeoutSeconds = "usenet.streaming-write-timeout-seconds";
    public const string UsenetSharedStreamsEnabled = "usenet.shared-streams.enabled";
    public const string UsenetSharedStreamsMaxEntries = "usenet.shared-streams.max-entries";
    public const string UsenetSharedStreamsMaxEntriesPerFile = "usenet.shared-streams.max-entries-per-file";
    public const string UsenetSharedStreamsRingMb = "usenet.shared-streams.ring-mb";
    public const string UsenetSharedStreamsGraceSeconds = "usenet.shared-streams.grace-seconds";
    public const string UsenetSharedStreamsSmallRangeMaxMb = "usenet.shared-streams.small-range-max-mb";
    public const string UsenetBandwidthLimitMbps = "usenet.bandwidth-limit-mbps";

    // webdav
    public const string WebdavEnforceReadonly = "webdav.enforce-readonly";
    public const string WebdavPass = "webdav.pass";
    public const string WebdavPreviewPar2Files = "webdav.preview-par2-files";
    public const string WebdavShowHiddenFiles = "webdav.show-hidden-files";
    public const string WebdavUser = "webdav.user";
    public const string WebdavWindowsSafePaths = "webdav.windows-safe-paths";

    // media / repair / arr
    public const string MediaLibraryDir = "media.library-dir";
    public const string RepairEnable = "repair.enable";
    public const string RepairHealthcheckConcurrency = "repair.healthcheck-concurrency";
    public const string RepairHealthcheckWorkers = "repair.healthcheck-workers";
    public const string RepairHealthcheckDepth = "repair.healthcheck-depth";
    public const string RepairHealthcheckAging = "repair.healthcheck-aging";
    public const string RepairAutoRemoveAfterFailures = "repair.auto-remove-after-failures";
    public const string RepairAutoRemoveUnlinkedOnly = "repair.auto-remove-unlinked-only";
    public const string RepairPar2Enabled = "repair.par2-enabled";
    public const string RepairPar2PreferredOverArr = "repair.par2-preferred-over-arr";
    public const string RepairPar2MaxMissingSlices = "repair.par2-max-missing-slices";
    public const string RepairPar2MaxReleaseGb = "repair.par2-max-release-gb";
    public const string RepairPar2MaxMemoryMb = "repair.par2-max-memory-mb";
    public const string RepairPar2MaxPatchGb = "repair.par2-max-patch-gb";
    public const string RepairPar2FetchConcurrency = "repair.par2-fetch-concurrency";
    public const string RepairPar2FailureCooldownHours = "repair.par2-failure-cooldown-hours";
    public const string RepairDegradedToleranceEnabled = "repair.degraded-tolerance-enabled";
    public const string RepairDegradedMaxConsecutiveMissing = "repair.degraded-max-consecutive-missing";
    public const string RepairDegradedMaxTotalMissing = "repair.degraded-max-total-missing";
    public const string RepairDegradedMaxMissingBytePercent = "repair.degraded-max-missing-byte-percent";
    public const string RepairCorruptionTrackingEnabled = "repair.corruption-tracking-enabled";
    public const string ArrInstances = "arr.instances";
    public const string ArrHealthEnabled = "arr.health-enabled";

    // rclone
    public const string RcloneHost = "rclone.host";
    public const string RcloneMountDir = "rclone.mount-dir";
    public const string RclonePass = "rclone.pass";
    public const string RcloneRcEnabled = "rclone.rc-enabled";
    public const string RcloneUser = "rclone.user";

    // general / db / maintenance
    public const string GeneralBaseUrl = "general.base-url";
    public const string DbIsStartupVacuumEnabled = "db.is-startup-vacuum-enabled";
    public const string MaintenanceRemoveOrphanedScheduleEnabled = "maintenance.remove-orphaned-schedule-enabled";
    public const string MaintenanceRemoveOrphanedScheduleTime = "maintenance.remove-orphaned-schedule-time";

    // database backups
    public const string BackupScheduleEnabled = "backup.schedule-enabled";
    public const string BackupScheduleTime = "backup.schedule-time";
    public const string BackupRetentionCount = "backup.retention-count";

    // play
    public const string PlayCandidateNegativeCacheMinutes = "play.candidate-negative-cache-minutes";
    public const string PlayExcludePatterns = "play.exclude-patterns";
    public const string PlayHedgeDelaySeconds = "play.hedge-delay-seconds";
    public const string PlayMaxAttempts = "play.max-attempts";
    public const string PlayMaxCandidates = "play.max-candidates";
    public const string PlayPreferSubtitles = "play.prefer-subtitles";
    public const string PlayResolutionCacheTtlHours = "play.resolution-cache-ttl-hours";
    public const string PlayTotalBudgetSeconds = "play.total-budget-seconds";
    public const string PlayVerifyMode = "play.verify-mode";
    public const string PlayVerifySampleCount = "play.verify-sample-count";
    public const string PlayWatchdogEnabled = "play.watchdog-enabled";

    // grab
    public const string GrabStallFailoverCeilingSeconds = "grab.stall-failover-ceiling-seconds";
    public const string GrabStallFailoverEnabled = "grab.stall-failover-enabled";
    public const string GrabStallFailoverWindowSeconds = "grab.stall-failover-window-seconds";

    // variants
    public const string VariantsEvictionActiveGraceSeconds = "variants.eviction-active-grace-seconds";
    public const string VariantsEvictionStrategy = "variants.eviction-strategy";
    public const string VariantsFallbackOnFailure = "variants.fallback-on-failure";
    public const string VariantsMaxPerGroup = "variants.max-per-group";
    public const string VariantsMode = "variants.mode";
    public const string VariantsReplayStrategy = "variants.replay-strategy";
    public const string VariantsSegmentDonorsEnabled = "variants.segment-donors-enabled";
    public const string VariantsSegmentDonorsMaxPerSegment = "variants.segment-donors-max-per-segment";
    public const string VariantsSegmentDonorsMaxSiblings = "variants.segment-donors-max-siblings";
    public const string VariantsTolerancePct = "variants.tolerance-pct";

    // preflight
    public const string PreflightIndexerMaxWaitSeconds = "preflight.indexer-max-wait-seconds";
    public const string PreflightMaxAttempts = "preflight.max-attempts";
    public const string PreflightMode = "preflight.mode";
    public const string PreflightTtlSeconds = "preflight.ttl-seconds";

    // watchtower
    public const string WatchtowerActiveSetCap = "watchtower.active-set-cap";
    public const string WatchtowerAutoThroughput = "watchtower.auto-throughput";
    public const string WatchtowerDailyResolveBudget = "watchtower.daily-resolve-budget";
    public const string WatchtowerEnabled = "watchtower.enabled";
    public const string WatchtowerGrabCapPerResolve = "watchtower.grab-cap-per-resolve";
    public const string WatchtowerKeepfreshBaseSeconds = "watchtower.keepfresh-base-seconds";
    public const string WatchtowerKeepfreshMaxSeconds = "watchtower.keepfresh-max-seconds";
    public const string WatchtowerMinGrabs = "watchtower.min-grabs";
    public const string WatchtowerProfileToken = "watchtower.profile-token";
    public const string WatchtowerRanking = "watchtower.ranking";
    public const string WatchtowerResolveConcurrency = "watchtower.resolve-concurrency";
    public const string WatchtowerSeasonBundleFallback = "watchtower.season-bundle-fallback";
    public const string WatchtowerSeasonBundleFallbackMaxEpisodes = "watchtower.season-bundle-fallback-max-episodes";
    public const string WatchtowerSeasonBundleFallbackRecentCount = "watchtower.season-bundle-fallback-recent-count";
    public const string WatchtowerSeasonBundleFallbackScope = "watchtower.season-bundle-fallback-scope";
    public const string WatchtowerSeasonBundles = "watchtower.season-bundles";
    public const string WatchtowerSeriesCapKeep = "watchtower.series-cap-keep";
    public const string WatchtowerSeriesMaxEpisodes = "watchtower.series-max-episodes";
    public const string WatchtowerSeriesRecentCount = "watchtower.series-recent-count";
    public const string WatchtowerSeriesScope = "watchtower.series-scope";
    public const string WatchtowerShortlistDepth = "watchtower.shortlist-depth";
    public const string WatchtowerSizeCeilingBytes = "watchtower.size-ceiling-bytes";
    public const string WatchtowerSizeFloorBytes = "watchtower.size-floor-bytes";
    public const string WatchtowerSyncIntervalSeconds = "watchtower.sync-interval-seconds";
    public const string WatchtowerUnavailableRetrySeconds = "watchtower.unavailable-retry-seconds";
    public const string WatchtowerVerboseLogging = "watchtower.verbose-logging";
    public const string WatchtowerVerifySampleCount = "watchtower.verify-sample-count";
    public const string WatchtowerVerifyTimeoutSeconds = "watchtower.verify-timeout-seconds";

    // warden
    public const string WardenBackboneScope = "warden.backbone-scope";
    public const string WardenHideDead = "warden.hide-dead";
    public const string WardenMaxSourceEntries = "warden.max-source-entries";
    public const string WardenQuorum = "warden.quorum";

    // search
    /// <summary>Prefix shared by all search-exclude keys (used with <c>StartsWith</c>).</summary>
    public const string SearchExcludePrefix = "search.exclude";
    public const string SearchExcludePatterns = "search.exclude-patterns";
    public const string SearchExcludeSyncCache = "search.exclude-sync-cache";
    public const string SearchExcludeSyncRefreshMinutes = "search.exclude-sync-refresh-minutes";
    public const string SearchExcludeSyncUrls = "search.exclude-sync-urls";

    // indexers / profiles
    public const string IndexersInstances = "indexers.instances";
    public const string ProfilesInstances = "profiles.instances";

    // prowlarr
    public const string ProwlarrUrl = "prowlarr.url";
    public const string ProwlarrApiKey = "prowlarr.api-key";
    public const string ProwlarrSyncEnabled = "prowlarr.sync-enabled";
    public const string ProwlarrSyncIntervalMinutes = "prowlarr.sync-interval-minutes";
    public const string ProwlarrSyncStatus = "prowlarr.sync-status";

    // database
    public const string DatabaseHealthcheckRetentionDays = "database.healthcheck-retention-days";
    public const string DatabaseHistoryRetentionDays = "database.history-retention-days";
    public const string MetricsFetchRetentionHours = "metrics.fetch-retention-hours";
}
