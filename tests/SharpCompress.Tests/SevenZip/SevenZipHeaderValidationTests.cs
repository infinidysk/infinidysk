using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SharpCompress.Archives.SevenZip;
using SharpCompress.Common;
using SharpCompress.Crypto;
using Xunit;

namespace SharpCompress.Test.SevenZip;

public class SevenZipHeaderValidationTests : TestBase
{
    private static byte[] ReadArchiveBytes(string fileName) =>
        File.ReadAllBytes(Path.Combine(TEST_ARCHIVES_PATH, fileName));

    [Fact]
    public void OpenArchive_Throws_On_Invalid_Signature()
    {
        var bytes = ReadArchiveBytes("7Zip.LZMA.7z");
        bytes[0] ^= 0xFF;

        using var stream = new MemoryStream(bytes);
        using var archive = SevenZipArchive.OpenArchive(stream);

        var exception = Assert.Throws<InvalidFormatException>(() => archive.Entries.ToList());

        Assert.Contains("signature", exception.Message, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OpenArchive_Throws_On_StartHeaderCrc_Mismatch()
    {
        var bytes = ReadArchiveBytes("7Zip.LZMA.7z");
        // StartHeaderCRC is at bytes 8–11
        bytes[8] ^= 0xFF;

        using var stream = new MemoryStream(bytes);
        using var archive = SevenZipArchive.OpenArchive(stream);

        var exception = Assert.Throws<InvalidFormatException>(() => archive.Entries.ToList());

        Assert.Equal("7z start header CRC mismatch", exception.Message);
    }

    [Fact]
    public void OpenArchive_Throws_On_NextHeaderCrc_Mismatch()
    {
        var bytes = ReadArchiveBytes("7Zip.LZMA.7z");
        // Corrupt NextHeaderCRC (bytes 28–31) and recompute StartHeaderCRC so only the
        // next-header check fails during ReadDatabase.
        bytes[28] ^= 0xFF;
        FixStartHeaderCrc(bytes);

        using var stream = new MemoryStream(bytes);
        using var archive = SevenZipArchive.OpenArchive(stream);

        var exception = Assert.Throws<InvalidFormatException>(() => archive.Entries.ToList());

        Assert.Equal("7z next header CRC mismatch", exception.Message);
    }

    [Fact]
    public async Task OpenAsyncArchive_Throws_On_StartHeaderCrc_Mismatch()
    {
        var bytes = ReadArchiveBytes("7Zip.LZMA.7z");
        bytes[8] ^= 0xFF;

        await using var stream = new MemoryStream(bytes);
        await using var archive = await SevenZipArchive.OpenAsyncArchive(stream);

        var exception = await Assert.ThrowsAsync<InvalidFormatException>(async () =>
            await archive.EntriesAsync.ToListAsync()
        );

        Assert.Equal("7z start header CRC mismatch", exception.Message);
    }

    private static void FixStartHeaderCrc(byte[] header)
    {
        var startHeader = new byte[20];
        System.Buffer.BlockCopy(header, 12, startHeader, 0, 20);
        var crc = Crc32Stream.Compute(startHeader);
        header[8] = (byte)crc;
        header[9] = (byte)(crc >> 8);
        header[10] = (byte)(crc >> 16);
        header[11] = (byte)(crc >> 24);
    }
}
