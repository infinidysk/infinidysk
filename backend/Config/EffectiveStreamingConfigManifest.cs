using System.Text.Json;
using System.Text.Json.Serialization;
using NzbWebDAV.Models;

namespace NzbWebDAV.Config;

internal enum EffectiveConfigSource
{
    Default,
    Sqlite,
    Environment,
}

/// <summary>
/// Safe-by-construction effective streaming settings for support packs and public
/// benchmark extraction. Built only from typed <see cref="ConfigManager"/> getters.
/// </summary>
internal static class EffectiveStreamingConfigManifest
{
    internal const int SchemaVersion = 1;

    internal static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    public static EffectiveStreamingConfigDocument Create(ConfigManager configManager)
    {
        ArgumentNullException.ThrowIfNull(configManager);
        var providers = configManager.GetUsenetProviderConfig().Providers;
        var providerEntries = new List<EffectiveProviderSettings>(providers.Count);
        for (var i = 0; i < providers.Count; i++)
        {
            var provider = providers[i];
            providerEntries.Add(new EffectiveProviderSettings(
                Alias: $"provider{i + 1}",
                Type: ProviderTypeName(provider.Type),
                MaxConnections: provider.MaxConnections,
                TlsEnabled: provider.UseSsl,
                TlsVerification: !provider.SkipTlsVerification,
                WarmConnectionFloor: configManager.GetWarmConnectionsFloor(provider.MaxConnections)));
        }

        return new EffectiveStreamingConfigDocument(
            SchemaVersion,
            new EffectiveStreamingSettings(
                configManager.GetArticleBufferSize(),
                configManager.IsPipelinedBodyRequestsEnabled(),
                configManager.GetStreamingBodyBatchWidth(),
                configManager.IsFiniteRangeSchedulerEnabled(),
                configManager.IsContainerAwareFillEnabled(),
                configManager.GetUsenetBandwidthLimitBytesPerSecond(),
                configManager.GetStreamingPriority().HighPriorityOdds,
                configManager.GetStreamingSegmentRetries(),
                (int)configManager.GetStreamingReadTimeout().TotalSeconds,
                (int)configManager.GetStreamingWriteTimeout().TotalSeconds,
                (int)configManager.GetStreamingSegmentTimeout().TotalSeconds),
            new EffectiveConnectionSettings(
                configManager.GetMaxDownloadConnections(),
                configManager.IsMaxDownloadConnectionsPerStream(),
                configManager.GetMaxDownloadConnectionsPerStreamCount(),
                configManager.IsWarmConnectionsEnabled(),
                configManager.IsReadStartWarmupEnabled(),
                providers.Count(provider => provider.Type == ProviderType.Pooled),
                configManager.GetUsenetProviderConfig().TotalPooledConnections),
            new EffectiveMemorySettings(
                configManager.GetInFlightArticleBudgetBytes(),
                configManager.IsSharedStreamsEnabled(),
                configManager.GetSharedStreamsRingBytes(),
                configManager.GetSharedStreamsMaxEntries(),
                configManager.GetSharedStreamsMaxEntriesPerFile(),
                configManager.GetSharedStreamsGraceSeconds(),
                configManager.GetSharedStreamsSmallRangeMaxBytes()),
            new EffectiveSegmentCacheSettings(
                configManager.IsSegmentCacheEnabled(),
                configManager.GetSegmentCacheMaxBytes(),
                configManager.GetSegmentCacheWriteBehindBytes()),
            new EffectiveRepairSettings(
                configManager.IsRepairJobEnabled(),
                configManager.IsPar2RepairEnabled(),
                configManager.IsDegradedToleranceEnabled(),
                configManager.IsCorruptionTrackingEnabled()),
            providerEntries,
            new EffectiveStreamingSources(
                new EffectiveStreamingSettingSources(
                    Source(configManager, ConfigKeys.UsenetArticleBufferSize),
                    Source(configManager, ConfigKeys.UsenetPipelinedBodyRequests),
                    Source(configManager, ConfigKeys.UsenetStreamingBodyBatchWidth),
                    Source(configManager, ConfigKeys.UsenetFiniteRangeSchedulerEnabled),
                    Source(configManager, ConfigKeys.UsenetContainerAwareFill),
                    Source(configManager, ConfigKeys.UsenetBandwidthLimitMbps),
                    Source(configManager, ConfigKeys.UsenetStreamingPriority),
                    Source(configManager, ConfigKeys.UsenetStreamingSegmentRetries),
                    Source(configManager, ConfigKeys.UsenetStreamingReadTimeoutSeconds),
                    Source(configManager, ConfigKeys.UsenetStreamingWriteTimeoutSeconds),
                    Source(configManager, ConfigKeys.UsenetStreamingSegmentTimeoutSeconds)),
                new EffectiveConnectionSettingSources(
                    Source(configManager, ConfigKeys.UsenetMaxDownloadConnections),
                    Source(configManager, ConfigKeys.UsenetMaxDownloadConnectionsPerStream),
                    Source(configManager, ConfigKeys.UsenetWarmConnectionsEnabled),
                    Source(configManager, ConfigKeys.UsenetReadStartWarmupEnabled)),
                new EffectiveMemorySettingSources(
                    Source(configManager, ConfigKeys.UsenetInFlightArticleBudgetMb),
                    Source(configManager, ConfigKeys.UsenetSharedStreamsEnabled),
                    Source(configManager, ConfigKeys.UsenetSharedStreamsRingMb),
                    Source(configManager, ConfigKeys.UsenetSharedStreamsMaxEntries),
                    Source(configManager, ConfigKeys.UsenetSharedStreamsMaxEntriesPerFile),
                    Source(configManager, ConfigKeys.UsenetSharedStreamsGraceSeconds),
                    Source(configManager, ConfigKeys.UsenetSharedStreamsSmallRangeMaxMb)),
                new EffectiveSegmentCacheSettingSources(
                    Source(configManager, ConfigKeys.UsenetSegmentCacheEnabled),
                    Source(configManager, ConfigKeys.UsenetSegmentCacheMaxGb),
                    Source(configManager, ConfigKeys.UsenetSegmentCacheWriteBehindMb)),
                new EffectiveRepairSettingSources(
                    Source(configManager, ConfigKeys.RepairEnable),
                    Source(configManager, ConfigKeys.RepairPar2Enabled),
                    Source(configManager, ConfigKeys.RepairDegradedToleranceEnabled),
                    Source(configManager, ConfigKeys.RepairCorruptionTrackingEnabled)),
                Source(configManager, ConfigKeys.UsenetProviders)));
    }

