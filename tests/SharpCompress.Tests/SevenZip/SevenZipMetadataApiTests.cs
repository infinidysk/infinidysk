using System.IO;
using System.Linq;
using SharpCompress.Archives.SevenZip;
using SharpCompress.Common;
using SharpCompress.Readers;
using Xunit;

namespace SharpCompress.Test.SevenZip;

/// <summary>
/// Covers the public 7z packed-byte-range / AES metadata APIs and the AES-first
/// <c>CompressionType</c> fix (issue #118).
/// </summary>
public class SevenZipMetadataApiTests : TestBase
{
    private const string Password = "testpassword";

    [Fact]
    public void StoredCopy_TryGetPackedByteRange_SlicesExactPayload()
    {
        var path = Path.Combine(TEST_ARCHIVES_PATH, "7Zip.Copy.7z");
        using var archive = (SevenZipArchive)SevenZipArchive.OpenArchive(path);

        var any = false;
        foreach (var entry in archive.Entries.Where(e => !e.IsDirectory && e.Size > 0))
        {
            any = true;
            Assert.Equal(CompressionType.None, entry.CompressionType);
            Assert.Null(entry.AesCoderProperties);

            Assert.True(entry.TryGetPackedByteRange(out var start, out var length));
            Assert.Equal(entry.Size, length);

            var expected = ReadFully(entry);
            var actual = ReadSlice(path, start, length);
            Assert.Equal(expected, actual);
        }

        Assert.True(any, "expected at least one stored entry");
    }

    [Fact]
    public void AesCopy_ReportsNoneAndWholeFolderRange()
    {
        var path = Path.Combine(TEST_ARCHIVES_PATH, "7Zip.Copy.Aes.7z");
        using var archive = (SevenZipArchive)
            SevenZipArchive.OpenArchive(
                path,
                ReaderOptions.ForFilePath with
                {
                    Password = Password,
                }
            );

        var entry = archive.Entries.First(e => !e.IsDirectory && e.Size > 0);

        Assert.True(entry.IsEncrypted);
        // AES-first coder chain must not throw; AES + Copy resolves to None.
        Assert.Equal(CompressionType.None, entry.CompressionType);
        Assert.NotNull(entry.AesCoderProperties);

        Assert.True(entry.TryGetPackedByteRange(out var start, out var length));
        Assert.Equal(entry.FolderStartOffset, start);
        // Whole folder pack range including AES block padding.
        Assert.True(length >= entry.Size);
        Assert.Equal(0, length % 16);
    }

    [Theory]
    [InlineData("7Zip.LZMA.Aes.7z")]
    [InlineData("7Zip.LZMA2.Aes.7z")]
    public void AesLzma_ReportsLzma_And_NoContiguousRange(string archiveName)
    {
        var path = Path.Combine(TEST_ARCHIVES_PATH, archiveName);
        using var archive = (SevenZipArchive)
            SevenZipArchive.OpenArchive(
                path,
                ReaderOptions.ForFilePath with
                {
                    Password = Password,
                }
            );

        var entry = archive.Entries.First(e => !e.IsDirectory && e.Size > 0);

        Assert.True(entry.IsEncrypted);
        Assert.Equal(CompressionType.LZMA, entry.CompressionType);
        Assert.NotNull(entry.AesCoderProperties);
        Assert.False(entry.TryGetPackedByteRange(out _, out _));
    }

    [Theory]
    [InlineData("7Zip.LZMA.7z")]
    [InlineData("7Zip.solid.7z")]
    public void Compressed_HasNoContiguousRange(string archiveName)
    {
        var path = Path.Combine(TEST_ARCHIVES_PATH, archiveName);
        using var archive = (SevenZipArchive)SevenZipArchive.OpenArchive(path);

        foreach (var entry in archive.Entries.Where(e => !e.IsDirectory && e.Size > 0))
        {
            Assert.False(entry.TryGetPackedByteRange(out _, out _));
            Assert.Null(entry.AesCoderProperties);
        }
    }

    private static byte[] ReadFully(SevenZipArchiveEntry entry)
    {
        using var s = entry.OpenEntryStream();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    private static byte[] ReadSlice(string path, long start, long length)
    {
        using var fs = File.OpenRead(path);
        fs.Position = start;
        var buffer = new byte[length];
        fs.ReadExactly(buffer);
        return buffer;
    }
}
