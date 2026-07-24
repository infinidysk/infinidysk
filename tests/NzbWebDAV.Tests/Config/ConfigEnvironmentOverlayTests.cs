using System.Collections;
using System.Text.Json;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Models;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Tests.Config;

public class ConfigEnvironmentOverlayTests
{
    [Fact]
    public void LoadFromEnvironment_WithNoPrefixedVars_ReturnsEmpty()
    {
        var overlay = ConfigEnvironmentOverlay.LoadFromEnvironment(new Hashtable
        {
            ["CONFIG_PATH"] = "/tmp",
            ["WEBDAV_USER"] = "legacy",
        });

        Assert.True(overlay.IsEmpty);
        Assert.Equal(0, overlay.Count);
    }

    [Fact]
    public void LoadFromEnvironment_TreatsEmptyValuesAsUnset()
    {
        var overlay = ConfigEnvironmentOverlay.LoadFromEnvironment(new Hashtable
        {
            ["NZBDAV_CONFIG__WEBDAV__USER"] = "",
            ["NZBDAV_CONFIG__API__CATEGORIES"] = "tv,movies",
        });

        Assert.False(overlay.IsManaged(ConfigKeys.WebdavUser));
        Assert.True(overlay.TryGetValue(ConfigKeys.ApiCategories, out var categories));
        Assert.Equal("tv,movies", categories);
    }

    [Fact]
    public void LoadFromEnvironment_RejectsUnknownPrefixedVariable()
    {
        var ex = Assert.Throws<ConfigEnvironmentException>(() =>
            ConfigEnvironmentOverlay.LoadFromEnvironment(new Hashtable
            {
                ["NZBDAV_CONFIG__NOT__A__REAL__KEY"] = "x",
            }));

        Assert.Contains("NZBDAV_CONFIG__NOT__A__REAL__KEY", ex.Message);
        Assert.DoesNotContain("x", ex.Message);
    }

    [Fact]
    public void LoadFromEnvironment_RejectsInvalidValuesWithoutLeakingSecrets()
    {
        var secret = "super-secret-password-value";
        var ex = Assert.Throws<ConfigEnvironmentException>(() =>
            ConfigEnvironmentOverlay.LoadFromEnvironment(new Hashtable
            {
                ["NZBDAV_CONFIG__REPAIR__HEALTHCHECK_CONCURRENCY"] = secret,
            }));

        Assert.Equal(
            "Invalid configuration environment variable `NZBDAV_CONFIG__REPAIR__HEALTHCHECK_CONCURRENCY`.",
            ex.Message);
        Assert.DoesNotContain(secret, ex.Message);
    }

    [Fact]
    public void LoadFromEnvironment_HashesWebdavPassword()
    {
        var plaintext = "plain-webdav-pass";
        var overlay = ConfigEnvironmentOverlay.LoadFromEnvironment(new Hashtable
        {
            ["NZBDAV_CONFIG__WEBDAV__PASS"] = plaintext,
        });

        Assert.True(overlay.TryGetValue(ConfigKeys.WebdavPass, out var hashed));
        Assert.NotEqual(plaintext, hashed);
        Assert.True(PasswordUtil.Verify(hashed, plaintext));
        Assert.Equal(
            "NZBDAV_CONFIG__WEBDAV__PASS",
            overlay.GetEnvironmentVariableName(ConfigKeys.WebdavPass));
    }

