using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SharpCompress.Archives.Rar;
using SharpCompress.Common;
using SharpCompress.Compressors.Rar;
using Xunit;

namespace SharpCompress.Test.Rar;

public class RarStoredEntryStreamTests : ArchiveTests
{
    private static readonly string[] MultiNoneParts =
    [
        "Rar5.multi.none.part01.rar",
        "Rar5.multi.none.part02.rar",
        "Rar5.multi.none.part03.rar",
    ];

    [Theory]
    [InlineData("Rar.none.rar")]
    [InlineData("Rar5.none.rar")]
    public void Stored_SingleVolume_CanSeek_And_MatchesFullRead(string archiveName)
    {
        using var archive = RarArchive.OpenArchive(Path.Combine(TEST_ARCHIVES_PATH, archiveName));
        var entry = archive.Entries.First(e => !e.IsDirectory && e.Size > 0);
        var full = ReadFully(entry);

        using var stream = entry.OpenEntryStream();
        Assert.IsType<StoredRarEntryStream>(stream);
        Assert.True(stream.CanSeek);
        Assert.Equal(entry.Size, stream.Length);

        Assert.Equal(full, ReadFullyFromOpenStream(stream));
    }

    [Theory]
    [InlineData("Rar.none.rar")]
    [InlineData("Rar5.none.rar")]
    public void Stored_SingleVolume_SeekRead_MatchesSlice(string archiveName)
    {
        using var archive = RarArchive.OpenArchive(Path.Combine(TEST_ARCHIVES_PATH, archiveName));
        var entry = archive.Entries.First(e => !e.IsDirectory && e.Size > 0);
        var full = ReadFully(entry);
        long[] offsets = [0, 1, full.Length / 4, full.Length / 2, Math.Max(0, full.Length - 16)];

        using var stream = entry.OpenEntryStream();
        Assert.True(stream.CanSeek);
        foreach (var offset in offsets)
        {
            stream.Position = offset;
            var toRead = (int)Math.Min(64, full.Length - offset);
            var buffer = new byte[toRead];
            var read = stream.Read(buffer, 0, toRead);
            Assert.Equal(toRead, read);
            Assert.Equal(full.AsSpan((int)offset, toRead).ToArray(), buffer);
        }
    }

