using UsenetSharp.Clients;
using UsenetSharp.Streams;

namespace UsenetSharpTest.Integration;

[TestFixture]
[NonParallelizable]
public class YencStreamIntegrationTests
{
    [Test]
    public async Task YencStream_RealUsenetData_DecodesSuccessfully()
    {
        // Arrange - Use a known valid segment ID from real Usenet server
        var segmentId = "8mthBMhpfyOJFM7OPe2RsZhm@CAtZlPkA1OiI.WLo";

        using var client = new UsenetClient();
        var cancellationToken = new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token;

        // Connect to server
        await client.ConnectAsync(Credentials.Host, 563, true, cancellationToken);

        // Authenticate
        await client.AuthenticateAsync(Credentials.Username, Credentials.Password, cancellationToken);

        // Act - Download the article body (yEnc-encoded)
        var bodyResult = await client.BodyAsync(segmentId, cancellationToken);
        Assert.That(bodyResult.Stream, Is.Not.Null);

        // Decode using YencStream
        using var yencStream = new YencStream(bodyResult.Stream!);
        using var decodedData = new MemoryStream();
        await yencStream.CopyToAsync(decodedData, cancellationToken);

        // Get headers
        var headers = await yencStream.GetYencHeadersAsync(cancellationToken);

        // Assert - Verify YencStream parsed headers correctly
        Assert.That(headers, Is.Not.Null, "YencHeaders should be populated");
        Assert.That(headers!.FileName, Is.Not.Null.And.Not.Empty, "FileName should be present");
        Assert.That(headers.FileSize, Is.GreaterThan(0), "FileSize should be greater than 0");
        Assert.That(headers.LineLength, Is.GreaterThan(0), "LineLength should be greater than 0");

        // Verify we actually decoded some data
        Assert.That(decodedData.Length, Is.GreaterThan(0), "Should have decoded some data");

        // For multi-part files, the decoded part size should match PartSize
        if (headers.PartNumber > 0)
        {
            Assert.That(decodedData.Length, Is.EqualTo(headers.PartSize),
                "Decoded data length should match PartSize for multi-part files");
        }

        Console.WriteLine($"Successfully decoded yEnc data:");
        Console.WriteLine($"  File: {headers.FileName}");
        Console.WriteLine($"  Total Size: {headers.FileSize} bytes");
        Console.WriteLine($"  Part: {headers.PartNumber}/{headers.TotalParts}");
        Console.WriteLine($"  Decoded Size: {decodedData.Length} bytes");
    }

    [Test]
    public async Task YencStream_MultipleRealSegments_AllDecodeSuccessfully()
    {
        // Arrange - Test multiple known valid segment IDs
        var segmentIds = new[]
        {
            "8mthBMhpfyOJFM7OPe2RsZhm@CAtZlPkA1OiI.WLo",
            "439e9msqibLI2Ckyt7z6EMUY@iLqyzimBg7ac.t2n",
            "RdmCrBrzTPnTiYj7ze5Gwyt6@mfdz3EInI86g.bfV"
        };

        using var client = new UsenetClient();
        var cancellationToken = new CancellationTokenSource(TimeSpan.FromSeconds(60)).Token;

        // Connect and authenticate
        await client.ConnectAsync(Credentials.Host, 563, true, cancellationToken);

        await client.AuthenticateAsync(Credentials.Username, Credentials.Password, cancellationToken);

        // Act - Download and decode each segment
        foreach (var segmentId in segmentIds)
        {
            var bodyResult = await client.BodyAsync(segmentId, cancellationToken);
            Assert.That(bodyResult.Stream, Is.Not.Null);

            using var yencStream = new YencStream(bodyResult.Stream!);
            using var decodedData = new MemoryStream();
            await yencStream.CopyToAsync(decodedData, cancellationToken);

            // Get headers
            var headers = await yencStream.GetYencHeadersAsync(cancellationToken);

            // Assert - Each segment should decode successfully
            Assert.That(headers, Is.Not.Null, $"Headers missing for segment: {segmentId}");
            Assert.That(decodedData.Length, Is.GreaterThan(0), $"No data decoded for segment: {segmentId}");

            Console.WriteLine($"Segment {segmentId}: {decodedData.Length} bytes decoded");
        }
    }

    [Test]
    public async Task YencStream_RealUsenetData_ChunkedReading()
    {
        // Arrange - Test that chunked reading works with real data
        var segmentId = "8mthBMhpfyOJFM7OPe2RsZhm@CAtZlPkA1OiI.WLo";

        using var client = new UsenetClient();
        var cancellationToken = new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token;

        await client.ConnectAsync(Credentials.Host, 563, true, cancellationToken);

        await client.AuthenticateAsync(Credentials.Username, Credentials.Password, cancellationToken);

        var bodyResult = await client.BodyAsync(segmentId, cancellationToken);
        Assert.That(bodyResult.Stream, Is.Not.Null);

        // Act - Read in small chunks
        using var yencStream = new YencStream(bodyResult.Stream!);
        using var decodedData = new MemoryStream();
        var buffer = new byte[1024]; // Small buffer to test chunked reading
        int bytesRead;

        while ((bytesRead = await yencStream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            decodedData.Write(buffer, 0, bytesRead);
        }

        // Get headers
        var headers = await yencStream.GetYencHeadersAsync(cancellationToken);

        // Assert
        Assert.That(headers, Is.Not.Null);
        Assert.That(decodedData.Length, Is.GreaterThan(0));

        Console.WriteLine($"Read {decodedData.Length} bytes in chunks of up to 1024 bytes");
    }
}
