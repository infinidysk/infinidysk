using System.Text.Json;
using NzbWebDAV.Tests.TestUtils;

namespace NzbWebDAV.Tests.TestUtils;

public sealed class JsonContractValidatorTests
{
    [Fact]
    public void AssertMatchesSchema_ReportsJsonPathAndActualType()
    {
        using var json = JsonDocument.Parse("""{"status":true,"nzo_ids":[1]}""");
        var error = Assert.ThrowsAny<Exception>(() =>
            JsonContractValidator.AssertMatchesSchema(json.RootElement, "sab/v1/addfile.schema.json"));
        Assert.Contains("/nzo_ids/0", error.Message, StringComparison.Ordinal);
        Assert.Contains("actual integer", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("apikey", error.Message, StringComparison.OrdinalIgnoreCase);
    }
}
