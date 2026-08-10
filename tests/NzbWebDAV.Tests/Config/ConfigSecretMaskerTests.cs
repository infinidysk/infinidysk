using System.Text.Json;
using Microsoft.AspNetCore.Http;
using NzbWebDAV.Config;

namespace NzbWebDAV.Tests.Config;

public class ConfigSecretMaskerTests
{
    [Fact]
    public void IndexerApiKeysAreMaskedAndResolvedForRoundTripUpdates()
    {
        const string configName = "indexers.instances";
        const string stored =
            """{"Indexers":[{"Name":"One","ApiKey":"first-secret"},{"Name":"Two","ApiKey":"second-secret"}]}""";
        var masker = new ConfigSecretMasker("test-signing-key");

        var masked = masker.MaskForResponse(configName, stored);

        using (var document = JsonDocument.Parse(masked))
        {
            var indexers = document.RootElement.GetProperty("Indexers");
            Assert.All(indexers.EnumerateArray(), indexer =>
                Assert.StartsWith(
                    ConfigSecretMasker.MaskPrefix,
                    indexer.GetProperty("ApiKey").GetString()!));
        }

        var resolved = masker.ResolveForUpdate(configName, masked, stored);
        using var resolvedDocument = JsonDocument.Parse(resolved);
        var resolvedIndexers = resolvedDocument.RootElement.GetProperty("Indexers");
        Assert.Equal("first-secret", resolvedIndexers[0].GetProperty("ApiKey").GetString());
        Assert.Equal("second-secret", resolvedIndexers[1].GetProperty("ApiKey").GetString());
    }

    [Fact]
    public void IndexerApiKeyCanBeReplacedWhileOtherMaskedKeysRoundTrip()
    {
        const string configName = "indexers.instances";
        const string stored =
            """{"Indexers":[{"Name":"One","ApiKey":"first-secret"},{"Name":"Two","ApiKey":"second-secret"}]}""";
        var masker = new ConfigSecretMasker("test-signing-key");
        var masked = masker.MaskForResponse(configName, stored);
        using var maskedDocument = JsonDocument.Parse(masked);
        var secondToken = maskedDocument.RootElement
            .GetProperty("Indexers")[1]
            .GetProperty("ApiKey")
            .GetString();
        var submitted =
            $$"""{"Indexers":[{"Name":"One","ApiKey":"replacement"},{"Name":"Two","ApiKey":"{{secondToken}}"}]}""";

        var resolved = masker.ResolveForUpdate(configName, submitted, stored);

        using var resolvedDocument = JsonDocument.Parse(resolved);
        var resolvedIndexers = resolvedDocument.RootElement.GetProperty("Indexers");
        Assert.Equal("replacement", resolvedIndexers[0].GetProperty("ApiKey").GetString());
        Assert.Equal("second-secret", resolvedIndexers[1].GetProperty("ApiKey").GetString());
    }

    [Fact]
    public void ProwlarrApiKeyIsMaskedAndResolvedForRoundTripUpdates()
    {
        var masker = new ConfigSecretMasker("test-signing-key");

        var masked = masker.MaskForResponse(ConfigKeys.ProwlarrApiKey, "prowlarr-secret");

        Assert.StartsWith(ConfigSecretMasker.MaskPrefix, masked);
        Assert.Equal(
            "prowlarr-secret",
            masker.ResolveForUpdate(ConfigKeys.ProwlarrApiKey, masked, "prowlarr-secret"));
        Assert.Equal(
            "replacement-secret",
            masker.ResolveForUpdate(ConfigKeys.ProwlarrApiKey, "replacement-secret", "prowlarr-secret"));
    }

    [Fact]
    public void ResolveMaskedJsonSecret_ReturnsPlaintextUnchanged()
    {
        var masker = new ConfigSecretMasker("test-signing-key");

        var resolved = masker.ResolveMaskedJsonSecret(
            "usenet.providers",
            "real-password",
            """{"Providers":[{"Pass":"stored-secret"}]}""");

        Assert.Equal("real-password", resolved);
    }

    [Fact]
    public void ResolveMaskedJsonSecret_ResolvesProviderPassAgainstStoredConfig()
    {
        const string configName = "usenet.providers";
        const string stored =
            """{"Providers":[{"Host":"news.example","User":"u1","Pass":"alpha-secret"},{"Host":"backup.example","User":"u2","Pass":"beta-secret"}]}""";
        var masker = new ConfigSecretMasker("test-signing-key");
        var masked = masker.MaskForResponse(configName, stored);
        using var maskedDocument = JsonDocument.Parse(masked);
        var secondToken = maskedDocument.RootElement
            .GetProperty("Providers")[1]
            .GetProperty("Pass")
            .GetString()!;

        var resolved = masker.ResolveMaskedJsonSecret(configName, secondToken, stored);

        Assert.Equal("beta-secret", resolved);
    }

    [Fact]
    public void ResolveMaskedJsonSecret_ThrowsForUnknownMaskToken()
    {
        var masker = new ConfigSecretMasker("test-signing-key");
        var otherMasker = new ConfigSecretMasker("other-signing-key");
        var stored = """{"Providers":[{"Pass":"real-secret"}]}""";
        var foreignToken = otherMasker.MaskForResponse("usenet.providers", stored);
        using var document = JsonDocument.Parse(foreignToken);
        var bogusToken = document.RootElement
            .GetProperty("Providers")[0]
            .GetProperty("Pass")
            .GetString()!;

        var ex = Assert.Throws<BadHttpRequestException>(() =>
            masker.ResolveMaskedJsonSecret("usenet.providers", bogusToken, stored));

        Assert.Contains("usenet.providers", ex.Message);
    }

    [Fact]
    public void ResolveMaskedJsonSecret_ThrowsForForgedMaskToken()
    {
        var masker = new ConfigSecretMasker("test-signing-key");
        var forged = $"{ConfigSecretMasker.MaskPrefix}AAAAAAAAAAAAAAAAAAAAAA.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

        var ex = Assert.Throws<BadHttpRequestException>(() =>
            masker.ResolveMaskedJsonSecret(
                "usenet.providers",
                forged,
                """{"Providers":[{"Pass":"real-secret"}]}"""));

        Assert.Contains("usenet.providers", ex.Message);
    }
}
