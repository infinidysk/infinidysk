using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Archives.SevenZip;
using SharpCompress.Common;
using SharpCompress.Common.SevenZip;
using SharpCompress.Crypto;
using SharpCompress.Test.Mocks;
using SharpCompress.Writers;
using SharpCompress.Writers.SevenZip;
using Xunit;

namespace SharpCompress.Test.SevenZip;

public class SevenZipWriterAsyncTests : TestBase
{
    [Fact]
    public async ValueTask SevenZipWriter_Async_SingleFile_RoundTrip()
    {
        var content = "Hello, async 7z world!"u8.ToArray();

        using var archiveStream = new MemoryStream();

        await using (
            var writer = new SevenZipWriter(
                new AsyncOnlyStream(archiveStream),
                new SevenZipWriterOptions()
            )
        )
        {
            await writer.WriteAsync("test.txt", new MemoryStream(content), DateTime.UtcNow);
        }

        archiveStream.Position = 0;
        using var archive = (SevenZipArchive)SevenZipArchive.OpenArchive(archiveStream);
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

    [Fact]
    public async ValueTask SevenZipWriter_Async_WithDirectory_RoundTrip()
    {
        using var archiveStream = new MemoryStream();

        await using (
            var writer = new SevenZipWriter(
                new AsyncOnlyStream(archiveStream),
                new SevenZipWriterOptions(CompressionType.LZMA2)
            )
        )
        {
            await writer.WriteDirectoryAsync("mydir", DateTime.UtcNow);
            await writer.WriteAsync(
                "mydir/file1.txt",
                new MemoryStream(Encoding.UTF8.GetBytes("file one")),
                DateTime.UtcNow
            );
            await writer.WriteAsync(
                "mydir/file2.txt",
                new MemoryStream(Encoding.UTF8.GetBytes("file two")),
                DateTime.UtcNow
            );
        }

        archiveStream.Position = 0;
        using var archive = (SevenZipArchive)SevenZipArchive.OpenArchive(archiveStream);
        var entries = archive.Entries.ToList();

        Assert.Equal(3, entries.Count);
        Assert.Contains(entries, e => e.IsDirectory && e.Key == "mydir");
        Assert.Contains(entries, e => !e.IsDirectory && e.Key == "mydir/file1.txt");
        Assert.Contains(entries, e => !e.IsDirectory && e.Key == "mydir/file2.txt");
    }

    [Fact]
    public async ValueTask SevenZipWriter_Async_ViaWriterFactory()
    {
        var content = "Factory-created async archive"u8.ToArray();

        using var archiveStream = new MemoryStream();

        await using (
            var writer = await WriterFactory.OpenAsyncWriter(
                new AsyncOnlyStream(archiveStream),
                ArchiveType.SevenZip,
                new SevenZipWriterOptions()
            )
        )
        {
            await writer.WriteAsync("factory.txt", new MemoryStream(content), DateTime.UtcNow);
        }

        archiveStream.Position = 0;
        using var archive = (SevenZipArchive)SevenZipArchive.OpenArchive(archiveStream);
        var entry = archive.Entries.Single(e => !e.IsDirectory);

        using var output = new MemoryStream();
        using (var entryStream = entry.OpenEntryStream())
        {
            entryStream.CopyTo(output);
        }

        Assert.Equal("factory.txt", entry.Key);
        Assert.Equal(content, output.ToArray());
    }

    [Fact]
    public async ValueTask SevenZipWriter_Async_UsesAsyncSourceReads()
    {
        var content = "source stream supports async reads only"u8.ToArray();

        using var archiveStream = new MemoryStream();

        await using (var writer = new SevenZipWriter(archiveStream, new SevenZipWriterOptions()))
        {
            using var source = new AsyncOnlyStream(new MemoryStream(content));
            await writer.WriteAsync("async-source.txt", source, DateTime.UtcNow);
        }

        archiveStream.Position = 0;
        using var archive = (SevenZipArchive)SevenZipArchive.OpenArchive(archiveStream);
        var entry = archive.Entries.Single(e => !e.IsDirectory);

        using var output = new MemoryStream();
        using (var entryStream = entry.OpenEntryStream())
        {
            entryStream.CopyTo(output);
        }

        Assert.Equal("async-source.txt", entry.Key);
        Assert.Equal(content, output.ToArray());
    }

    [Fact]
    public async ValueTask SevenZipWriter_Async_Solid_ThreeFiles_RoundTrip()
    {
        var files = new[]
        {
            ("first.txt", "Async solid file one with some shared vocabulary here."),
            ("second.txt", "Async solid file two with some shared vocabulary here."),
            ("third.txt", "Async solid file three with some shared vocabulary here."),
        };

        using var archiveStream = new MemoryStream();

        await using (
            var writer = new SevenZipWriter(
                new AsyncOnlyStream(archiveStream),
                new SevenZipWriterOptions(CompressionType.LZMA2) { Solid = true }
            )
        )
        {
            foreach (var (name, text) in files)
            {
                await writer.WriteAsync(
                    name,
                    new MemoryStream(Encoding.UTF8.GetBytes(text)),
                    DateTime.UtcNow
                );
            }
        }

        archiveStream.Position = 0;
        using var archive = (SevenZipArchive)SevenZipArchive.OpenArchive(archiveStream);

        var entries = archive.Entries.Where(e => !e.IsDirectory).ToList();
        Assert.Equal(files.Length, entries.Count);

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
    public async ValueTask SevenZipWriter_Async_Cancelled_Throws()
    {
        using var archiveStream = new MemoryStream();
        await using var writer = new SevenZipWriter(archiveStream, new SevenZipWriterOptions());

        using var source = new MemoryStream("cancel me"u8.ToArray());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            writer.WriteAsync("cancel.txt", source, DateTime.UtcNow, cts.Token).AsTask()
        );
    }
}
