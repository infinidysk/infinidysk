using System;
using System.IO;
using System.Linq;
using System.Text;
using SharpCompress.Archives.SevenZip;
using SharpCompress.Common;
using SharpCompress.Common.SevenZip;
using SharpCompress.Crypto;
using SharpCompress.Writers;
using SharpCompress.Writers.SevenZip;
using Xunit;

namespace SharpCompress.Test.SevenZip;

public class SevenZipWriterTests : TestBase
{
    [Theory]
    [InlineData(CompressionType.LZMA)]
    [InlineData(CompressionType.LZMA2)]
    public void SevenZipWriter_Solid_ThreeFiles_RoundTrip(CompressionType compressionType)
    {
        var files = new[]
        {
            ("first.txt", "The quick brown fox jumps over the lazy dog. 111111111"),
            ("second.txt", "Solid compression shares one folder across files. 22222"),
            ("third.txt", "Repeated similar text compresses better when solid. 333"),
        };

        using var archiveStream = new MemoryStream();

        using (
            var writer = new SevenZipWriter(
                archiveStream,
                new SevenZipWriterOptions(compressionType) { Solid = true }
            )
        )
        {
            foreach (var (name, text) in files)
            {
                using var source = new MemoryStream(Encoding.UTF8.GetBytes(text));
                writer.Write(name, source, DateTime.UtcNow);
            }
        }

        archiveStream.Position = 0;
        using var archive = (SevenZipArchive)SevenZipArchive.OpenArchive(archiveStream);

        var entries = archive.Entries.Where(e => !e.IsDirectory).ToList();
        Assert.Equal(files.Length, entries.Count);

        // Solid layout: exactly one folder shared by all three files.
        Assert.True(archive.IsSolid);
        var folderGroups = entries.GroupBy(e => e.FilePart.Folder).ToList();
        Assert.Single(folderGroups);
        Assert.Equal(3, folderGroups[0].Count());

        foreach (var (name, text) in files)
        {
            var entry = entries.First(e => e.Key == name);
            var expected = Encoding.UTF8.GetBytes(text);

            Assert.Equal(expected.Length, (int)entry.Size);
            Assert.Equal(Crc32Stream.Compute(expected), (uint)entry.Crc);

            using var output = new MemoryStream();
            using (var entryStream = entry.OpenEntryStream())
            {
                entryStream.CopyTo(output);
            }
            Assert.Equal(expected, output.ToArray());
        }
    }

    [Fact]
    public void SevenZipWriter_Solid_EmptyFilesAndDirectoriesStayOutsideFolder()
    {
        using var archiveStream = new MemoryStream();

        using (
            var writer = new SevenZipWriter(
                archiveStream,
                new SevenZipWriterOptions { Solid = true }
            )
        )
        {
            writer.WriteDirectory("dir", DateTime.UtcNow);
            using (var empty = new MemoryStream())
            {
                writer.Write("dir/empty.txt", empty, DateTime.UtcNow);
            }
            using (var a = new MemoryStream("alpha content in solid folder"u8.ToArray()))
            {
                writer.Write("dir/a.txt", a, DateTime.UtcNow);
            }
            using (var b = new MemoryStream("beta content in solid folder"u8.ToArray()))
            {
                writer.Write("dir/b.txt", b, DateTime.UtcNow);
            }
        }

        archiveStream.Position = 0;
        using var archive = (SevenZipArchive)SevenZipArchive.OpenArchive(archiveStream);

        // Only the two non-empty files belong to the single solid folder.
        var nonEmpty = archive.Entries.Where(e => !e.IsDirectory && e.Size > 0).ToList();
        var folderGroups = nonEmpty.GroupBy(e => e.FilePart.Folder).ToList();
        Assert.Single(folderGroups);
        Assert.Equal(2, folderGroups[0].Count());

        var dir = archive.Entries.First(e => e.IsDirectory);
        Assert.Equal("dir", dir.Key);

        var emptyEntry = archive.Entries.First(e => e.Key == "dir/empty.txt");
        Assert.Equal(0, (int)emptyEntry.Size);
        Assert.Null(emptyEntry.FilePart.Folder);

        var aEntry = archive.Entries.First(e => e.Key == "dir/a.txt");
        using var output = new MemoryStream();
        using (var entryStream = aEntry.OpenEntryStream())
        {
            entryStream.CopyTo(output);
        }
        Assert.Equal("alpha content in solid folder", Encoding.UTF8.GetString(output.ToArray()));
    }

