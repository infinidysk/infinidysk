using System.Collections.Generic;
using System.IO;
using SharpCompress.Readers;
using SharpCompress.Test.Mocks;
using Xunit;

namespace SharpCompress.Test.Zip;

/// <summary>
/// Tests for ZIP reading with streams that return short reads.
/// Reproduces the regression where ZIP parsing fails depending on Stream.Read chunking patterns.
/// </summary>
public class ZipShortReadTests : ReaderTests
{
    /// <summary>
    /// Test that ZIP reading works correctly with short reads on non-seekable streams.
    /// Uses a test archive and different chunking patterns.
    /// </summary>
    [Theory]
    [InlineData("Zip.deflate.zip", 1000, 4096)]
    [InlineData("Zip.deflate.zip", 999, 4096)]
    [InlineData("Zip.deflate.zip", 100, 4096)]
    [InlineData("Zip.deflate.zip", 50, 512)]
    [InlineData("Zip.deflate.zip", 1, 1)] // Extreme case: 1 byte at a time
    [InlineData("Zip.deflate.dd.zip", 1000, 4096)]
    [InlineData("Zip.deflate.dd.zip", 999, 4096)]
    [InlineData("Zip.zip64.zip", 3816, 4096)]
    [InlineData("Zip.zip64.zip", 3815, 4096)] // Similar to the issue pattern
    public void Zip_Reader_Handles_Short_Reads(string zipFile, int firstReadSize, int chunkSize)
    {
        // Use an existing test ZIP file
        var zipPath = Path.Combine(TEST_ARCHIVES_PATH, zipFile);
        if (!File.Exists(zipPath))
        {
            return; // Skip if file doesn't exist
        }

        var bytes = File.ReadAllBytes(zipPath);

        // Baseline with MemoryStream (seekable, no short reads)
        var baseline = ReadEntriesFromStream(new MemoryStream(bytes, writable: false));
        Assert.NotEmpty(baseline);

        // Non-seekable stream with controlled short read pattern
        var chunked = ReadEntriesFromStream(
            ChunkyReadStream.FromBytes(bytes, chunkSize, firstReadSize)
        );
        Assert.Equal(baseline, chunked);
    }

    private List<string> ReadEntriesFromStream(Stream stream)
    {
        var names = new List<string>();
        using var reader = ReaderFactory.OpenReader(
            stream,
            ReaderOptions.ForExternalStream with
            {
                LeaveStreamOpen = true,
            }
        );

        while (reader.MoveToNextEntry())
        {
            if (reader.Entry.IsDirectory)
            {
                continue;
            }

            names.Add(reader.Entry.Key!);

            using var entryStream = reader.OpenEntryStream();
            entryStream.CopyTo(Stream.Null);
        }

        return names;
    }
}
