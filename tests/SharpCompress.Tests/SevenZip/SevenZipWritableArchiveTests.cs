using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using SharpCompress;
using SharpCompress.Archives;
using SharpCompress.Archives.SevenZip;
using SharpCompress.Common;
using SharpCompress.Writers.SevenZip;
using Xunit;

namespace SharpCompress.Test.SevenZip;

public class SevenZipWritableArchiveTests : TestBase
{
    private const string FixtureArchive = "7Zip.LZMA2.7z";

    private static byte[] ReadAll(IArchiveEntry entry)
    {
        using var output = new MemoryStream();
        using var entryStream = entry.OpenEntryStream();
        entryStream.CopyTo(output);
        return output.ToArray();
    }

    [Fact]
    public void SevenZipWritableArchive_CreateNew_AddTwoEntries_RoundTrip()
    {
        var content1 = "First writable-archive entry contents."u8.ToArray();
        var content2 = "Second entry so the archive holds more than one file."u8.ToArray();

        using var archiveStream = new MemoryStream();
        using (var archive = SevenZipArchive.CreateArchive())
        {
            archive.AddEntry("first.txt", new MemoryStream(content1), true, content1.Length);
            archive.AddEntry("dir/second.txt", new MemoryStream(content2), true, content2.Length);
            archive.SaveTo(archiveStream, new SevenZipWriterOptions(CompressionType.LZMA2));
        }

        archiveStream.Position = 0;
        using var result = SevenZipArchive.OpenArchive(archiveStream);
        var files = result.Entries.Where(e => !e.IsDirectory).ToList();

        Assert.Equal(2, files.Count);
        Assert.Equal(content1, ReadAll(files.First(e => e.Key == "first.txt")));
        Assert.Equal(content2, ReadAll(files.First(e => e.Key == "dir/second.txt")));
    }

    [Fact]
    public void SevenZipWritableArchive_ModifyExisting_RemoveAndAdd_RoundTrip()
    {
        var testArchive = Path.Combine(TEST_ARCHIVES_PATH, FixtureArchive);
        var newContent = "Freshly added entry during modify-existing test."u8.ToArray();

        using var outputStream = new MemoryStream();
        string removedKey;
        Dictionary<string, byte[]> survivingFiles;

        using (var archive = SevenZipArchive.OpenArchive(testArchive))
        {
            var fileEntries = archive.Entries.Where(e => !e.IsDirectory).ToList();
            Assert.NotEmpty(fileEntries);

            var toRemove = fileEntries[0];
            removedKey = toRemove.Key.NotNull();
            survivingFiles = fileEntries.Skip(1).ToDictionary(e => e.Key.NotNull(), ReadAll);

            archive.RemoveEntry(toRemove);
            archive.AddEntry(
                "added/new.txt",
                new MemoryStream(newContent),
                true,
                newContent.Length
            );
            archive.SaveTo(outputStream, new SevenZipWriterOptions(CompressionType.LZMA2));
        }

        outputStream.Position = 0;
        using var result = SevenZipArchive.OpenArchive(outputStream);
        var resultFiles = result.Entries.Where(e => !e.IsDirectory).ToList();

        Assert.DoesNotContain(resultFiles, e => e.Key == removedKey);
        Assert.Contains(resultFiles, e => e.Key == "added/new.txt");
        Assert.Equal(newContent, ReadAll(resultFiles.First(e => e.Key == "added/new.txt")));

        foreach (var (key, expected) in survivingFiles)
        {
            Assert.Equal(expected, ReadAll(resultFiles.First(e => e.Key == key)));
        }
    }

    [Fact]
    public async ValueTask SevenZipWritableArchive_CreateNew_AddTwoEntries_RoundTrip_Async()
    {
        var content1 = "First async writable-archive entry."u8.ToArray();
        var content2 = "Second async entry contents here."u8.ToArray();

        using var archiveStream = new MemoryStream();
        await using (var archive = await SevenZipArchive.CreateAsyncArchive())
        {
            await archive.AddEntryAsync(
                "first.txt",
                new MemoryStream(content1),
                true,
                content1.Length
            );
            await archive.AddEntryAsync(
                "dir/second.txt",
                new MemoryStream(content2),
                true,
                content2.Length
            );
            await archive.SaveToAsync(
                archiveStream,
                new SevenZipWriterOptions(CompressionType.LZMA2)
            );
        }

        archiveStream.Position = 0;
        using var result = SevenZipArchive.OpenArchive(archiveStream);
        var files = result.Entries.Where(e => !e.IsDirectory).ToList();

        Assert.Equal(2, files.Count);
        Assert.Equal(content1, ReadAll(files.First(e => e.Key == "first.txt")));
        Assert.Equal(content2, ReadAll(files.First(e => e.Key == "dir/second.txt")));
    }

    [Fact]
    public async ValueTask SevenZipWritableArchive_ModifyExisting_RemoveAndAdd_RoundTrip_Async()
    {
        var testArchive = Path.Combine(TEST_ARCHIVES_PATH, FixtureArchive);
        var newContent = "Async freshly added entry during modify-existing test."u8.ToArray();

        using var outputStream = new MemoryStream();
        string removedKey;
        var survivingFiles = new Dictionary<string, byte[]>();

        await using (var archive = await SevenZipArchive.OpenAsyncArchive(testArchive))
        {
            var fileEntries = new List<IArchiveEntry>();
            await foreach (var entry in archive.EntriesAsync)
            {
                if (!entry.IsDirectory)
                {
                    fileEntries.Add(entry);
                }
            }
            Assert.NotEmpty(fileEntries);

            var toRemove = fileEntries[0];
            removedKey = toRemove.Key.NotNull();
            foreach (var entry in fileEntries.Skip(1))
            {
                survivingFiles[entry.Key.NotNull()] = ReadAll(entry);
            }

            await archive.RemoveEntryAsync(toRemove);
            await archive.AddEntryAsync(
                "added/new.txt",
                new MemoryStream(newContent),
                true,
                newContent.Length
            );
            await archive.SaveToAsync(
                outputStream,
                new SevenZipWriterOptions(CompressionType.LZMA2)
            );
        }

        outputStream.Position = 0;
        using var result = SevenZipArchive.OpenArchive(outputStream);
        var resultFiles = result.Entries.Where(e => !e.IsDirectory).ToList();

        Assert.DoesNotContain(resultFiles, e => e.Key == removedKey);
        Assert.Contains(resultFiles, e => e.Key == "added/new.txt");
        Assert.Equal(newContent, ReadAll(resultFiles.First(e => e.Key == "added/new.txt")));

        foreach (var (key, expected) in survivingFiles)
        {
            Assert.Equal(expected, ReadAll(resultFiles.First(e => e.Key == key)));
        }
    }
}
