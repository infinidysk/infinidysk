using System.Collections;
using System.Text.Json;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Models;

namespace NzbWebDAV.Tests.Config;

public sealed class EffectiveStreamingConfigManifestTests
{
    [Fact]
    public void Defaults_MatchCurrentTypedGetters()
    {
        var config = new ConfigManager();
        var document = EffectiveStreamingConfigManifest.Create(config);

        Assert.Equal(1, document.SchemaVersion);
        Assert.Equal(40, document.Streaming.ArticleBufferSize);
        Assert.True(document.Streaming.PipelinedBodyRequests);
        Assert.Equal(4, document.Streaming.BodyBatchWidth);
        Assert.True(document.Streaming.ContainerAwareFill);
        Assert.Equal(0, document.Streaming.BandwidthLimitBytesPerSecond);
        Assert.Equal(80, document.Streaming.StreamingPriority);
        Assert.Equal(3, document.Streaming.SegmentRetryCount);
        Assert.Equal(30, document.Streaming.ReadTimeoutSeconds);
        Assert.Equal(60, document.Streaming.WriteTimeoutSeconds);
        Assert.Equal(8, document.Streaming.SegmentTimeoutSeconds);
        Assert.Equal(config.GetMaxDownloadConnections(), document.Connections.EffectiveTotalDownloadLimit);
        Assert.False(document.Connections.PerStreamModeEnabled);
        Assert.True(document.Connections.WarmConnectionsEnabled);
        Assert.Equal(config.GetInFlightArticleBudgetBytes(), document.Memory.InFlightArticleBudgetBytes);
        Assert.True(document.Memory.SharedStreamsEnabled);
        Assert.True(document.SegmentCache.Enabled);
        Assert.Equal(10L * 1024 * 1024 * 1024, document.SegmentCache.MaxBytes);
        Assert.True(document.Repair.BackgroundEnabled);
        Assert.True(document.Repair.Par2Enabled);
        Assert.True(document.Repair.DegradedToleranceEnabled);
        Assert.True(document.Repair.CorruptionTrackingEnabled);
        Assert.Empty(document.Providers);
        Assert.Equal(EffectiveConfigSource.Default, document.Source.Streaming.ArticleBufferSize);
    }

    [Fact]
    public void SqliteAndEnvironmentOverrides_AreEffectiveValues()
    {
        var config = new ConfigManager();
        config.UpdateValues(
        [
            new ConfigItem { ConfigName = ConfigKeys.UsenetArticleBufferSize, ConfigValue = "12" },
            new ConfigItem { ConfigName = ConfigKeys.UsenetStreamingBodyBatchWidth, ConfigValue = "99" },
            new ConfigItem { ConfigName = ConfigKeys.UsenetSegmentCacheMaxGb, ConfigValue = "2" },
            new ConfigItem { ConfigName = ConfigKeys.RepairEnable, ConfigValue = "false" },
        ]);
        config.ApplyEnvironmentOverlay(ConfigEnvironmentOverlay.LoadFromEnvironment(new Hashtable
        {
            ["NZBDAV_CONFIG__USENET__PIPELINED_BODY_REQUESTS"] = "false",
            ["NZBDAV_CONFIG__USENET__ARTICLE_BUFFER_SIZE"] = "7",
        }));

        var document = EffectiveStreamingConfigManifest.Create(config);
        Assert.Equal(7, document.Streaming.ArticleBufferSize);
        Assert.False(document.Streaming.PipelinedBodyRequests);
        Assert.Equal(8, document.Streaming.BodyBatchWidth);
        Assert.Equal(2L * 1024 * 1024 * 1024, document.SegmentCache.MaxBytes);
        Assert.False(document.Repair.BackgroundEnabled);
        Assert.False(document.Repair.Par2Enabled);
        Assert.Equal(EffectiveConfigSource.Environment, document.Source.Streaming.ArticleBufferSize);
        Assert.Equal(EffectiveConfigSource.Environment, document.Source.Streaming.PipelinedBodyRequests);
        Assert.Equal(EffectiveConfigSource.Sqlite, document.Source.Streaming.BodyBatchWidth);
        Assert.Equal(EffectiveConfigSource.Sqlite, document.Source.Repair.BackgroundEnabled);
    }

