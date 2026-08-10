using System.IO;
using SharpCompress.Common;
using SharpCompress.Writers;
using SharpCompress.Writers.GZip;
using Xunit;

namespace SharpCompress.Test.GZip;

public class GZipWriterTests : WriterTests
{
    public GZipWriterTests()
        : base(ArchiveType.GZip) => UseExtensionInsteadOfNameToVerify = true;

    [Fact]
    public void GZip_Writer_Generic()
    {
        using (
            Stream stream = File.Open(
                Path.Join(SCRATCH_FILES_PATH, "Tar.tar.gz"),
                FileMode.OpenOrCreate,
                FileAccess.Write
            )
        )
        using (
            var writer = WriterFactory.OpenWriter(
                stream,
                ArchiveType.GZip,
                new WriterOptions(CompressionType.GZip)
            )
        )
        {
            writer.Write("Tar.tar", Path.Join(TEST_ARCHIVES_PATH, "Tar.tar"));
        }
        CompareArchivesByPath(
            Path.Join(SCRATCH_FILES_PATH, "Tar.tar.gz"),
            Path.Join(TEST_ARCHIVES_PATH, "Tar.tar.gz")
        );
    }

    [Fact]
    public void GZip_Writer()
    {
        using (
            Stream stream = File.Open(
                Path.Join(SCRATCH_FILES_PATH, "Tar.tar.gz"),
                FileMode.OpenOrCreate,
                FileAccess.Write
            )
        )
        using (var writer = new GZipWriter(stream))
        {
            writer.Write("Tar.tar", Path.Join(TEST_ARCHIVES_PATH, "Tar.tar"));
        }
        CompareArchivesByPath(
            Path.Join(SCRATCH_FILES_PATH, "Tar.tar.gz"),
            Path.Join(TEST_ARCHIVES_PATH, "Tar.tar.gz")
        );
    }

    [Fact]
    public void GZip_Writer_Generic_Bad_Compression() =>
        Assert.Throws<InvalidFormatException>(() =>
        {
            using Stream stream = File.OpenWrite(Path.Join(SCRATCH_FILES_PATH, "Tar.tar.gz"));
            using var writer = WriterFactory.OpenWriter(
                stream,
                ArchiveType.GZip,
                new WriterOptions(CompressionType.BZip2)
            );
        });

    [Fact]
    public void GZip_Writer_Entry_Path_With_Dir()
    {
        using (
            Stream stream = File.Open(
                Path.Join(SCRATCH_FILES_PATH, "Tar.tar.gz"),
                FileMode.OpenOrCreate,
                FileAccess.Write
            )
        )
        using (var writer = new GZipWriter(stream))
        {
            var path = Path.Join(TEST_ARCHIVES_PATH, "Tar.tar");
            writer.Write(path, path); //covers issue #532
        }
        CompareArchivesByPath(
            Path.Join(SCRATCH_FILES_PATH, "Tar.tar.gz"),
            Path.Join(TEST_ARCHIVES_PATH, "Tar.tar.gz")
        );
    }
}
