using System.Text;
using UsenetSharp.Streams;

namespace UsenetSharpTest.Streams;

[TestFixture]
public class YencStreamTests
{
    private static Stream CreateYencEncodedStream(string content)
    {
        // Simple single-part yEnc encoding
        // Use Latin1 encoding to preserve byte values when content represents binary data
        var inputBytes = Encoding.Latin1.GetBytes(content);
        int? column = 0;
        var encodedBytes = RapidYencSharp.YencEncoder.EncodeEx(inputBytes, ref column, 128, true);

        // Build the full yEnc message using Latin1 (preserves all byte values 0-255)
        var headerBytes = Encoding.Latin1.GetBytes($"=ybegin line=128 size={inputBytes.Length} name=test.bin\r\n");
        var footerBytes = Encoding.Latin1.GetBytes($"\r\n=yend size={inputBytes.Length}\r\n");

        var ms = new MemoryStream();
        ms.Write(headerBytes);
        ms.Write(encodedBytes);
        ms.Write(footerBytes);
        ms.Position = 0;
        return ms;
    }

    private static Stream CreateMultiPartYencStream(string content, long partBegin, long partEnd)
    {
        // Use Latin1 encoding to preserve byte values when content represents binary data
        var inputBytes = Encoding.Latin1.GetBytes(content);
        int? column = 0;
        var encodedBytes = RapidYencSharp.YencEncoder.EncodeEx(inputBytes, ref column, 128, true);

        // Build the full yEnc message using Latin1 (preserves all byte values 0-255)
        var totalSize = inputBytes.Length;
        var headerBytes = Encoding.Latin1.GetBytes($"=ybegin line=128 size={totalSize} name=test.bin\r\n");
        var partHeaderBytes = Encoding.Latin1.GetBytes($"=ypart begin={partBegin} end={partEnd}\r\n");
        var partSize = partEnd - partBegin + 1;
        var footerBytes = Encoding.Latin1.GetBytes($"\r\n=yend size={partSize} part={partBegin}\r\n");

        var ms = new MemoryStream();
        ms.Write(headerBytes);
        ms.Write(partHeaderBytes);
        ms.Write(encodedBytes);
        ms.Write(footerBytes);
        ms.Position = 0;
        return ms;
    }

    [Test]
    public async Task YencStream_SinglePart_DecodesCorrectly()
    {
        // Arrange
        var originalContent = "Hello, World! This is a test of yEnc encoding.";
        var encodedStream = CreateYencEncodedStream(originalContent);

        // Act
        using var yencStream = new YencStream(encodedStream);
        using var ms = new MemoryStream();
        await yencStream.CopyToAsync(ms);

        var decodedContent = Encoding.UTF8.GetString(ms.ToArray());

        // Assert
        Assert.That(decodedContent, Is.EqualTo(originalContent));
    }

    [Test]
    public async Task YencStream_ParsesHeaders_SinglePart()
    {
        // Arrange
        var originalContent = "Test content";
        var encodedStream = CreateYencEncodedStream(originalContent);

        // Act
        using var yencStream = new YencStream(encodedStream);
        var headers = await yencStream.GetYencHeadersAsync();

        // Assert
        Assert.That(headers, Is.Not.Null);
        Assert.That(headers!.FileName, Is.EqualTo("test.bin"));
        Assert.That(headers.FileSize, Is.EqualTo(originalContent.Length));
        Assert.That(headers.LineLength, Is.EqualTo(128));
        Assert.That(headers.PartNumber, Is.EqualTo(0));
    }

    [Test]
    public async Task YencStream_ParsesHeaders_MultiPart()
    {
        // Arrange
        var originalContent = "Multipart test content";
        var encodedStream = CreateMultiPartYencStream(originalContent, 1, originalContent.Length);

        // Act
        using var yencStream = new YencStream(encodedStream);
        var headers = await yencStream.GetYencHeadersAsync();

        // Assert
        Assert.That(headers, Is.Not.Null);
        Assert.That(headers!.PartNumber, Is.EqualTo(0));
        Assert.That(headers.PartOffset, Is.EqualTo(0)); // 1-based to 0-based
        Assert.That(headers.PartSize, Is.EqualTo(originalContent.Length));
    }

    [Test]
    public void ParseYencHeaders_MultipartBytes_ParsesAllFields()
    {
        var headers = YencStream.ParseYencHeaders(
            "=ybegin part=2 total=4 line=128 size=1000 name=test.bin"u8,
            "=ypart begin=251 end=500"u8);

        Assert.Multiple(() =>
        {
            Assert.That(headers.FileName, Is.EqualTo("test.bin"));
            Assert.That(headers.FileSize, Is.EqualTo(1000));
            Assert.That(headers.LineLength, Is.EqualTo(128));
            Assert.That(headers.PartNumber, Is.EqualTo(2));
            Assert.That(headers.TotalParts, Is.EqualTo(4));
            Assert.That(headers.PartOffset, Is.EqualTo(250));
            Assert.That(headers.PartSize, Is.EqualTo(250));
        });
    }

