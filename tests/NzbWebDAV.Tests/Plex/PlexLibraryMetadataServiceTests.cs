using Microsoft.Extensions.Logging.Abstractions;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services.Plex;
using NzbWebDAV.Tests.Database;

namespace NzbWebDAV.Tests.Plex;

[Collection(nameof(ConfigPathCollection))]
public sealed class PlexLibraryMetadataServiceTests
{
    [Fact]
    public async Task FailedRefresh_KeepsCompletePersistedSnapshot()
    {
        var previous = Environment.GetEnvironmentVariable("CONFIG_PATH");
        var directory = Path.Combine(Path.GetTempPath(), $"plex-metadata-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        Environment.SetEnvironmentVariable("CONFIG_PATH", directory);
        try
        {
            var config = new ConfigManager();
            config.UpdateValues([new ConfigItem
            {
                ConfigName = PlexSettings.ServersKey,
                ConfigValue = """[{"Id":"machine","Name":"Plex","Url":"http://localhost:32400","Token":"secret","Enabled":true}]""",
            }]);
            var fail = false;
            using var handler = new FakePlexHandler(request =>
            {
                if (fail) throw new HttpRequestException("offline");
                return request.RequestUri!.AbsolutePath == "/library/sections"
                    ? PlexApiClientTests.Xml("""<MediaContainer totalSize="1"><Directory key="1" title="TV" type="show"/></MediaContainer>""")
                    : PlexApiClientTests.Xml("""<MediaContainer totalSize="1"><Video title="Pilot" grandparentTitle="A Show" parentIndex="1" index="1" ratingKey="42"><Media><Part file="/plex/A.Show.S01E01.mkv"/></Media></Video></MediaContainer>""");
            });
            var api = new PlexApiClient(new HttpClient(handler), "installation");
            using var service = new PlexLibraryMetadataService(config, api,
                NullLogger<PlexLibraryMetadataService>.Instance);
            Assert.False(service.Status.Ready);
            Assert.True((await service.SyncAsync()).Ready);
            Assert.Equal("episode", service.Match("TV-4K/A Show/A.Show.S01E01.mkv")?.MediaType);
            config.UpdateValues([new ConfigItem { ConfigName = ConfigKeys.MediaLibraryPlexServerIds, ConfigValue = "[]" }]);
            Assert.Null(service.Match("A.Show.S01E01.mkv"));
            config.UpdateValues([new ConfigItem { ConfigName = ConfigKeys.MediaLibraryPlexServerIds, ConfigValue = "[\"machine\"]" }]);
            Assert.Equal("episode", service.Match("A.Show.S01E01.mkv")?.MediaType);
            config.UpdateValues([new ConfigItem { ConfigName = ConfigKeys.MediaLibraryEnabled, ConfigValue = "false" }]);
            Assert.Null(service.Match("A.Show.S01E01.mkv"));
            config.UpdateValues([new ConfigItem { ConfigName = ConfigKeys.MediaLibraryEnabled, ConfigValue = "true" }]);
            fail = true;
            var failed = await service.SyncAsync();
            Assert.True(failed.Ready);
            Assert.NotNull(failed.Warning);
            Assert.Equal("A Show", service.Match("A.Show.S01E01.mkv")?.ShowName);

            using var reloaded = new PlexLibraryMetadataService(config, api,
                NullLogger<PlexLibraryMetadataService>.Instance);
            Assert.True(reloaded.Status.Ready);
            Assert.Equal("A Show", reloaded.Match("A.Show.S01E01.mkv")?.ShowName);
            Assert.True(reloaded.RequestSync().Syncing);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CONFIG_PATH", previous);
            Directory.Delete(directory, recursive: true);
        }
    }
}
