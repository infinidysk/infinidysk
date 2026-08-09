using System.IO.Compression;
using System.Text;
using System.Text.Json;
using NzbWebDAV.Database;

namespace NzbWebDAV.Tests.Database;

public class DeserializeOrFallbackTests
{
    [Fact]
    public void DeserializeOrFallback_ReadsJsonArray()
    {
        var result = DavDatabaseContext.DeserializeOrFallback<string[]>("[\"a\",\"b\"]");
        Assert.Equal(["a", "b"], result!);
    }

    [Fact]
    public void DeserializeOrFallback_ReturnsDefaultForEmpty()
    {
        Assert.Null(DavDatabaseContext.DeserializeOrFallback<string[]>(null));
        Assert.Null(DavDatabaseContext.DeserializeOrFallback<string[]>(""));
    }

    [Fact]
    public void DeserializeOrFallback_DecodesLegacyBase64Brotli()
    {
        var json = JsonSerializer.Serialize(new[] { "seg1", "seg2" });
        var encoded = EncodeBase64Brotli(json);

        var result = DavDatabaseContext.DeserializeOrFallback<string[]>(encoded);
        Assert.Equal(["seg1", "seg2"], result!);
    }

    [Fact]
    public void DeserializeOrFallback_ReturnsDefaultForCorruptNonJson()
    {
        // Starts with a non-JSON character so the fallback path runs, then fails decode.
        var result = DavDatabaseContext.DeserializeOrFallback<string[]>("!!!!not-base64!!!!");
        Assert.Null(result);
    }

    private static string EncodeBase64Brotli(string json)
    {
        using var output = new MemoryStream();
        using (var brotli = new BrotliStream(output, CompressionLevel.Fastest, leaveOpen: true))
        using (var writer = new StreamWriter(brotli, Encoding.UTF8))
        {
            writer.Write(json);
        }

        return Convert.ToBase64String(output.ToArray());
    }
}
