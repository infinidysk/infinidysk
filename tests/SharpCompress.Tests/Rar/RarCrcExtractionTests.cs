using System.IO;
using System.Linq;
using SharpCompress.Archives;
using SharpCompress.Archives.Rar;
using SharpCompress.Common;
using Xunit;

namespace SharpCompress.Test.Rar;

public class RarCrcExtractionTests : ArchiveTests
{
    [Theory]
    [InlineData("Rar.rar")]
    [InlineData("Rar5.rar")]
    public void Rar_Archive_WriteToFile_Throws_On_Crc_Mismatch(string archiveName)
    {
        using var archive = RarArchive.OpenArchive(Path.Combine(TEST_ARCHIVES_PATH, archiveName));
        var entry = CorruptFirstFileCrc(archive);
        var destination = Path.Combine(SCRATCH_FILES_PATH, $"{archiveName}-crc-mismatch.txt");

        Assert.Throws<InvalidFormatException>(() => entry.WriteToFile(destination));
    }

    private static RarArchiveEntry CorruptFirstFileCrc(IArchive archive)
    {
        var entry = archive.Entries.OfType<RarArchiveEntry>().First(e => !e.IsDirectory);
        CorruptCrc(entry);
        return entry;
    }

    private static void CorruptCrc(RarArchiveEntry entry)
    {
        var crc = entry.FileHeader.FileCrc.NotNull();
        crc[0] ^= 0xFF;
    }
}
