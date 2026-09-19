
using System.Text.Json;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Tests.Config;

public class MediaServerConfigTests
{
    [Fact]
    public void ValidateConfigItems_AcceptsValidMultiInstanceConfig()
    {
        var config = new MediaServerConfig
        {
            Instances =
            [
                new MediaServerInstance
                {
                    Id = Guid.NewGuid(),
                    Type = MediaServerType.Plex,
                    Name = "Home Plex",
                    BaseUrl = "https://plex.example.test",
                    Token = "secret",
                    PathMappings =
                    [
                        new MediaServerPathMapping
                        {
                            MediaServerPrefix = "/movies",
                            InfiniDyskPrefix = "/library/movies",
                        },
                    ],
                },
            ],
        };

        ConfigManager.ValidateConfigItems(
        [
            new ConfigItem
            {
                ConfigName = ConfigKeys.MediaServersInstances,
                ConfigValue = JsonSerializer.Serialize(config),
            },
        ], rejectUnknownJsonProperties: true);
    }

    [Fact]
    public void ValidateConfigItems_RejectsDuplicateInstanceIds()
    {
        var id = Guid.NewGuid();
        var json = $$"""
            {"Instances":[
              {"Id":"{{id}}","Type":"Plex","Name":"A","BaseUrl":"http://a.test","Token":"a","Enabled":true,"PathMappings":[]},
              {"Id":"{{id}}","Type":"Jellyfin","Name":"B","BaseUrl":"http://b.test","Token":"b","Enabled":true,"PathMappings":[]}
            ]}
            """;

        var error = Assert.Throws<ArgumentException>(() => ConfigManager.ValidateConfigItems(
        [
            new ConfigItem { ConfigName = ConfigKeys.MediaServersInstances, ConfigValue = json },
        ], rejectUnknownJsonProperties: true));

        Assert.Contains("duplicate media-server id", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ConfigSecretMasker_MasksAndRestoresMediaServerTokens()
    {
        var masker = new ConfigSecretMasker("signing-key");
        var existing = """{"Instances":[{"Id":"11111111-1111-1111-1111-111111111111","Type":"Plex","Name":"Plex","BaseUrl":"http://plex.test","Token":"top-secret","Enabled":true,"PathMappings":[]}]}""";

        var masked = masker.MaskForResponse(ConfigKeys.MediaServersInstances, existing);
        Assert.DoesNotContain("top-secret", masked, StringComparison.Ordinal);
        Assert.Contains(ConfigSecretMasker.MaskPrefix, masked, StringComparison.Ordinal);

        var restored = masker.ResolveForUpdate(ConfigKeys.MediaServersInstances, masked, existing);
        using var document = JsonDocument.Parse(restored);
        var token = document.RootElement.GetProperty("Instances")[0].GetProperty("Token").GetString();
        Assert.Equal("top-secret", token);
    }
}