    [Fact]
    public void LoadFromEnvironment_PreservesExistingUsenetProviderIds()
    {
        var existingId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var existing = JsonSerializer.Serialize(new UsenetProviderConfig
        {
            Providers =
            [
                new UsenetProviderConfig.ConnectionDetails
                {
                    ProviderId = existingId,
                    Type = ProviderType.Pooled,
                    Host = "news.example",
                    Port = 563,
                    UseSsl = true,
                    User = "alice",
                    Pass = "stored",
                    MaxConnections = 10,
                },
            ],
        });

        var incoming = JsonSerializer.Serialize(new UsenetProviderConfig
        {
            Providers =
            [
                new UsenetProviderConfig.ConnectionDetails
                {
                    Type = ProviderType.Pooled,
                    Host = "news.example",
                    Port = 563,
                    UseSsl = true,
                    User = "alice",
                    Pass = "env-secret",
                    MaxConnections = 20,
                },
            ],
        });

        var overlay = ConfigEnvironmentOverlay.LoadFromEnvironment(
            new Hashtable { ["NZBDAV_CONFIG__USENET__PROVIDERS"] = incoming },
            existingUsenetProvidersJson: existing);

        Assert.True(overlay.TryGetValue(ConfigKeys.UsenetProviders, out var normalized));
        var parsed = JsonSerializer.Deserialize<UsenetProviderConfig>(normalized)!;
        Assert.Single(parsed.Providers);
        Assert.Equal(existingId, parsed.Providers[0].ProviderId);
        Assert.Equal(20, parsed.Providers[0].MaxConnections);
        Assert.Equal("env-secret", parsed.Providers[0].Pass);
    }

    [Fact]
    public void LoadFromEnvironment_AssignsProviderIdsWhenMissingEverywhere()
    {
        var incoming = JsonSerializer.Serialize(new UsenetProviderConfig
        {
            Providers =
            [
                new UsenetProviderConfig.ConnectionDetails
                {
                    Type = ProviderType.Pooled,
                    Host = "news.example",
                    Port = 563,
                    UseSsl = true,
                    User = "alice",
                    Pass = "env-secret",
                    MaxConnections = 5,
                },
            ],
        });

        var overlay = ConfigEnvironmentOverlay.LoadFromEnvironment(new Hashtable
        {
            ["NZBDAV_CONFIG__USENET__PROVIDERS"] = incoming,
        });

        Assert.True(overlay.TryGetValue(ConfigKeys.UsenetProviders, out var normalized));
        var parsed = JsonSerializer.Deserialize<UsenetProviderConfig>(normalized)!;
        Assert.NotEqual(Guid.Empty, parsed.Providers[0].ProviderId);
    }

    [Fact]
    public void LoadFromEnvironment_RejectsMiscasedUsenetProviderJson()
    {
        var secret = "env-secret-pass";
        var ex = Assert.Throws<ConfigEnvironmentException>(() =>
            ConfigEnvironmentOverlay.LoadFromEnvironment(new Hashtable
            {
                // camelCase "providers" used to deserialize to zero providers
                // and boot clean.
                ["NZBDAV_CONFIG__USENET__PROVIDERS"] =
                    "{\"providers\":[{\"Type\":1,\"Host\":\"news.example\",\"Port\":563," +
                    $"\"UseSsl\":true,\"User\":\"u\",\"Pass\":\"{secret}\",\"MaxConnections\":10}}]}}",
            }));

        Assert.Contains("NZBDAV_CONFIG__USENET__PROVIDERS", ex.Message);
        Assert.Contains("$.providers", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, ex.Message);
    }

