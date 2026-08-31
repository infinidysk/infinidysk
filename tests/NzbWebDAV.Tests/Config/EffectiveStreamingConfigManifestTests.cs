using System.Collections;
using System.Reflection;
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
    public void ReflectionAllowlist_RejectsUnknownDocumentProperties()
    {
        var names = CollectPropertyNames(typeof(EffectiveStreamingConfigDocument));
        foreach (var name in names)
        {
            Assert.True(
                char.IsUpper(name[0]) && name.All(char.IsLetterOrDigit),
                $"document property {name} is not a stable identifier");
        }

        Assert.DoesNotContain("Path", names);
        Assert.DoesNotContain("Host", names);
        Assert.DoesNotContain("User", names);
        Assert.DoesNotContain("Pass", names);
        Assert.DoesNotContain("Password", names);
        Assert.DoesNotContain("ProviderId", names);
        Assert.DoesNotContain("StorageGroup", names);
        Assert.DoesNotContain("ApiKey", names);
    }

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

    private static IEnumerable<string> CollectPropertyNames(Type type)
    {
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            yield return property.Name;
            var propertyType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            if (propertyType.IsGenericType
                && propertyType.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))
            {
                foreach (var nested in CollectPropertyNames(propertyType.GetGenericArguments()[0]))
                    yield return nested;
            }
            else if (propertyType.Namespace == typeof(EffectiveStreamingConfigDocument).Namespace
                     && propertyType.IsClass
                     && propertyType != typeof(string))
            {
                foreach (var nested in CollectPropertyNames(propertyType))
                    yield return nested;
            }
        }
    }
}
