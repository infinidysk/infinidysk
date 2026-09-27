using NzbWebDAV.Services.Plex;

namespace NzbWebDAV.Tests.Plex;

public sealed class PlexSettingsTests
{
    [Fact]
    public void ValidationHook_PreservesUnsetAndUnrelatedConfiguration()
    {
        PlexSettings.ValidateItem("unrelated.key", null!);
        PlexSettings.ValidateItem("plex.servers", null!);
        PlexSettings.ValidateItem("plex.accounts", "");
    }

    [Fact]
    public void StrictConfiguration_RejectsUnknownPropertiesIncludingNestedMappings()
    {
        var server = System.Text.Json.JsonSerializer.SerializeToNode(PlexApiClientTests.Server())!;
        server["TypoEnabled"] = true;
        Assert.Throws<ArgumentException>(() => PlexSettings.ValidateItem("plex.servers", "[" + server.ToJsonString() + "]", true));
        PlexSettings.ValidateItem("plex.servers", "[" + server.ToJsonString() + "]", false);
        server.AsObject().Remove("TypoEnabled");
        server["PathMappings"] = System.Text.Json.Nodes.JsonNode.Parse("""[{"PlexPath":"/plex","DavPath":"/dav","typo":"bad"}]""");
        Assert.Throws<ArgumentException>(() => PlexSettings.ValidateItem("plex.servers", "[" + server.ToJsonString() + "]", true));
        Assert.Throws<ArgumentException>(() => PlexSettings.ValidateItem("plex.accounts", """[{"Id":"1","Name":"Owner","Token":"secret","unknown":true}]""", true));
    }

    [Fact]
    public void CredentialModels_DoNotRevealSecretsInDiagnosticStrings()
    {
        object[] values = [PlexApiClientTests.Server(), new PlexDiscoveredServer("id", "Home", "secret", []),
            new PlexPin(1, "secret", 900, "secret"), new PlexServerSaveRequest { Token = "secret" },
            new NzbWebDAV.Api.Controllers.Plex.PlexOperationRequest { Pin = "secret" },
            new PlexLoginStart("handle", "https://app.plex.tv/auth#code=secret", DateTimeOffset.UtcNow)];
        Assert.All(values, value => Assert.DoesNotContain("secret", value.ToString()!));
    }

    [Theory]
    [InlineData("/dav", "/local")]
    [InlineData("/dav/../escape", null)]
    [InlineData(null, "relative/local")]
    [InlineData(null, "/local/../escape")]
    public void AmbiguousOrUnsafeMappings_AreRejected(string? dav, string? local)
    {
        var server = PlexApiClientTests.Server() with { PathMappings = [new PlexPathMapping("/plex", dav, local)] };
        Assert.Throws<ArgumentException>(() => PlexSettings.ParseServers(System.Text.Json.JsonSerializer.Serialize(new[] { server })));
    }

    [Theory]
    [InlineData("Name")]
    [InlineData("Url")]
    [InlineData("Token")]
    [InlineData("PathMappings")]
    public void ExplicitNullFields_AreRejectedAsValidationErrors(string property)
    {
        var value = System.Text.Json.JsonSerializer.SerializeToNode(PlexApiClientTests.Server())!;
        value[property] = null;
        Assert.Throws<ArgumentException>(() => PlexSettings.ParseServers("[" + value.ToJsonString() + "]"));
    }

    [Theory]
    [InlineData("http://user:secret@localhost:32400")]
    [InlineData("http://localhost:32400/?token=secret")]
    [InlineData("file:///etc/passwd")]
    public void InvalidServerUrls_AreRejectedWithoutLeakingInput(string url)
    {
        var exception = Assert.Throws<ArgumentException>(() => PlexSettings.ValidateServerUri(url));
        Assert.DoesNotContain("secret", exception.ToString());
    }
}
