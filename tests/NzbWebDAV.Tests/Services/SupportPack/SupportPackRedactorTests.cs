using System.Text.Json;
using NzbWebDAV.Config;
using NzbWebDAV.Services.SupportPack;

namespace NzbWebDAV.Tests.Services.SupportPack;

public class SupportPackRedactorTests
{
    [Fact]
    public void RedactText_RedactsLiteralEncodedUrlSecretsAndPseudonymizesAddresses()
    {
        var redactor = new SupportPackRedactor(["top secret", "api-secret"]);

        var result = redactor.RedactText(
            "GET https://user:pass@example.test/nzb?apikey=api-secret&token=abc " +
            "from 192.0.2.10 and [2001:db8::1]; repeated 192.0.2.10; encoded top%20secret");

        Assert.DoesNotContain("api-secret", result);
        Assert.DoesNotContain("top%20secret", result);
        Assert.DoesNotContain("user:pass@", result);
        Assert.DoesNotContain("192.0.2.10", result);
        Assert.DoesNotContain("2001:db8::1", result);
        Assert.Contains("apikey=[REDACTED]", result);
        Assert.Contains("token=[REDACTED]", result);
        Assert.Equal(2, redactor.AddressesPseudonymized);
    }

    [Fact]
    public void RedactText_KeepsVersionStringsThatLookLikeAddresses()
    {
        var redactor = new SupportPackRedactor([]);

        // Browser and player versions are dotted quads. Mangling them costs the
        // client identity that playback triage relies on, so only real addresses
        // may be pseudonymized.
        var result = redactor.RedactText(
            "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 " +
            "Chrome/140.0.0.0 Safari/537.36 Edg/140.0.0.0 client v1.2.3.4 from 192.0.2.10");

        Assert.Contains("Chrome/140.0.0.0", result);
        Assert.Contains("Edg/140.0.0.0", result);
        Assert.Contains("v1.2.3.4", result);
        Assert.DoesNotContain("192.0.2.10", result);
        Assert.Equal(1, redactor.AddressesPseudonymized);
    }

    [Theory]
    [InlineData("connected from \"203.0.113.7\"")]
    [InlineData("host=203.0.113.7")]
    [InlineData("proxy http://203.0.113.7:8080/path")]
    [InlineData("peer (203.0.113.7)")]
    [InlineData("203.0.113.7 opened a range")]
    public void RedactText_StillPseudonymizesRealAddressContexts(string input)
    {
        var redactor = new SupportPackRedactor([]);

        var result = redactor.RedactText(input);

        Assert.DoesNotContain("203.0.113.7", result);
        Assert.Contains("[IP-1]", result);
    }

    [Fact]
    public void RedactConfigurationValue_RedactsKnownStructuredSecrets()
    {
        var redactor = new SupportPackRedactor([]);
        var result = redactor.RedactConfigurationValue(
            ConfigKeys.UsenetProviders,
            """{"Providers":[{"Host":"news.example","User":"alice","Pass":"provider-secret"}]}""");

        using var document = JsonDocument.Parse(result);
        var provider = document.RootElement.GetProperty("Providers")[0];
        Assert.Equal("news.example", provider.GetProperty("Host").GetString());
        Assert.Equal("alice", provider.GetProperty("User").GetString());
        Assert.Equal("[REDACTED]", provider.GetProperty("Pass").GetString());
    }

    [Fact]
    public void RedactText_StillRedactsSentinelSecretsInsideAllowlistedJson()
    {
        var redactor = new SupportPackRedactor(["sentinel-api-key"]);
        var json = """{"schemaVersion":1,"streaming":{"note":"sentinel-api-key"}}""";
        var result = redactor.RedactText(json);
        Assert.DoesNotContain("sentinel-api-key", result);
        Assert.Contains("[REDACTED]", result);
    }

    [Fact]
    public void RedactConfigurationValue_FailsClosedForMalformedStructuredConfig()
    {
        var redactor = new SupportPackRedactor([]);

        var result = redactor.RedactConfigurationValue(ConfigKeys.IndexersInstances, "{not-json");

        Assert.Equal("[REDACTED_MALFORMED_STRUCTURED_VALUE]", result);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("null")]
    [InlineData("schema")]
    [InlineData("enabled")]
    [InlineData("203.0.113.50")]
    public void RedactJson_PreservesPropertyNamesAndPrimitiveSyntax(string secret)
    {
        var redactor = new SupportPackRedactor([secret]);
        var json = """
            {
              "schemaVersion": 1,
              "enabled": true,
              "flag": null,
              "note": "value-before",
              "nested": { "schema": "inner", "enabled": false }
            }
            """.Replace("value-before", secret, StringComparison.Ordinal);

        var result = redactor.RedactJson(json);
        using var document = JsonDocument.Parse(result);
        var root = document.RootElement;

        Assert.Equal(JsonValueKind.Number, root.GetProperty("schemaVersion").ValueKind);
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(JsonValueKind.True, root.GetProperty("enabled").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("flag").ValueKind);
        Assert.Equal("[REDACTED]", root.GetProperty("note").GetString());
        Assert.Equal("inner", root.GetProperty("nested").GetProperty("schema").GetString());
        Assert.Equal(JsonValueKind.False, root.GetProperty("nested").GetProperty("enabled").ValueKind);
    }
}
