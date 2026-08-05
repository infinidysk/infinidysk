using System.Text.Json;
using Microsoft.AspNetCore.Http;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Tests.Config;

[Collection(nameof(SecretResolverCollection))]
public class IndexerApiKeyResolverTests
{
    [Fact]
    public void Resolve_ReturnsPlaintextUnchanged()
    {
        using var _ = TempEnv("FRONTEND_BACKEND_API_KEY", "test-signing-key");
        var configManager = new ConfigManager();

        var resolved = IndexerApiKeyResolver.Resolve("typed-api-key", configManager);

        Assert.Equal("typed-api-key", resolved);
    }

    [Fact]
    public void Resolve_UnmasksStoredIndexerApiKey()
    {
        using var _ = TempEnv("FRONTEND_BACKEND_API_KEY", "test-signing-key");
        var stored = JsonSerializer.Serialize(new IndexerConfig
        {
            Indexers =
            [
                new IndexerConfig.ConnectionDetails
                {
                    Name = "Example",
                    Url = "https://indexer.example",
                    ApiKey = "stored-indexer-key",
                }
            ],
        });
        var configManager = new ConfigManager();
        configManager.UpdateValues(
        [
            new ConfigItem { ConfigName = ConfigKeys.IndexersInstances, ConfigValue = stored }
        ]);

        var masker = new ConfigSecretMasker("test-signing-key");
        var masked = masker.MaskForResponse(ConfigKeys.IndexersInstances, stored);
        using var document = JsonDocument.Parse(masked);
        var token = document.RootElement
            .GetProperty("Indexers")[0]
            .GetProperty("ApiKey")
            .GetString()!;

        var resolved = IndexerApiKeyResolver.Resolve(token, configManager);

        Assert.Equal("stored-indexer-key", resolved);
    }

    [Fact]
    public void Resolve_ThrowsForUnknownMaskToken()
    {
        using var _ = TempEnv("FRONTEND_BACKEND_API_KEY", "test-signing-key");
        var configManager = new ConfigManager();
        configManager.UpdateValues(
        [
            new ConfigItem
            {
                ConfigName = ConfigKeys.IndexersInstances,
                ConfigValue = JsonSerializer.Serialize(new IndexerConfig
                {
                    Indexers =
                    [
                        new IndexerConfig.ConnectionDetails
                        {
                            Name = "Example",
                            Url = "https://indexer.example",
                            ApiKey = "stored-secret",
                        }
                    ],
                })
            }
        ]);

        var forged = $"{ConfigSecretMasker.MaskPrefix}AAAAAAAAAAAAAAAAAAAAAA.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

        Assert.Throws<BadHttpRequestException>(() =>
            IndexerApiKeyResolver.Resolve(forged, configManager));
    }

    private static IDisposable TempEnv(string name, string value)
    {
        var previous = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
        return new RestoreEnv(name, previous);
    }

    private sealed class RestoreEnv(string name, string? previous) : IDisposable
    {
        public void Dispose() => Environment.SetEnvironmentVariable(name, previous);
    }
}