    [Fact]
    public void LoadFromEnvironment_RejectsUnknownArrInstanceProperty()
    {
        var ex = Assert.Throws<ConfigEnvironmentException>(() =>
            ConfigEnvironmentOverlay.LoadFromEnvironment(new Hashtable
            {
                ["NZBDAV_CONFIG__ARR__INSTANCES"] =
                    "{\"radarrInstances\":[{\"host\":\"http://radarr:7878\",\"apiKey\":\"k\"}]}",
            }));

        Assert.Contains("NZBDAV_CONFIG__ARR__INSTANCES", ex.Message);
        Assert.Contains("$.radarrInstances", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadFromEnvironment_NamesNestedUnknownProperty()
    {
        var apiKey = "nested-secret-key";
        var ex = Assert.Throws<ConfigEnvironmentException>(() =>
            ConfigEnvironmentOverlay.LoadFromEnvironment(new Hashtable
            {
                // A stale property inside an instance, left behind by a rename or
                // by config copied from a newer image.
                ["NZBDAV_CONFIG__ARR__INSTANCES"] =
                    $"{{\"RadarrInstances\":[{{\"Host\":\"http://radarr:7878\",\"ApiKey\":\"{apiKey}\",\"Timeout\":30}}]}}",
            }));

        // The path locates the offending property inside the array element.
        Assert.Contains("$.RadarrInstances[0].Timeout", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(apiKey, ex.Message);
    }

    [Fact]
    public void LoadFromEnvironment_MalformedJsonDoesNotEchoValue()
    {
        var secret = "supersecretpassword";
        var ex = Assert.Throws<ConfigEnvironmentException>(() =>
            ConfigEnvironmentOverlay.LoadFromEnvironment(new Hashtable
            {
                ["NZBDAV_CONFIG__ARR__INSTANCES"] = $"{{\"RadarrInstances\": {secret}}}",
            }));

        // Malformed input keeps the bare message, since its parser detail can
        // quote the value.
        Assert.Equal(
            "Invalid configuration environment variable `NZBDAV_CONFIG__ARR__INSTANCES`.",
            ex.Message);
    }

    [Fact]
    public void LoadFromEnvironment_AcceptsPascalCaseArrInstances()
    {
        var arr = JsonSerializer.Serialize(new ArrConfig
        {
            RadarrInstances =
            [
                new ArrConfig.ConnectionDetails { Host = "http://radarr:7878", ApiKey = "k" },
            ],
        });

        var overlay = ConfigEnvironmentOverlay.LoadFromEnvironment(new Hashtable
        {
            ["NZBDAV_CONFIG__ARR__INSTANCES"] = arr,
        });

        Assert.True(overlay.TryGetValue(ConfigKeys.ArrInstances, out var stored));
        var parsed = JsonSerializer.Deserialize<ArrConfig>(stored)!;
        Assert.Equal("http://radarr:7878", Assert.Single(parsed.RadarrInstances).Host);
    }

    [Fact]
    public void LoadFromEnvironment_NamesMiscasedMemberInsideProvider()
    {
        var secret = "provider-pass";
        var ex = Assert.Throws<ConfigEnvironmentException>(() =>
            ConfigEnvironmentOverlay.LoadFromEnvironment(new Hashtable
            {
                // Container key correct, members camelCased. Every member is skipped,
                // so the required ones read as missing and only a case-insensitive
                // parse proves this is a naming problem rather than a malformed one.
                ["NZBDAV_CONFIG__USENET__PROVIDERS"] =
                    "{\"Providers\":[{\"type\":1,\"host\":\"news.example\",\"port\":563," +
                    $"\"useSsl\":true,\"user\":\"u\",\"pass\":\"{secret}\",\"maxConnections\":10}}]}}",
            }));

        Assert.Contains("$.Providers[0].type", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, ex.Message);
    }

    [Fact]
    public void LoadFromEnvironment_AcceptsSerializedProfilesInstances()
    {
        var profiles = JsonSerializer.Serialize(new ProfileConfig
        {
            Profiles = [new ProfileConfig.Profile { Token = "t", Name = "n" }],
        });

        var overlay = ConfigEnvironmentOverlay.LoadFromEnvironment(new Hashtable
        {
            ["NZBDAV_CONFIG__PROFILES__INSTANCES"] = profiles,
        });

        Assert.True(overlay.TryGetValue(ConfigKeys.ProfilesInstances, out var stored));
        Assert.Equal("n", Assert.Single(JsonSerializer.Deserialize<ProfileConfig>(stored)!.Profiles).Name);
    }

    [Fact]
    public void LoadFromEnvironment_AcceptsSerializedIndexersInstances()
    {
        var indexers = JsonSerializer.Serialize(new IndexerConfig
        {
            Indexers =
            [
                new IndexerConfig.ConnectionDetails
                {
                    Name = "example", Url = "https://indexer.example/api", ApiKey = "k",
                },
            ],
        });

        var overlay = ConfigEnvironmentOverlay.LoadFromEnvironment(new Hashtable
        {
            ["NZBDAV_CONFIG__INDEXERS__INSTANCES"] = indexers,
        });

        Assert.True(overlay.TryGetValue(ConfigKeys.IndexersInstances, out var stored));
        Assert.Equal("example", Assert.Single(JsonSerializer.Deserialize<IndexerConfig>(stored)!.Indexers).Name);
    }
}
