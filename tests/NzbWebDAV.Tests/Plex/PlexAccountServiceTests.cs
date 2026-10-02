using System.Net;
using System.Text.Json;
using NzbWebDAV.Services.Plex;

namespace NzbWebDAV.Tests.Plex;

public sealed class PlexAccountServiceTests
{
    [Fact]
    public async Task Cancel_DropsCredentials_AndCapacityDoesNotGrowWithoutBound()
    {
        using var handler = new FakePlexHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("""{"id":42,"code":"pin-code","expiresIn":900}""") });
        var service = new PlexAccountService(new PlexApiClient(new HttpClient(handler), "installation"), new PlexTestClock(), capacity: 1);
        var login = await service.StartAsync("owner-a");
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync("owner-b"));
        service.Cancel("owner-a", login.Handle);
        Assert.Equal("cancelled", (await service.PollAsync("owner-a", login.Handle)).State);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DiscoverAsync("owner-a", login.Handle));
    }

    [Fact]
    public async Task LoginAndServerHandles_AreOwnedBoundedExpiringAndNeverExposeTokens()
    {
        using var handler = new FakePlexHandler(request => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(request.RequestUri!.AbsolutePath.Contains("resources")
                ? """[{"name":"Home","clientIdentifier":"machine","provides":"server","accessToken":"server-secret","connections":[{"uri":"http://192.168.1.2:32400","local":true,"relay":false}]}]"""
                : request.Method == HttpMethod.Post
                    ? """{"id":42,"code":"pin-code","expiresIn":900}"""
                    : """{"id":42,"authToken":"account-secret"}""" )
        });
        var clock = new PlexTestClock();
        var service = new PlexAccountService(new PlexApiClient(new HttpClient(handler), "installation"), clock, capacity: 2);
        var login = await service.StartAsync("owner-a");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.PollAsync("owner-b", login.Handle));
        Assert.Equal("connected", (await service.PollAsync("owner-a", login.Handle)).State);
        var servers = await service.DiscoverAsync("owner-a", login.Handle);
        var server = Assert.Single(servers);
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(servers));
        Assert.Equal("server-secret", service.ResolveServer("owner-a", server.Handle).Token);
        Assert.Throws<UnauthorizedAccessException>(() => service.ResolveServer("owner-b", server.Handle));
        clock.Now = clock.Now.AddMinutes(16);
        Assert.Throws<UnauthorizedAccessException>(() => service.ResolveServer("owner-a", server.Handle));
        Assert.Equal("expired", (await service.PollAsync("owner-a", login.Handle)).State);
    }

    [Fact]
    public void InstallationId_IsStableAcrossInstances()
    {
        var directory = Directory.CreateTempSubdirectory("plex-client-id-");
        try
        {
            var first = PlexInstallationIdentity.LoadOrCreate(directory.FullName);
            Assert.Equal(first, PlexInstallationIdentity.LoadOrCreate(directory.FullName));
            Assert.True(Guid.TryParse(first, out _));
        }
        finally { directory.Delete(true); }
    }
}

internal sealed class PlexTestClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 9, 20, 0, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
}