    private static EffectiveConfigSource Source(ConfigManager configManager, string key) =>
        configManager.GetEffectiveSource(key);

    private static string ProviderTypeName(ProviderType type) => type switch
    {
        ProviderType.Pooled => "pooled",
        ProviderType.BackupAndStats => "backupandstats",
        ProviderType.BackupOnly => "backuponly",
        _ => "disabled",
    };
}

internal sealed record EffectiveStreamingConfigDocument(
    int SchemaVersion,
    EffectiveStreamingSettings Streaming,
    EffectiveConnectionSettings Connections,
    EffectiveMemorySettings Memory,
    EffectiveSegmentCacheSettings SegmentCache,
    EffectiveRepairSettings Repair,
    IReadOnlyList<EffectiveProviderSettings> Providers,
    EffectiveStreamingSources Source);

internal sealed record EffectiveStreamingSettings(
    int ArticleBufferSize,
    bool PipelinedBodyRequests,
    int BodyBatchWidth,
    bool FiniteRangeSchedulerEnabled,
    bool ContainerAwareFill,
    long BandwidthLimitBytesPerSecond,
    int StreamingPriority,
    int SegmentRetryCount,
    int ReadTimeoutSeconds,
    int WriteTimeoutSeconds,
    int SegmentTimeoutSeconds);

internal sealed record EffectiveConnectionSettings(
    int EffectiveTotalDownloadLimit,
    bool PerStreamModeEnabled,
    int EffectivePerStreamCount,
    bool WarmConnectionsEnabled,
    bool ReadStartWarmupEnabled,
    int PooledProviderCount,
    int TotalPooledConnectionCount);

internal sealed record EffectiveMemorySettings(
    long InFlightArticleBudgetBytes,
    bool SharedStreamsEnabled,
    long SharedStreamRingBytes,
    int SharedStreamMaxEntries,
    int MaxEntriesPerFile,
    int GraceSeconds,
    long SmallRangeMaximumBytes);

internal sealed record EffectiveSegmentCacheSettings(
    bool Enabled,
    long MaxBytes,
    long WriteBehindBytes);

internal sealed record EffectiveRepairSettings(
    bool BackgroundEnabled,
    bool Par2Enabled,
    bool DegradedToleranceEnabled,
    bool CorruptionTrackingEnabled);

internal sealed record EffectiveProviderSettings(
    string Alias,
    string Type,
    int MaxConnections,
    bool TlsEnabled,
    bool TlsVerification,
    int WarmConnectionFloor);

internal sealed record EffectiveStreamingSources(
    EffectiveStreamingSettingSources Streaming,
    EffectiveConnectionSettingSources Connections,
    EffectiveMemorySettingSources Memory,
    EffectiveSegmentCacheSettingSources SegmentCache,
    EffectiveRepairSettingSources Repair,
    EffectiveConfigSource Providers);

internal sealed record EffectiveStreamingSettingSources(
    EffectiveConfigSource ArticleBufferSize,
    EffectiveConfigSource PipelinedBodyRequests,
    EffectiveConfigSource BodyBatchWidth,
    EffectiveConfigSource FiniteRangeSchedulerEnabled,
    EffectiveConfigSource ContainerAwareFill,
    EffectiveConfigSource BandwidthLimitBytesPerSecond,
    EffectiveConfigSource StreamingPriority,
    EffectiveConfigSource SegmentRetryCount,
    EffectiveConfigSource ReadTimeoutSeconds,
    EffectiveConfigSource WriteTimeoutSeconds,
    EffectiveConfigSource SegmentTimeoutSeconds);

internal sealed record EffectiveConnectionSettingSources(
    EffectiveConfigSource EffectiveTotalDownloadLimit,
    EffectiveConfigSource PerStreamModeEnabled,
    EffectiveConfigSource WarmConnectionsEnabled,
    EffectiveConfigSource ReadStartWarmupEnabled);

internal sealed record EffectiveMemorySettingSources(
    EffectiveConfigSource InFlightArticleBudgetBytes,
    EffectiveConfigSource SharedStreamsEnabled,
    EffectiveConfigSource SharedStreamRingBytes,
    EffectiveConfigSource SharedStreamMaxEntries,
    EffectiveConfigSource MaxEntriesPerFile,
    EffectiveConfigSource GraceSeconds,
    EffectiveConfigSource SmallRangeMaximumBytes);

internal sealed record EffectiveSegmentCacheSettingSources(
    EffectiveConfigSource Enabled,
    EffectiveConfigSource MaxBytes,
    EffectiveConfigSource WriteBehindBytes);

internal sealed record EffectiveRepairSettingSources(
    EffectiveConfigSource BackgroundEnabled,
    EffectiveConfigSource Par2Enabled,
    EffectiveConfigSource DegradedToleranceEnabled,
    EffectiveConfigSource CorruptionTrackingEnabled);