    [Fact]
    public void Providers_AreAliasedWithoutSecretsOrPaths()
    {
        var config = new ConfigManager();
        config.UpdateValues(
        [
            new ConfigItem
            {
                ConfigName = ConfigKeys.UsenetProviders,
                ConfigValue = JsonSerializer.Serialize(new UsenetProviderConfig
                {
                    Providers =
                    [
                        new UsenetProviderConfig.ConnectionDetails
                        {
                            ProviderId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                            Type = ProviderType.Pooled,
                            Host = "news.secret.example",
                            Port = 563,
                            UseSsl = true,
                            SkipTlsVerification = false,
                            User = "sentinel-user",
                            Pass = "sentinel-pass",
                            MaxConnections = 20,
                            StorageGroup = "secret-group",
                        },
                    ],
                }),
            },
            new ConfigItem
            {
                ConfigName = ConfigKeys.UsenetSegmentCachePath,
                ConfigValue = "/secret/cache-path",
            },
            new ConfigItem { ConfigName = ConfigKeys.ApiKey, ConfigValue = "sentinel-api-key" },
            new ConfigItem { ConfigName = ConfigKeys.WebdavPass, ConfigValue = "sentinel-webdav-pass" },
            new ConfigItem { ConfigName = ConfigKeys.ApiStrmKey, ConfigValue = "sentinel-strm-key" },
        ]);

        var document = EffectiveStreamingConfigManifest.Create(config);
        var json = JsonSerializer.Serialize(document, EffectiveStreamingConfigManifest.SerializerOptions);

        Assert.Single(document.Providers);
        Assert.Equal("provider1", document.Providers[0].Alias);
        Assert.Equal("pooled", document.Providers[0].Type);
        Assert.Equal(20, document.Providers[0].MaxConnections);
        Assert.True(document.Providers[0].TlsEnabled);
        Assert.True(document.Providers[0].TlsVerification);
        Assert.DoesNotContain("news.secret.example", json);
        Assert.DoesNotContain("sentinel-user", json);
        Assert.DoesNotContain("sentinel-pass", json);
        Assert.DoesNotContain("11111111-1111-1111-1111-111111111111", json);
        Assert.DoesNotContain("secret-group", json);
        Assert.DoesNotContain("/secret/cache-path", json);
        Assert.DoesNotContain("sentinel-api-key", json);
        Assert.DoesNotContain("sentinel-webdav-pass", json);
        Assert.DoesNotContain("sentinel-strm-key", json);
        Assert.DoesNotContain("NZBDAV_CONFIG", json);
        Assert.DoesNotContain("usenet.segment-cache.path", json);
    }

