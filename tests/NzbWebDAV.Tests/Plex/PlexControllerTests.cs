using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NzbWebDAV.Tests.TestUtils;

namespace NzbWebDAV.Tests.Plex;

[Collection(nameof(HttpIntegrationCollection))]
public sealed class PlexControllerTests
{
    [Fact]
    public async Task VerifiedOwner_CanReadMaskedServerConfiguration()
    {
        await using var factory = new NzbDavWebApplicationFactory();
        using var client = factory.CreateAuthenticatedClient();
        var owner = new string('a', 64);
        var expiry = DateTimeOffset.UtcNow.AddMinutes(2).ToUnixTimeSeconds();
        var signature = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(NzbDavWebApplicationFactory.ApiKey),
            Encoding.UTF8.GetBytes($"plex-owner-v1\n{owner}\n{expiry}")));
        client.DefaultRequestHeaders.Add("X-InfiniDysk-Plex-Owner", $"{owner}.{expiry}.{signature}");
        using var response = await client.PostAsJsonAsync("/api/plex/servers", new { });
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Empty(json.RootElement.GetProperty("servers").EnumerateArray());
    }

    [Theory]
    [InlineData("login/start")]
    [InlineData("login/poll")]
    [InlineData("save")]
    [InlineData("servers")]
    public async Task ApiKeyAloneOrBrowserForgedOwner_CannotUsePlexOperations(string endpoint)
    {
        await using var factory = new NzbDavWebApplicationFactory();
        using var client = factory.CreateAuthenticatedClient();
        client.DefaultRequestHeaders.Add("X-InfiniDysk-Plex-Owner", "browser-forged-owner");
        using var response = await client.PostAsJsonAsync("/api/plex/" + endpoint, new { });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
