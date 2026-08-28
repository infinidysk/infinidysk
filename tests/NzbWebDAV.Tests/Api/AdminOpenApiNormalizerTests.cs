using NzbWebDAV.Api.OpenApi;

namespace NzbWebDAV.Tests.Api;

public sealed class AdminOpenApiNormalizerTests
{
    [Fact]
    public void Normalize_ForcesContractVersionEmptyServerAndSortedKeys()
    {
        const string input = """
            {
              "openapi": "3.1.0",
              "paths": {
                "/z": { "get": { "operationId": "z" } },
                "/a": { "post": { "operationId": "a" } }
              },
              "info": { "title": "t", "version": "9.9.9" },
              "servers": [ { "url": "http://localhost:5000" } ]
            }
            """;

        var normalized = AdminOpenApiNormalizer.Normalize(input);

        Assert.Equal(
            """
            {
              "info": {
                "title": "t",
                "version": "2.0.0"
              },
              "openapi": "3.1.0",
              "paths": {
                "/a": {
                  "post": {
                    "operationId": "a"
                  }
                },
                "/z": {
                  "get": {
                    "operationId": "z"
                  }
                }
              },
              "servers": [
                {
                  "url": ""
                }
              ]
            }

            """.Replace("\r\n", "\n", StringComparison.Ordinal),
            normalized);
        Assert.EndsWith("\n", normalized, StringComparison.Ordinal);
        Assert.False(normalized.EndsWith("\n\n", StringComparison.Ordinal));
    }
}
