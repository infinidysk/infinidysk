using Usenet.Nntp;
using Usenet.Yenc;
using UsenetSharp.Clients;
using OurYencStream = UsenetSharp.Streams.YencStream;
using UsenetYencStream = Usenet.Yenc.YencStream;

namespace UsenetSharpTest.Integration;

/// <summary>
/// Validates our YencStream implementation against the known-good Usenet package implementation.
/// Downloads the same segments using both implementations and compares decoded output.
/// </summary>
[TestFixture]
[NonParallelizable]
public class YencStreamValidationTests
{
    [Test]
    public async Task YencStream_CompareWithUsenetPackage_MultipleSegments()
    {
        // Arrange - Test multiple segments
        var segmentIds = new[]
        {
            "8mthBMhpfyOJFM7OPe2RsZhm@CAtZlPkA1OiI.WLo",
            "439e9msqibLI2Ckyt7z6EMUY@iLqyzimBg7ac.t2n",
            "RdmCrBrzTPnTiYj7ze5Gwyt6@mfdz3EInI86g.bfV",
            "njX6awmG5Rl0lZbBbfll8WtA@M6zC3hmaiMoK.w5x",
            "vAOEczfxpsXMjg0bUPUGO7Bb@KDqE994Bw3O0.BG5",
        };

        var cancellationToken = new CancellationTokenSource(TimeSpan.FromSeconds(120)).Token;

        // Connect both clients once
        var usenetClient = new NntpClient(new NntpConnection());
        await usenetClient.ConnectAsync(Credentials.Host, 563, true);
        usenetClient.Authenticate(Credentials.Username, Credentials.Password);

        var ourClient = new UsenetClient();
        await ourClient.ConnectAsync(Credentials.Host, 563, true, cancellationToken);
        await ourClient.AuthenticateAsync(Credentials.Username, Credentials.Password, cancellationToken);

        try
        {
            foreach (var segmentId in segmentIds)
            {
                Console.WriteLine($"\nValidating segment: {segmentId}");

                // Decode with Usenet package
                var response = usenetClient.Article($"<{segmentId}>");
                Assert.That(response.Success, Is.True);

                byte[] usenetPackageDecoded;
                YencHeader usenetPackageHeader;
                using (UsenetYencStream yencStream = YencStreamDecoder.Decode(response.Article.Body))
                using (var ms = new MemoryStream())
                {
                    usenetPackageHeader = yencStream.Header;
                    yencStream.CopyTo(ms);
                    usenetPackageDecoded = ms.ToArray();
                }

                // Decode with our implementation
                var bodyResult = await ourClient.BodyAsync(segmentId, cancellationToken);

                byte[] ourDecoded;
                UsenetSharp.Models.UsenetYencHeader? ourHeader;
                using (var yencStream = new OurYencStream(bodyResult.Stream!))
                {
                    ourHeader = await yencStream.GetYencHeadersAsync(cancellationToken);
                    using var ms = new MemoryStream();
                    await yencStream.CopyToAsync(ms, cancellationToken);
                    ourDecoded = ms.ToArray();
                }

                // Assert - Compare decoded data
                Assert.That(ourDecoded.Length, Is.EqualTo(usenetPackageDecoded.Length),
                    $"Segment {segmentId}: Length mismatch");

                Assert.That(ourDecoded, Is.EqualTo(usenetPackageDecoded),
                    $"Segment {segmentId}: Data mismatch");

                // Assert - Compare yEnc headers
                Assert.That(ourHeader, Is.Not.Null,
                    $"Segment {segmentId}: Our implementation should parse yEnc headers");
                Assert.That(ourHeader!.FileName, Is.EqualTo(usenetPackageHeader.FileName),
                    $"Segment {segmentId}: FileName mismatch");
                Assert.That(ourHeader.FileSize, Is.EqualTo(usenetPackageHeader.FileSize),
                    $"Segment {segmentId}: FileSize mismatch");
                Assert.That(ourHeader.LineLength, Is.EqualTo(usenetPackageHeader.LineLength),
                    $"Segment {segmentId}: LineLength mismatch");
                Assert.That(ourHeader.PartNumber, Is.EqualTo(usenetPackageHeader.PartNumber),
                    $"Segment {segmentId}: PartNumber mismatch");
                Assert.That(ourHeader.TotalParts, Is.EqualTo(usenetPackageHeader.TotalParts),
                    $"Segment {segmentId}: TotalParts mismatch");
                Assert.That(ourHeader.PartSize, Is.EqualTo(usenetPackageHeader.PartSize),
                    $"Segment {segmentId}: PartSize mismatch");
                Assert.That(ourHeader.PartOffset, Is.EqualTo(usenetPackageHeader.PartOffset),
                    $"Segment {segmentId}: PartOffset mismatch");

                Console.WriteLine($"  ✓ Data: {ourDecoded.Length} bytes - Match!");
                Console.WriteLine($"  ✓ Headers: FileName={ourHeader.FileName}, " +
                                  $"Part={ourHeader.PartNumber}/{ourHeader.TotalParts} - Match!");
            }
        }
        finally
        {
            usenetClient.Quit();
            ourClient.Dispose();
        }

        Console.WriteLine($"\n✓ All {segmentIds.Length} segments validated successfully!");
    }

    [Test]
    public async Task YencStream_ByteByByteComparison_WithUsenetPackage()
    {
        // Arrange - Most thorough test: compare every single byte
        var segmentId = "439e9msqibLI2Ckyt7z6EMUY@iLqyzimBg7ac.t2n";
        var cancellationToken = new CancellationTokenSource(TimeSpan.FromSeconds(60)).Token;

        // Decode with Usenet package
        byte[] usenetPackageDecoded;
        var usenetClient = new NntpClient(new NntpConnection());
        try
        {
            await usenetClient.ConnectAsync(Credentials.Host, 563, true);
            usenetClient.Authenticate(Credentials.Username, Credentials.Password);

            var response = usenetClient.Article($"<{segmentId}>");
            Assert.That(response.Success, Is.True);

            using (UsenetYencStream yencStream = YencStreamDecoder.Decode(response.Article.Body))
            using (var ms = new MemoryStream())
            {
                yencStream.CopyTo(ms);
                usenetPackageDecoded = ms.ToArray();
            }
        }
        finally
        {
            usenetClient.Quit();
        }

        // Decode with our implementation
        byte[] ourDecoded;
        using (var ourClient = new UsenetClient())
        {
            await ourClient.ConnectAsync(Credentials.Host, 563, true, cancellationToken);

            await ourClient.AuthenticateAsync(Credentials.Username, Credentials.Password, cancellationToken);

            var bodyResult = await ourClient.BodyAsync(segmentId, cancellationToken);

            using var yencStream = new OurYencStream(bodyResult.Stream!);
            using var ms = new MemoryStream();
            await yencStream.CopyToAsync(ms, cancellationToken);
            ourDecoded = ms.ToArray();
        }

        // Assert - Byte-by-byte comparison
        Assert.That(ourDecoded.Length, Is.EqualTo(usenetPackageDecoded.Length));

        for (int i = 0; i < ourDecoded.Length; i++)
        {
            if (ourDecoded[i] != usenetPackageDecoded[i])
            {
                Assert.Fail($"Byte mismatch at position {i}: Our={ourDecoded[i]}, Usenet={usenetPackageDecoded[i]}");
            }
        }

        Console.WriteLine($"✓ Byte-by-byte validation passed! All {ourDecoded.Length} bytes match exactly.");
    }
}