    [Fact]
    public void SevenZipWriter_SingleFile_RoundTrip()
    {
        var content = "Hello, 7z world! This is a test of the SevenZipWriter."u8.ToArray();

        using var archiveStream = new MemoryStream();

        // Write archive
        using (var writer = new SevenZipWriter(archiveStream, new SevenZipWriterOptions()))
        {
            using var source = new MemoryStream(content);
            writer.Write("test.txt", source, DateTime.UtcNow);
        }

        // Read back and verify
        archiveStream.Position = 0;
        using (var archive = (SevenZipArchive)SevenZipArchive.OpenArchive(archiveStream))
        {
            var entries = archive.Entries.Where(e => !e.IsDirectory).ToList();
            Assert.Single(entries);
            Assert.Equal("test.txt", entries[0].Key);
            Assert.Equal(content.Length, (int)entries[0].Size);

            using var output = new MemoryStream();
            using (var entryStream = entries[0].OpenEntryStream())
            {
                entryStream.CopyTo(output);
            }
            Assert.Equal(content, output.ToArray());
        }
    }

    [Fact]
    public void SevenZipWriter_MultipleFiles_RoundTrip()
    {
        var files = new[]
        {
            ("file1.txt", "Content of file 1"),
            ("subdir/file2.txt", "Content of file 2 in subdirectory"),
            ("file3.bin", "Some binary-ish content with special bytes"),
        };

        using var archiveStream = new MemoryStream();

        // Write archive
        using (var writer = new SevenZipWriter(archiveStream, new SevenZipWriterOptions()))
        {
            foreach (var (name, text) in files)
            {
                using var source = new MemoryStream(Encoding.UTF8.GetBytes(text));
                writer.Write(name, source, DateTime.UtcNow);
            }
        }

        // Read back and verify
        archiveStream.Position = 0;
        using (var archive = (SevenZipArchive)SevenZipArchive.OpenArchive(archiveStream))
        {
            var entries = archive.Entries.Where(e => !e.IsDirectory).ToList();
            Assert.Equal(files.Length, entries.Count);

            for (var i = 0; i < files.Length; i++)
            {
                var entry = entries.First(e => e.Key == files[i].Item1);
                using var output = new MemoryStream();
                using (var entryStream = entry.OpenEntryStream())
                {
                    entryStream.CopyTo(output);
                }
                var extractedText = Encoding.UTF8.GetString(output.ToArray());
                Assert.Equal(files[i].Item2, extractedText);
            }
        }
    }

    [Fact]
    public void SevenZipWriter_WithDirectory_RoundTrip()
    {
        using var archiveStream = new MemoryStream();

        // Write archive with directory and file
        using (var writer = new SevenZipWriter(archiveStream, new SevenZipWriterOptions()))
        {
            writer.WriteDirectory("mydir", DateTime.UtcNow);

            using var source = new MemoryStream("file inside dir"u8.ToArray());
            writer.Write("mydir/data.txt", source, DateTime.UtcNow);
        }

        // Read back and verify
        archiveStream.Position = 0;
        using (var archive = (SevenZipArchive)SevenZipArchive.OpenArchive(archiveStream))
        {
            var allEntries = archive.Entries.ToList();
            Assert.Equal(2, allEntries.Count);

            var dirEntry = allEntries.FirstOrDefault(e => e.IsDirectory);
            Assert.NotNull(dirEntry);

            var fileEntry = allEntries.FirstOrDefault(e => !e.IsDirectory);
            Assert.NotNull(fileEntry);
            Assert.Equal("mydir/data.txt", fileEntry!.Key);

            using var output = new MemoryStream();
            using (var entryStream = fileEntry.OpenEntryStream())
            {
                entryStream.CopyTo(output);
            }
            Assert.Equal("file inside dir", Encoding.UTF8.GetString(output.ToArray()));
        }
    }

    [Fact]
    public void SevenZipWriter_EmptyFile_RoundTrip()
    {
        using var archiveStream = new MemoryStream();

        // Write archive with an empty file
        using (var writer = new SevenZipWriter(archiveStream, new SevenZipWriterOptions()))
        {
            using var source = new MemoryStream();
            writer.Write("empty.txt", source, DateTime.UtcNow);

            using var source2 = new MemoryStream("not empty"u8.ToArray());
            writer.Write("notempty.txt", source2, DateTime.UtcNow);
        }

        // Read back and verify
        archiveStream.Position = 0;
        using (var archive = (SevenZipArchive)SevenZipArchive.OpenArchive(archiveStream))
        {
            var entries = archive.Entries.Where(e => !e.IsDirectory).ToList();
            Assert.Equal(2, entries.Count);

            var emptyEntry = entries.First(e => e.Key == "empty.txt");
            Assert.Equal(0, (int)emptyEntry.Size);

            var nonEmptyEntry = entries.First(e => e.Key == "notempty.txt");
            using var output = new MemoryStream();
            using (var entryStream = nonEmptyEntry.OpenEntryStream())
            {
                entryStream.CopyTo(output);
            }
            Assert.Equal("not empty", Encoding.UTF8.GetString(output.ToArray()));
        }
    }