    [Fact]
    public void SerializedOutput_IsCamelCaseSchemaVersion1_AndAllowlisted()
    {
        var config = new ConfigManager();
        var document = EffectiveStreamingConfigManifest.Create(config);
        var json = JsonSerializer.Serialize(document, EffectiveStreamingConfigManifest.SerializerOptions);
        using var parsed = JsonDocument.Parse(json);

        Assert.Equal(1, parsed.RootElement.GetProperty("schemaVersion").GetInt32());
        var offenders = new List<string>();
        CollectJsonNames(parsed.RootElement, "", offenders);
        Assert.True(offenders.Count == 0, string.Join(", ", offenders));
        Assert.DoesNotContain("path", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("providerId", json, StringComparison.Ordinal);
    }

    [Fact]
    public void SerializedPropertyPaths_MatchExplicitPublicAllowlist()
    {
        var config = new ConfigManager();
        config.UpdateValues(
        [
            new ConfigItem
            {
                ConfigName = ConfigKeys.UsenetProviders,
                ConfigValue = JsonSerializer.Serialize(new UsenetProviderConfig
                {
                    Providers =
                    [
                        new UsenetProviderConfig.ConnectionDetails
                        {
                            Type = ProviderType.Pooled,
                            Host = "news.secret.example",
                            Port = 563,
                            UseSsl = true,
                            User = "sentinel-user",
                            Pass = "sentinel-pass",
                            MaxConnections = 8,
                        },
                    ],
                }),
            },
        ]);

        var json = JsonSerializer.Serialize(
            EffectiveStreamingConfigManifest.Create(config),
            EffectiveStreamingConfigManifest.SerializerOptions);
        using var parsed = JsonDocument.Parse(json);
        var actual = CollectJsonPaths(parsed.RootElement);

        var unexpected = actual.Except(AllowedJsonPaths, StringComparer.Ordinal).Order().ToArray();
        var missing = AllowedJsonPaths.Except(actual, StringComparer.Ordinal).Order().ToArray();
        Assert.True(
            unexpected.Length == 0 && missing.Length == 0,
            "unexpected: " + string.Join(", ", unexpected) + "; missing: " + string.Join(", ", missing));
        Assert.DoesNotContain(
            actual,
            static path => ForbiddenJsonPathSegments.Contains(path.Split('.')[^1]));
    }

    private static readonly HashSet<string> AllowedJsonPaths =
    [
        "schemaVersion",
        "streaming",
        "streaming.articleBufferSize",
        "streaming.pipelinedBodyRequests",
        "streaming.bodyBatchWidth",
        "streaming.containerAwareFill",
        "streaming.bandwidthLimitBytesPerSecond",
        "streaming.streamingPriority",
        "streaming.segmentRetryCount",
        "streaming.readTimeoutSeconds",
        "streaming.writeTimeoutSeconds",
        "streaming.segmentTimeoutSeconds",
        "connections",
        "connections.effectiveTotalDownloadLimit",
        "connections.perStreamModeEnabled",
        "connections.effectivePerStreamCount",
        "connections.warmConnectionsEnabled",
        "connections.pooledProviderCount",
        "connections.totalPooledConnectionCount",
        "memory",
        "memory.inFlightArticleBudgetBytes",
        "memory.sharedStreamsEnabled",
        "memory.sharedStreamRingBytes",
        "memory.sharedStreamMaxEntries",
        "memory.maxEntriesPerFile",
        "memory.graceSeconds",
        "memory.smallRangeMaximumBytes",
        "segmentCache",
        "segmentCache.enabled",
        "segmentCache.maxBytes",
        "repair",
        "repair.backgroundEnabled",
        "repair.par2Enabled",
        "repair.degradedToleranceEnabled",
        "repair.corruptionTrackingEnabled",
        "providers",
        "providers.alias",
        "providers.type",
        "providers.maxConnections",
        "providers.tlsEnabled",
        "providers.tlsVerification",
        "providers.warmConnectionFloor",
        "source",
        "source.streaming",
        "source.streaming.articleBufferSize",
        "source.streaming.pipelinedBodyRequests",
        "source.streaming.bodyBatchWidth",
        "source.streaming.containerAwareFill",
        "source.streaming.bandwidthLimitBytesPerSecond",
        "source.streaming.streamingPriority",
        "source.streaming.segmentRetryCount",
        "source.streaming.readTimeoutSeconds",
        "source.streaming.writeTimeoutSeconds",
        "source.streaming.segmentTimeoutSeconds",
        "source.connections",
        "source.connections.effectiveTotalDownloadLimit",
        "source.connections.perStreamModeEnabled",
        "source.connections.warmConnectionsEnabled",
        "source.memory",
        "source.memory.inFlightArticleBudgetBytes",
        "source.memory.sharedStreamsEnabled",
        "source.memory.sharedStreamRingBytes",
        "source.memory.sharedStreamMaxEntries",
        "source.memory.maxEntriesPerFile",
        "source.memory.graceSeconds",
        "source.memory.smallRangeMaximumBytes",
        "source.segmentCache",
        "source.segmentCache.enabled",
        "source.segmentCache.maxBytes",
        "source.repair",
        "source.repair.backgroundEnabled",
        "source.repair.par2Enabled",
        "source.repair.degradedToleranceEnabled",
        "source.repair.corruptionTrackingEnabled",
        "source.providers",
    ];

    private static readonly HashSet<string> ForbiddenJsonPathSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "path",
        "host",
        "user",
        "pass",
        "password",
        "providerId",
        "storageGroup",
        "apiKey",
    };

    private static void CollectJsonNames(JsonElement element, string path, List<string> offenders)
    {
        if (element.ValueKind != JsonValueKind.Object) return;
        foreach (var property in element.EnumerateObject())
        {
            if (property.Name.Length == 0 || !char.IsLower(property.Name[0]) || !property.Name.All(char.IsLetterOrDigit))
                offenders.Add($"{path}/{property.Name}");
            if (property.Value.ValueKind == JsonValueKind.Object)
                CollectJsonNames(property.Value, $"{path}/{property.Name}", offenders);
            else if (property.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in property.Value.EnumerateArray())
                    CollectJsonNames(item, path, offenders);
            }
        }
    }

    private static HashSet<string> CollectJsonPaths(JsonElement element)
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        CollectJsonPaths(element, prefix: "", paths);
        return paths;
    }

    private static void CollectJsonPaths(JsonElement element, string prefix, HashSet<string> paths)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                var path = prefix.Length == 0 ? property.Name : $"{prefix}.{property.Name}";
                paths.Add(path);
                CollectJsonPaths(property.Value, path, paths);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                CollectJsonPaths(item, prefix, paths);
        }
    }
}
