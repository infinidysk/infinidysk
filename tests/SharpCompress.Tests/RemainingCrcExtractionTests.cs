using System;
using System.IO;
using SharpCompress.Common;
using SharpCompress.Compressors.LZMA;
using SharpCompress.Readers;
using Xunit;

namespace SharpCompress.Test;

public class RemainingCrcExtractionTests : TestBase
{
    [Theory]
    [InlineData("Arj.store.arj", "This")]
    [InlineData("Ace.store.ace", "This")]
    [InlineData("Arc.uncompressed.arc", "This")]
    public void Reader_WriteEntryToFile_Throws_On_Checksum_Mismatch(
        string archiveName,
        string payloadMarker
    )
    {
        using var stream = new MemoryStream(ReadCorruptedArchive(archiveName, payloadMarker));
        using var reader = ReaderFactory.OpenReader(stream);
        var destination = Path.Combine(SCRATCH_FILES_PATH, Guid.NewGuid().ToString());
        Directory.CreateDirectory(destination);

        Assert.Throws<InvalidFormatException>(() => reader.WriteAllToDirectory(destination));
    }

    [Theory]
    [InlineData("Arj.store.arj", "This")]
    [InlineData("Ace.store.ace", "This")]
    [InlineData("Arc.uncompressed.arc", "This")]
    public void Reader_WriteEntryToFile_Skips_Checksum_When_CheckCrc_Is_False(
        string archiveName,
        string payloadMarker
    )
    {
        using var stream = new MemoryStream(ReadCorruptedArchive(archiveName, payloadMarker));
        using var reader = ReaderFactory.OpenReader(stream);
        var destination = Path.Combine(SCRATCH_FILES_PATH, Guid.NewGuid().ToString());
        Directory.CreateDirectory(destination);

        reader.WriteAllToDirectory(destination, new ExtractionOptions { CheckCrc = false });

        Assert.True(Directory.GetFiles(destination, "*", SearchOption.AllDirectories).Length > 0);
    }

    [Fact]
    public void LZipStream_Throws_On_Trailer_Crc_Mismatch()
    {
        var bytes = File.ReadAllBytes(Path.Combine(TEST_ARCHIVES_PATH, "Tar.tar.lz"));
        bytes[^20] ^= 1;
        using var stream = LZipStream.Create(
            new MemoryStream(bytes),
            SharpCompress.Compressors.CompressionMode.Decompress
        );
        using var output = new MemoryStream();

        Assert.Throws<InvalidFormatException>(() => stream.CopyTo(output));
    }

    private static byte[] ReadCorruptedArchive(string archiveName, string payloadMarker)
    {
        var bytes = File.ReadAllBytes(Path.Combine(TEST_ARCHIVES_PATH, archiveName));
        var marker = System.Text.Encoding.ASCII.GetBytes(payloadMarker);
        var offset = bytes.AsSpan().IndexOf(marker);
        if (offset < 0)
        {
            throw new InvalidOperationException($"Payload marker '{payloadMarker}' was not found.");
        }

        bytes[offset] ^= 1;
        return bytes;
    }
}