    [Fact]
    public void SevenZipWriter_LZMA2_SingleFile_RoundTrip()
    {
        var content =
            "Hello, LZMA2 world! This is a test of LZMA2 encoding in the SevenZipWriter."u8.ToArray();

        using var archiveStream = new MemoryStream();

        using (
            var writer = new SevenZipWriter(
                archiveStream,
                new SevenZipWriterOptions(CompressionType.LZMA2)
            )
        )
        {
            using var source = new MemoryStream(content);
            writer.Write("test.txt", source, DateTime.UtcNow);
        }

        archiveStream.Position = 0;
        using (var archive = (SevenZipArchive)SevenZipArchive.OpenArchive(archiveStream))
        {
            var entries = archive.Entries.Where(e => !e.IsDirectory).ToList();
            Assert.Single(entries);
            Assert.Equal("test.txt", entries[0].Key);
            Assert.Equal(content.Length, (int)entries[0].Size);

            using var output = new MemoryStream();
            using (var entryStream = entries[0].OpenEntryStream())
            {
                entryStream.CopyTo(output);
            }
            Assert.Equal(content, output.ToArray());
        }
    }

    [Fact]
    public void SevenZipWriter_LZMA2_MultipleFiles_RoundTrip()
    {
        var files = new[]
        {
            ("file1.txt", "Content of file 1 for LZMA2 testing"),
            ("subdir/file2.txt", "Content of file 2 in subdirectory for LZMA2"),
            ("file3.bin", "Some binary-ish content with special bytes for LZMA2 testing"),
        };

        using var archiveStream = new MemoryStream();

        using (
            var writer = new SevenZipWriter(
                archiveStream,
                new SevenZipWriterOptions(CompressionType.LZMA2)
            )
        )
        {
            foreach (var (name, text) in files)
            {
                using var source = new MemoryStream(Encoding.UTF8.GetBytes(text));
                writer.Write(name, source, DateTime.UtcNow);
            }
        }

        archiveStream.Position = 0;
        using (var archive = (SevenZipArchive)SevenZipArchive.OpenArchive(archiveStream))
        {
            var entries = archive.Entries.Where(e => !e.IsDirectory).ToList();
            Assert.Equal(files.Length, entries.Count);

            for (var i = 0; i < files.Length; i++)
            {
                var entry = entries.First(e => e.Key == files[i].Item1);
                using var output = new MemoryStream();
                using (var entryStream = entry.OpenEntryStream())
                {
                    entryStream.CopyTo(output);
                }
                var extractedText = Encoding.UTF8.GetString(output.ToArray());
                Assert.Equal(files[i].Item2, extractedText);
            }
        }
    }

    [Fact]
    public void SevenZipWriter_LZMA2_LargerFile_RoundTrip()
    {
        // Create 3MB of repeating pattern data - forces multi-chunk in LZMA2
        var content = new byte[3 * 1024 * 1024];
        var pattern = Encoding.UTF8.GetBytes(
            "This is a repeating pattern for LZMA2 compression testing. "
        );
        for (var i = 0; i < content.Length; i++)
        {
            content[i] = pattern[i % pattern.Length];
        }

        using var archiveStream = new MemoryStream();

        using (
            var writer = new SevenZipWriter(
                archiveStream,
                new SevenZipWriterOptions(CompressionType.LZMA2)
            )
        )
        {
            using var source = new MemoryStream(content);
            writer.Write("large.bin", source, DateTime.UtcNow);
        }

        Assert.True(
            archiveStream.Length < content.Length,
            "Archive should be smaller than uncompressed data"
        );

        archiveStream.Position = 0;
        using (var archive = (SevenZipArchive)SevenZipArchive.OpenArchive(archiveStream))
        {
            var entries = archive.Entries.Where(e => !e.IsDirectory).ToList();
            Assert.Single(entries);
            Assert.Equal(content.Length, (int)entries[0].Size);

            using var output = new MemoryStream();
            using (var entryStream = entries[0].OpenEntryStream())
            {
                entryStream.CopyTo(output);
            }
            Assert.Equal(content, output.ToArray());
        }
    }

