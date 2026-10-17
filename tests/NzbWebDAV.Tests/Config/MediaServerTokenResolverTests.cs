using System.Text.Json;
using Microsoft.AspNetCore.Http;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Tests.Config;

[Collection(nameof(SecretResolverCollection))]
public class MediaServerTokenResolverTests
{
    [Fact]
    public void Resolve_ReturnsPlaintextUnchanged()
    {
        using var _ = TempEnv("FRONTEND_BACKEND_API_KEY", "test-signing-key");
        var configManager = new ConfigManager();

        var resolved = MediaServerTokenResolver.Resolve("typed-token", configManager);

        Assert.Equal("typed-token", resolved);
    }

    [Fact]
    public void Resolve_UnmasksStoredMediaServerToken()
    {
        using var _ = TempEnv("FRONTEND_BACKEND_API_KEY", "test-signing-key");
        var stored = JsonSerializer.Serialize(new MediaServerConfig
        {
            Instances =
            [
                new MediaServerInstance
                {
                    Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                    Type = MediaServerType.Plex,
                    Name = "Home Plex",
                    BaseUrl = "http://plex:32400",
                    Token = "stored-plex-token",
                    Enabled = true,
                    PathMappings = [],
                },
            ],
        });
        var configManager = new ConfigManager();
        configManager.UpdateValues(
        [
            new ConfigItem { ConfigName = ConfigKeys.MediaServersInstances, ConfigValue = stored },
        ]);

        var masker = new ConfigSecretMasker("test-signing-key");
        var masked = masker.MaskForResponse(ConfigKeys.MediaServersInstances, stored);
        using var document = JsonDocument.Parse(masked);
        var token = document.RootElement
            .GetProperty("Instances")[0]
            .GetProperty("Token")
            .GetString()!;

        var resolved = MediaServerTokenResolver.Resolve(token, configManager);

        Assert.Equal("stored-plex-token", resolved);
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
                ConfigName = ConfigKeys.MediaServersInstances,
                ConfigValue = JsonSerializer.Serialize(new MediaServerConfig
                {
                    Instances =
                    [
                        new MediaServerInstance
                        {
                            Id = Guid.NewGuid(),
                            Type = MediaServerType.Jellyfin,
                            Name = "Jellyfin",
                            BaseUrl = "http://jellyfin:8096",
                            Token = "stored-secret",
                            Enabled = true,
                            PathMappings = [],
                        },
                    ],
                }),
            },
        ]);

        var forged =
            $"{ConfigSecretMasker.MaskPrefix}AAAAAAAAAAAAAAAAAAAAAA.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

        Assert.Throws<BadHttpRequestException>(() =>
            MediaServerTokenResolver.Resolve(forged, configManager));
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