    [Test]
    public void ParseYencHeaders_MissingAndMalformedValues_UsesExistingDefaults()
    {
        var headers = YencStream.ParseYencHeaders(
            "=ybegin line=invalid name=test.bin"u8);

        Assert.Multiple(() =>
        {
            Assert.That(headers.FileName, Is.EqualTo("test.bin"));
            Assert.That(headers.FileSize, Is.Zero);
            Assert.That(headers.LineLength, Is.Zero);
            Assert.That(headers.PartNumber, Is.Zero);
            Assert.That(headers.TotalParts, Is.Zero);
            Assert.That(headers.PartOffset, Is.Zero);
            Assert.That(headers.PartSize, Is.Zero);
        });
    }

    [Test]
    public async Task YencStream_ReadInChunks_DecodesCorrectly()
    {
        // Arrange
        var originalContent = "This is a longer test to ensure chunked reading works correctly!";
        var encodedStream = CreateYencEncodedStream(originalContent);

        // Act
        using var yencStream = new YencStream(encodedStream);
        using var ms = new MemoryStream();
        var buffer = new byte[10]; // Small buffer to force multiple reads
        int bytesRead;

        while ((bytesRead = await yencStream.ReadAsync(buffer)) > 0)
        {
            ms.Write(buffer, 0, bytesRead);
        }

        var decodedContent = Encoding.UTF8.GetString(ms.ToArray());

        // Assert
        Assert.That(decodedContent, Is.EqualTo(originalContent));
    }

    [Test]
    public async Task YencStream_EmptyDataLine_DoesNotEndStream()
    {
        // 'A' and 'B' encode to 'k' and 'l'; the empty line between them is not EOF.
        var encoded = Encoding.Latin1.GetBytes(
            "=ybegin line=128 size=2 name=test.bin\r\nk\r\n\r\nl\r\n=yend size=2\r\n");
        using var yencStream = new YencStream(new MemoryStream(encoded));
        using var decoded = new MemoryStream();

        await yencStream.CopyToAsync(decoded);

        Assert.That(Encoding.Latin1.GetString(decoded.ToArray()), Is.EqualTo("AB"));
    }

    [Test]
    public async Task YencStream_EmptyStream_ThrowsInvalidDataException()
    {
        // Arrange
        var emptyStream = new MemoryStream();

        // Act & Assert
        using var yencStream = new YencStream(emptyStream);
        var buffer = new byte[10];
        Assert.ThrowsAsync<InvalidDataException>(async () => await yencStream.ReadAsync(buffer));
    }

    [Test]
    public async Task YencStream_MissingYbegin_ThrowsInvalidDataException()
    {
        // Arrange
        var invalidStream = new MemoryStream(Encoding.ASCII.GetBytes("Some random content\nwithout yEnc headers"));

        // Act & Assert
        using var yencStream = new YencStream(invalidStream);
        var buffer = new byte[10];
        Assert.ThrowsAsync<InvalidDataException>(async () => await yencStream.ReadAsync(buffer));
    }

    [Test]
    public async Task YencStream_BinaryData_DecodesCorrectly()
    {
        // Arrange
        var binaryData = new byte[] { 0, 1, 2, 3, 4, 5, 255, 254, 253, 128, 127, 10, 13 };
        var originalContent = Encoding.Latin1.GetString(binaryData);
        var encodedStream = CreateYencEncodedStream(originalContent);

        // Act
        using var yencStream = new YencStream(encodedStream);
        using var ms = new MemoryStream();
        await yencStream.CopyToAsync(ms);

        // Assert
        Assert.That(ms.ToArray(), Is.EqualTo(binaryData));
    }

    [Test]
    public async Task YencStream_CanReadReturnsFalse_AfterEndReached()
    {
        // Arrange
        var originalContent = "Test";
        var encodedStream = CreateYencEncodedStream(originalContent);

        // Act
        using var yencStream = new YencStream(encodedStream);
        using var ms = new MemoryStream();
        await yencStream.CopyToAsync(ms);

        var buffer = new byte[10];
        var bytesRead = await yencStream.ReadAsync(buffer);

        // Assert
        Assert.That(bytesRead, Is.EqualTo(0));
    }

    [Test]
    public async Task YencStream_LongEncodedLine_DecodesCorrectly()
    {
        var original = Enumerable.Range(0, 4096).Select(index => (byte)(index % 251)).ToArray();
        int? column = 0;
        var encoded = RapidYencSharp.YencEncoder.EncodeEx(original, ref column, 4096, true);
        using var source = new MemoryStream();
        await source.WriteAsync(Encoding.ASCII.GetBytes(
            $"=ybegin line=4096 size={original.Length} name=long.bin\r\n"));
        await source.WriteAsync(encoded);
        await source.WriteAsync(Encoding.ASCII.GetBytes(
            $"\r\n=yend size={original.Length}\r\n"));
        source.Position = 0;
        using var yencStream = new YencStream(source, leaveOpen: true);
        using var decoded = new MemoryStream();

        await yencStream.CopyToAsync(decoded);

        Assert.That(decoded.ToArray(), Is.EqualTo(original));
        Assert.That(source.CanRead, Is.True);
    }
}