    [Fact]
    public void SevenZipWriter_LZMA2_IncompressibleData_RoundTrip()
    {
        // Random bytes - forces uncompressed fallback in LZMA2
        var content = new byte[100 * 1024];
        var rng = new Random(42);
        rng.NextBytes(content);

        using var archiveStream = new MemoryStream();

        using (
            var writer = new SevenZipWriter(
                archiveStream,
                new SevenZipWriterOptions(CompressionType.LZMA2)
            )
        )
        {
            using var source = new MemoryStream(content);
            writer.Write("random.bin", source, DateTime.UtcNow);
        }

        archiveStream.Position = 0;
        using (var archive = (SevenZipArchive)SevenZipArchive.OpenArchive(archiveStream))
        {
            var entries = archive.Entries.Where(e => !e.IsDirectory).ToList();
            Assert.Single(entries);
            Assert.Equal(content.Length, (int)entries[0].Size);

            using var output = new MemoryStream();
            using (var entryStream = entries[0].OpenEntryStream())
            {
                entryStream.CopyTo(output);
            }
            Assert.Equal(content, output.ToArray());
        }
    }

    [Fact]
    public void SevenZipWriter_UnsupportedCompressionType_Throws()
    {
        Assert.Throws<ArgumentException>(() => new SevenZipWriterOptions(CompressionType.Deflate));
    }

    [Fact]
    public void SevenZipWriter_UncompressedHeader_RoundTrip()
    {
        var content = "Testing with uncompressed header"u8.ToArray();

        using var archiveStream = new MemoryStream();

        // Write archive with uncompressed header
        using (
            var writer = new SevenZipWriter(
                archiveStream,
                new SevenZipWriterOptions { CompressHeader = false }
            )
        )
        {
            using var source = new MemoryStream(content);
            writer.Write("rawheader.txt", source, DateTime.UtcNow);
        }

        // Read back and verify
        archiveStream.Position = 0;
        using (var archive = (SevenZipArchive)SevenZipArchive.OpenArchive(archiveStream))
        {
            var entries = archive.Entries.Where(e => !e.IsDirectory).ToList();
            Assert.Single(entries);

            using var output = new MemoryStream();
            using (var entryStream = entries[0].OpenEntryStream())
            {
                entryStream.CopyTo(output);
            }
            Assert.Equal(content, output.ToArray());
        }
    }

    [Fact]
    public void SevenZipWriter_ViaWriterFactory()
    {
        var content = "Factory-created archive"u8.ToArray();

        using var archiveStream = new MemoryStream();

        // Write via WriterFactory
        using (
            var writer = WriterFactory.OpenWriter(
                archiveStream,
                ArchiveType.SevenZip,
                new SevenZipWriterOptions()
            )
        )
        {
            using var source = new MemoryStream(content);
            writer.Write("factory.txt", source, DateTime.UtcNow);
        }

        // Read back and verify
        archiveStream.Position = 0;
        using (var archive = (SevenZipArchive)SevenZipArchive.OpenArchive(archiveStream))
        {
            var entries = archive.Entries.Where(e => !e.IsDirectory).ToList();
            Assert.Single(entries);

            using var output = new MemoryStream();
            using (var entryStream = entries[0].OpenEntryStream())
            {
                entryStream.CopyTo(output);
            }
            Assert.Equal(content, output.ToArray());
        }
    }

    [Fact]
    public void SevenZipWriter_LargerFile_RoundTrip()
    {
        // Create 100KB of repeating pattern data (compresses well)
        var content = new byte[100 * 1024];
        var pattern = Encoding.UTF8.GetBytes(
            "This is a repeating pattern for compression testing. "
        );
        for (var i = 0; i < content.Length; i++)
        {
            content[i] = pattern[i % pattern.Length];
        }

        using var archiveStream = new MemoryStream();

        // Write archive
        using (var writer = new SevenZipWriter(archiveStream, new SevenZipWriterOptions()))
        {
            using var source = new MemoryStream(content);
            writer.Write("large.bin", source, DateTime.UtcNow);
        }

        // Verify compressed size is smaller than original
        Assert.True(
            archiveStream.Length < content.Length,
            "Archive should be smaller than uncompressed data"
        );

        // Read back and verify
        archiveStream.Position = 0;
        using (var archive = (SevenZipArchive)SevenZipArchive.OpenArchive(archiveStream))
        {
            var entries = archive.Entries.Where(e => !e.IsDirectory).ToList();
            Assert.Single(entries);
            Assert.Equal(content.Length, (int)entries[0].Size);

            using var output = new MemoryStream();
            using (var entryStream = entries[0].OpenEntryStream())
            {
                entryStream.CopyTo(output);
            }
            Assert.Equal(content, output.ToArray());
        }
    }

    [Fact]
    public void SevenZipWriter_RequiresSeekableStream()
    {
        var nonSeekable = new NonSeekableStream();
        Assert.Throws<ArchiveOperationException>(() =>
            new SevenZipWriter(nonSeekable, new SevenZipWriterOptions())
        );
    }

    private class NonSeekableStream : MemoryStream
    {
        public override bool CanSeek => false;
    }
}