    [Theory]
    [InlineData("Rar.none.rar")]
    [InlineData("Rar5.none.rar")]
    public void Stored_SequentialEof_Throws_On_CrcMismatch(string archiveName)
    {
        using var archive = RarArchive.OpenArchive(Path.Combine(TEST_ARCHIVES_PATH, archiveName));
        var entry = archive
            .Entries.OfType<RarArchiveEntry>()
            .First(e => !e.IsDirectory && e.Size > 0);
        entry.FileHeader.FileCrc.NotNull()[0] ^= 0xFF;

        using var stream = entry.OpenEntryStream();
        Assert.IsType<StoredRarEntryStream>(stream);
        var ex = Assert.Throws<InvalidFormatException>(() => stream.CopyTo(Stream.Null));
        Assert.Contains("crc", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Rar.none.rar")]
    [InlineData("Rar5.none.rar")]
    public void Stored_AfterSeek_DoesNotValidateCrc(string archiveName)
    {
        using var archive = RarArchive.OpenArchive(Path.Combine(TEST_ARCHIVES_PATH, archiveName));
        var entry = archive
            .Entries.OfType<RarArchiveEntry>()
            .First(e => !e.IsDirectory && e.Size > 0);
        entry.FileHeader.FileCrc.NotNull()[0] ^= 0xFF;

        using var stream = entry.OpenEntryStream();
        stream.Seek(0, SeekOrigin.Begin);
        stream.CopyTo(Stream.Null); // should not throw despite corrupted CRC
    }

    [Theory]
    [InlineData("Rar.none.rar")]
    [InlineData("Rar5.none.rar")]
    public void Stored_EofAtLength_ReturnsZero(string archiveName)
    {
        using var archive = RarArchive.OpenArchive(Path.Combine(TEST_ARCHIVES_PATH, archiveName));
        var entry = archive.Entries.First(e => !e.IsDirectory && e.Size > 0);
        using var stream = entry.OpenEntryStream();
        Assert.IsType<StoredRarEntryStream>(stream);
        stream.Seek(stream.Length, SeekOrigin.Begin);
        Assert.Equal(0, stream.Read(new byte[1], 0, 1));
    }

    [Theory]
    [InlineData("Rar.none.rar")]
    [InlineData("Rar5.none.rar")]
    public void Stored_InterleavedConcurrentStreams_MatchReference(string archiveName)
    {
        using var archive = RarArchive.OpenArchive(Path.Combine(TEST_ARCHIVES_PATH, archiveName));
        var entries = archive.Entries.Where(e => !e.IsDirectory && e.Size > 0).Take(2).ToList();
        Assert.True(entries.Count >= 2);

        var references = new List<byte[]> { ReadFully(entries[0]), ReadFully(entries[1]) };

        using var streamA = entries[0].OpenEntryStream();
        using var streamB = entries[1].OpenEntryStream();
        Assert.IsType<StoredRarEntryStream>(streamA);
        Assert.IsType<StoredRarEntryStream>(streamB);

        var bufferA = new byte[4096];
        var bufferB = new byte[4096];
        using var resultA = new MemoryStream();
        using var resultB = new MemoryStream();
        var doneA = false;
        var doneB = false;
        while (!doneA || !doneB)
        {
            if (!doneA)
            {
                var read = streamA.Read(bufferA, 0, bufferA.Length);
                if (read == 0)
                {
                    doneA = true;
                }
                else
                {
                    resultA.Write(bufferA, 0, read);
                }
            }

            if (!doneB)
            {
                var read = streamB.Read(bufferB, 0, bufferB.Length);
                if (read == 0)
                {
                    doneB = true;
                }
                else
                {
                    resultB.Write(bufferB, 0, read);
                }
            }
        }

        Assert.Equal(references[0], resultA.ToArray());
        Assert.Equal(references[1], resultB.ToArray());
    }

    [Fact]
    public void Stored_MultiVolume_CanSeek_AcrossParts()
    {
        using var archive = OpenMultiNone();
        var entry = archive.Entries.Single(e => !e.IsDirectory);
        Assert.Equal(300_000, entry.Size);
        Assert.Equal(300_000, entry.CompressedSize);

        var full = ReadFully(entry);
        Assert.Equal(300_000, full.Length);

        using var stream = entry.OpenEntryStream();
        Assert.IsType<StoredRarEntryStream>(stream);
        Assert.True(stream.CanSeek);

        // Offsets that fall in part 1, part 2, and part 3.
        long[] offsets = [50_000, 150_000, 250_000];
        foreach (var offset in offsets)
        {
            stream.Position = offset;
            var buffer = new byte[32];
            Assert.Equal(32, stream.Read(buffer, 0, 32));
            Assert.Equal(full.AsSpan((int)offset, 32).ToArray(), buffer);
        }
    }

    [Fact]
    public void Stored_MultiVolume_SequentialCrcMismatch_Throws()
    {
        using var archive = OpenMultiNone();
        var entry = archive.Entries.OfType<RarArchiveEntry>().Single(e => !e.IsDirectory);
        // CRC lives on the final (non-split-after) part for multi-volume entries.
        var finalHeader = entry
            .Parts.Cast<SharpCompress.Common.Rar.RarFilePart>()
            .Select(p => p.FileHeader)
            .Single(fh => !fh.IsSplitAfter);
        finalHeader.FileCrc.NotNull()[0] ^= 0xFF;

        using var stream = entry.OpenEntryStream();
        var ex = Assert.Throws<InvalidFormatException>(() => stream.CopyTo(Stream.Null));
        Assert.Contains("crc", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Rar.rar")]
    [InlineData("Rar.solid.rar")]
    public void NonStored_Or_Solid_Uses_NonSeekable_SlowPath(string archiveName)
    {
        using var archive = RarArchive.OpenArchive(Path.Combine(TEST_ARCHIVES_PATH, archiveName));
        var entry = archive.Entries.First(e => !e.IsDirectory && e.Size > 0);
        using var stream = entry.OpenEntryStream();
        Assert.False(stream.CanSeek);
        Assert.IsNotType<StoredRarEntryStream>(stream);
    }

    [Theory]
    [InlineData("Rar.none.rar")]
    [InlineData("Rar5.none.rar")]
    public async Task Stored_Async_SeekRead_MatchesSlice(string archiveName)
    {
        await using var archive = await RarArchive.OpenAsyncArchive(
            Path.Combine(TEST_ARCHIVES_PATH, archiveName)
        );
        SharpCompress.Archives.IArchiveEntry? entry = null;
        await foreach (var e in archive.EntriesAsync)
        {
            if (!e.IsDirectory && e.Size > 0)
            {
                entry = e;
                break;
            }
        }

        Assert.NotNull(entry);
        var full = await ReadFullyAsync(entry);

        await using var stream = await entry.OpenEntryStreamAsync();
        Assert.IsType<StoredRarEntryStream>(stream);
        Assert.True(stream.CanSeek);

        var offset = full.Length / 3;
        stream.Position = offset;
        var toRead = Math.Min(128, full.Length - offset);
        var buffer = new byte[toRead];
        var read = await stream.ReadAsync(buffer);
        Assert.Equal(toRead, read);
        Assert.Equal(full.AsSpan(offset, toRead).ToArray(), buffer);
    }

    [Fact]
    public async Task Stored_MultiVolume_Async_FullRead()
    {
        var streams = MultiNoneParts
            .Select(p => (Stream)File.OpenRead(Path.Combine(TEST_ARCHIVES_PATH, p)))
            .ToArray();
        try
        {
            await using var archive = await RarArchive.OpenAsyncArchive(streams);
            SharpCompress.Archives.IArchiveEntry? entry = null;
            await foreach (var e in archive.EntriesAsync)
            {
                if (!e.IsDirectory)
                {
                    entry = e;
                    break;
                }
            }

            Assert.NotNull(entry);
            await using var stream = await entry.OpenEntryStreamAsync();
            Assert.IsType<StoredRarEntryStream>(stream);
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            Assert.Equal(300_000, ms.Length);
        }
        finally
        {
            foreach (var s in streams)
            {
                await s.DisposeAsync();
            }
        }
    }

    private static IRarArchive OpenMultiNone()
    {
        var streams = MultiNoneParts
            .Select(p => (Stream)File.OpenRead(Path.Combine(TEST_ARCHIVES_PATH, p)))
            .ToArray();
        return RarArchive.OpenArchive(streams);
    }

    private static byte[] ReadFully(SharpCompress.Archives.IArchiveEntry entry)
    {
        using var stream = entry.OpenEntryStream();
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    private static async Task<byte[]> ReadFullyAsync(SharpCompress.Archives.IArchiveEntry entry)
    {
        await using var stream = await entry.OpenEntryStreamAsync();
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms);
        return ms.ToArray();
    }

    private static byte[] ReadFullyFromOpenStream(Stream stream)
    {
        stream.Position = 0;
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }
}
