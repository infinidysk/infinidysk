using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Services;
using NzbWebDAV.Services.Plex;
using NzbWebDAV.Tests.Database;

namespace NzbWebDAV.Tests.Plex;

[Collection(nameof(ConfigPathCollection))]
public sealed class PlexServerConfigServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MaskedServerSave_CannotRestoreDisconnectedOrRotatedCredentials(bool rotate)
    {
        var previous = Environment.GetEnvironmentVariable("FRONTEND_BACKEND_API_KEY");
        Environment.SetEnvironmentVariable("FRONTEND_BACKEND_API_KEY", "plex-test-key");
        try
        {
            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            await using var context = new DavDatabaseContext(new DbContextOptionsBuilder<DavDatabaseContext>().UseSqlite(connection).Options);
            await context.Database.EnsureCreatedAsync();
            var db = new DavDatabaseClient(context);
            var config = new ConfigManager();
            var initial = """[{"Id":"machine","Name":"Home","Url":"http://localhost:32400","Token":"old-secret"}]""";
            config.UpdateValues([new NzbWebDAV.Database.Models.ConfigItem { ConfigName = PlexSettings.ServersKey, ConfigValue = initial }]);
            var updates = new ConfigUpdateService(db, config);
            using var handler = new FakePlexHandler(_ => throw new InvalidOperationException("No HTTP expected."));
            var api = new PlexApiClient(new HttpClient(handler), "installation");
            var service = new PlexServerConfigService(config, updates, db, new PlexAccountService(api, new PlexTestClock()), api);
            var masked = Assert.Single(service.GetServers()).Token;
            using var pending = await updates.StageAsync([]);
            var save = service.SaveAsync("admin", [new PlexServerSaveRequest
                { Id = "machine", Name = "Edited", Url = "http://localhost:32400", Token = masked }]);
            Assert.False(save.IsCompleted);
            config.UpdateValues([new NzbWebDAV.Database.Models.ConfigItem { ConfigName = PlexSettings.ServersKey,
                ConfigValue = rotate ? initial.Replace("old-secret", "new-secret", StringComparison.Ordinal) : "[]" }]);
            pending.Dispose();
            await Assert.ThrowsAsync<InvalidOperationException>(() => save);
            var current = PlexSettings.ParseServers(config.GetEffectiveConfigValue(PlexSettings.ServersKey));
            if (rotate) Assert.Equal("new-secret", Assert.Single(current).Token);
            else Assert.Empty(current);
        }
        finally { Environment.SetEnvironmentVariable("FRONTEND_BACKEND_API_KEY", previous); }
    }

    [Fact]
    public async Task DisconnectedLinkedAccount_CannotBeReenabledByStaleServerSave()
    {
        var previous = Environment.GetEnvironmentVariable("FRONTEND_BACKEND_API_KEY");
        Environment.SetEnvironmentVariable("FRONTEND_BACKEND_API_KEY", "plex-test-key");
        try
        {
            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            await using var context = new DavDatabaseContext(new DbContextOptionsBuilder<DavDatabaseContext>().UseSqlite(connection).Options);
            await context.Database.EnsureCreatedAsync();
            var db = new DavDatabaseClient(context);
            var config = new ConfigManager();
            config.UpdateValues([new NzbWebDAV.Database.Models.ConfigItem { ConfigName = PlexSettings.ServersKey,
                ConfigValue = """[{"Id":"machine","Name":"Home","Url":"http://localhost:32400","Token":"secret","Enabled":false,"AccountId":"removed-account"}]""" }]);
            using var handler = new FakePlexHandler(_ => throw new InvalidOperationException("No HTTP expected."));
            var api = new PlexApiClient(new HttpClient(handler), "installation");
            var service = new PlexServerConfigService(config, new ConfigUpdateService(db, config), db,
                new PlexAccountService(api, new PlexTestClock()), api);
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveAsync("admin", [new PlexServerSaveRequest
                { Id = "machine", Name = "Home", Url = "http://localhost:32400", Token = "secret", Enabled = true }]));
            Assert.False(service.GetServer("machine").Enabled);
        }
        finally { Environment.SetEnvironmentVariable("FRONTEND_BACKEND_API_KEY", previous); }
    }

    [Fact]
    public async Task CancelledDiscoveryHandleWaitingForWriteLease_CannotSaveServerCredentials()
    {
        var previous = Environment.GetEnvironmentVariable("FRONTEND_BACKEND_API_KEY");
        Environment.SetEnvironmentVariable("FRONTEND_BACKEND_API_KEY", "plex-test-key");
        try
        {
            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            await using var context = new DavDatabaseContext(new DbContextOptionsBuilder<DavDatabaseContext>().UseSqlite(connection).Options);
            await context.Database.EnsureCreatedAsync();
            var db = new DavDatabaseClient(context);
            var config = new ConfigManager();
            var updates = new ConfigUpdateService(db, config);
            using var handler = new FakePlexHandler(request => request.RequestUri!.Host == "plex.tv"
                ? new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(request.RequestUri.AbsolutePath.Contains("resources", StringComparison.Ordinal)
                        ? """[{"clientIdentifier":"machine","name":"Home","provides":"server","accessToken":"server-secret","connections":[{"uri":"http://localhost:32400"}]}]"""
                        : request.Method == HttpMethod.Post ? """{"id":42,"code":"code"}""" : """{"id":42,"authToken":"account-secret"}""")
                }
                : PlexApiClientTests.Xml("""<MediaContainer machineIdentifier="machine"/>"""));
            var api = new PlexApiClient(new HttpClient(handler), "installation");
            var accounts = new PlexAccountService(api, new PlexTestClock());
            var service = new PlexServerConfigService(config, updates, db, accounts, api);
            var login = await accounts.StartAsync("admin");
            await accounts.PollAsync("admin", login.Handle);
            var discovered = Assert.Single(await accounts.DiscoverAsync("admin", login.Handle));
            using var blocking = await updates.StageAsync([]);
            var save = service.SaveAsync("admin", [new PlexServerSaveRequest { Handle = discovered.Handle, Name = "Home", Url = "http://localhost:32400" }]);
            Assert.False(save.IsCompleted);
            accounts.Cancel("admin", login.Handle);
            blocking.Dispose();
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => save);
            Assert.Empty(service.GetServers());
        }
        finally { Environment.SetEnvironmentVariable("FRONTEND_BACKEND_API_KEY", previous); }
    }

    [Fact]
    public async Task Save_TestsStableServerIdentityAndPersistsSecretWhileReturningMask()
    {
        var previous = Environment.GetEnvironmentVariable("FRONTEND_BACKEND_API_KEY");
        Environment.SetEnvironmentVariable("FRONTEND_BACKEND_API_KEY", "plex-test-key");
        try
        {
            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            await using var context = new DavDatabaseContext(new DbContextOptionsBuilder<DavDatabaseContext>().UseSqlite(connection).Options);
            await context.Database.EnsureCreatedAsync();
            var db = new DavDatabaseClient(context);
            var config = new ConfigManager();
            var offline = false;
            using var handler = new FakePlexHandler(_ => offline ? new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable)
                : PlexApiClientTests.Xml("""<MediaContainer machineIdentifier="machine" version="1"/>"""));
            var api = new PlexApiClient(new HttpClient(handler), "installation");
            var service = new PlexServerConfigService(config, new ConfigUpdateService(db, config), db,
                new PlexAccountService(api, new PlexTestClock()), api);
            var strictUpdates = new ConfigUpdateService(db, config);
            await Assert.ThrowsAsync<ArgumentException>(async () =>
            {
                using var batch = await strictUpdates.StageAsync([
                new NzbWebDAV.Database.Models.ConfigItem { ConfigName = PlexSettings.ServersKey,
                    ConfigValue = """[{"Id":"machine","Name":"Home","Url":"http://localhost:32400","Token":"secret","unexpected":true}]""" }
                ]);
            });
            var saved = await service.SaveAsync("owner", [new PlexServerSaveRequest
                { Id = "machine", Name = "Home", Url = "http://192.168.1.2:32400", Token = "secret" }]);
            Assert.StartsWith(ConfigSecretMasker.MaskPrefix, Assert.Single(saved).Token);
            Assert.Equal("secret", Assert.Single(PlexSettings.ParseServers(config.GetEffectiveConfigValue(PlexSettings.ServersKey))).Token);
            var stored = await context.ConfigItems.SingleAsync(item => item.ConfigName == PlexSettings.ServersKey);
            Assert.Contains("secret", stored.ConfigValue);
            await Assert.ThrowsAsync<PlexRequestException>(() => service.SaveAsync("owner", [new PlexServerSaveRequest
                { Id = "wrong-machine", Name = "Wrong", Url = "http://192.168.1.2:32400", Token = "other" }]));
            Assert.Equal("machine", Assert.Single(service.GetServers()).Id);
            // An offline unchanged server must remain disableable without another network test.
            var requestsBeforeDisable = handler.Requests.Count;
            offline = true;
            var disabled = await service.SaveAsync("owner", [new PlexServerSaveRequest
                { Id = "machine", Name = "Home", Url = "http://192.168.1.2:32400", Token = saved[0].Token, Enabled = false }]);
            Assert.False(Assert.Single(disabled).Enabled);
            Assert.Equal(requestsBeforeDisable, handler.Requests.Count);
        }
        finally { Environment.SetEnvironmentVariable("FRONTEND_BACKEND_API_KEY", previous); }
    }
}
