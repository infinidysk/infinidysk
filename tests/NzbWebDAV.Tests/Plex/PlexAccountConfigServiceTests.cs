using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Services;
using NzbWebDAV.Services.Plex;
using NzbWebDAV.Tests.Database;

namespace NzbWebDAV.Tests.Plex;

[Collection(nameof(ConfigPathCollection))]
public sealed class PlexAccountConfigServiceTests
{
    [Fact]
    public async Task Disconnect_InvalidatesEveryLoginForSameAccount()
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
            using var handler = new FakePlexHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent("""{"id":7,"username":"Owner"}""") });
            var api = new PlexApiClient(new HttpClient(handler), "installation");
            var accounts = new PlexAccountService(api, new PlexTestClock());
            var service = new PlexAccountConfigService(config, new ConfigUpdateService(db, config), db, accounts, api);
            var first = accounts.OpenConnected("admin-a", "first-token");
            var second = accounts.OpenConnected("admin-b", "second-token");
            await service.SaveLoginAsync("admin-a", first.Handle);
            var account = await service.SaveLoginAsync("admin-b", second.Handle);
            await service.DisconnectAsync(account.Id);
            Assert.Equal("cancelled", (await accounts.PollAsync("admin-a", first.Handle)).State);
            Assert.Equal("cancelled", (await accounts.PollAsync("admin-b", second.Handle)).State);
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveLoginAsync("admin-a", first.Handle));
            Assert.Empty(service.GetAccounts());
        }
        finally { Environment.SetEnvironmentVariable("FRONTEND_BACKEND_API_KEY", previous); }
    }

    [Fact]
    public async Task AccountSelection_WaitsForPendingDisconnect()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = new DavDatabaseContext(new DbContextOptionsBuilder<DavDatabaseContext>().UseSqlite(connection).Options);
        await context.Database.EnsureCreatedAsync();
        var db = new DavDatabaseClient(context);
        var config = new ConfigManager();
        config.UpdateValues([new NzbWebDAV.Database.Models.ConfigItem { ConfigName = PlexSettings.AccountsKey,
            ConfigValue = """[{"Id":"owner","Name":"Owner","Token":"secret"}]""" }]);
        var updates = new ConfigUpdateService(db, config);
        using var handler = new FakePlexHandler(_ => throw new InvalidOperationException("No HTTP expected."));
        var api = new PlexApiClient(new HttpClient(handler), "installation");
        var service = new PlexAccountConfigService(config, updates, db, new PlexAccountService(api, new PlexTestClock()), api);
        using var pendingDisconnect = await updates.StageAsync([]);
        var select = service.SelectAsync("admin", "owner");
        Assert.False(select.IsCompleted);
        config.UpdateValues([new NzbWebDAV.Database.Models.ConfigItem { ConfigName = PlexSettings.AccountsKey, ConfigValue = "[]" }]);
        pendingDisconnect.Dispose();
        await Assert.ThrowsAsync<ArgumentException>(() => select);
    }

    [Fact]
    public async Task ConnectedAccount_RestartsWithMaskedCredentials_AndNewOwnerBoundHandles()
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
            using var handler = new FakePlexHandler(request => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(request.RequestUri!.AbsolutePath == "/api/v2/user"
                    ? """{"id":7,"uuid":"owner-7","username":"Owner"}"""
                    : request.Method == HttpMethod.Post ? """{"id":42,"code":"pin-code"}""" : """{"id":42,"authToken":"account-secret"}""")
            });
            var api = new PlexApiClient(new HttpClient(handler), "installation");
            var accounts = new PlexAccountService(api, new PlexTestClock());
            var service = new PlexAccountConfigService(config, new ConfigUpdateService(db, config), db, accounts, api);
            var login = await accounts.StartAsync("admin-a");
            await accounts.PollAsync("admin-a", login.Handle);
            var saved = await service.SaveLoginAsync("admin-a", login.Handle);
            Assert.Equal("owner-7", saved.Id);
            Assert.StartsWith(ConfigSecretMasker.MaskPrefix, saved.Token);
            var restartedConfig = new ConfigManager();
            restartedConfig.UpdateValues(await context.ConfigItems.AsNoTracking().ToListAsync());
            var restartedAccounts = new PlexAccountService(api, new PlexTestClock());
            var restarted = new PlexAccountConfigService(restartedConfig, new ConfigUpdateService(db, restartedConfig), db, restartedAccounts, api);
            var selected = await restarted.SelectAsync("admin-b", saved.Id);
            Assert.Equal("connected", (await restartedAccounts.PollAsync("admin-b", selected.Handle)).State);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => restartedAccounts.PollAsync("admin-a", selected.Handle));
            restartedConfig.UpdateValues([new NzbWebDAV.Database.Models.ConfigItem
            {
                ConfigName = PlexSettings.ServersKey,
                ConfigValue = """[{"Id":"linked","Name":"Linked","Url":"http://localhost:32400","Token":"server-secret","Enabled":true,"AccountId":"owner-7"},{"Id":"manual","Name":"Manual","Url":"http://localhost:32401","Token":"manual-secret","Enabled":true}]"""
            }]);
            await restarted.DisconnectAsync(saved.Id);
            Assert.Empty(restarted.GetAccounts());
            Assert.Equal("cancelled", (await restartedAccounts.PollAsync("admin-b", selected.Handle)).State);
            var retainedServers = PlexSettings.ParseServers(restartedConfig.GetEffectiveConfigValue(PlexSettings.ServersKey));
            Assert.False(retainedServers.Single(server => server.Id == "linked").Enabled);
            Assert.True(retainedServers.Single(server => server.Id == "manual").Enabled);
        }
        finally { Environment.SetEnvironmentVariable("FRONTEND_BACKEND_API_KEY", previous); }
    }

    [Fact]
    public async Task CancelledLoginWaitingForWriteLease_DoesNotPersistCredentials()
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
            using var handler = new FakePlexHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent("""{"id":7,"username":"Owner"}""") });
            var api = new PlexApiClient(new HttpClient(handler), "installation");
            var accounts = new PlexAccountService(api, new PlexTestClock());
            var service = new PlexAccountConfigService(config, updates, db, accounts, api);
            var opened = accounts.OpenConnected("admin", "account-secret");
            using var blocking = await updates.StageAsync([]);
            var save = service.SaveLoginAsync("admin", opened.Handle);
            Assert.False(save.IsCompleted);
            accounts.Cancel("admin", opened.Handle);
            blocking.Dispose();
            await Assert.ThrowsAsync<InvalidOperationException>(() => save);
            Assert.Empty(service.GetAccounts());
            Assert.Empty(await context.ConfigItems.ToArrayAsync());
        }
        finally { Environment.SetEnvironmentVariable("FRONTEND_BACKEND_API_KEY", previous); }
    }
}
