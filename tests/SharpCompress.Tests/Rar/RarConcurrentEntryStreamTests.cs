using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SharpCompress.Archives;
using SharpCompress.Archives.Rar;
using SharpCompress.Common;
using Xunit;

namespace SharpCompress.Test.Rar;

public class RarConcurrentEntryStreamTests : ArchiveTests
{
    [Fact]
    public void NonSolid_InterleavedEntryStreams_MatchReference()
    {
        var archivePath = Path.Combine(TEST_ARCHIVES_PATH, "Rar.rar");
        using var archive = RarArchive.OpenArchive(archivePath);
        Assert.False(archive.IsSolid);

        var entries = archive.Entries.Where(e => !e.IsDirectory).Take(2).ToList();
        Assert.True(entries.Count >= 2, "Test archive needs at least two file entries.");

        var references = new List<byte[]> { ExtractFully(entries[0]), ExtractFully(entries[1]) };

        using var streamA = entries[0].OpenEntryStream();
        using var streamB = entries[1].OpenEntryStream();
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
    public async Task NonSolid_InterleavedEntryStreams_MatchReference_Async()
    {
        var archivePath = Path.Combine(TEST_ARCHIVES_PATH, "Rar.rar");
        await using var archive = await RarArchive.OpenAsyncArchive(archivePath);
        Assert.False(await archive.IsSolidAsync());

        var entries = new List<IArchiveEntry>();
        await foreach (var entry in archive.EntriesAsync)
        {
            if (!entry.IsDirectory)
            {
                entries.Add(entry);
                if (entries.Count == 2)
                {
                    break;
                }
            }
        }

        Assert.True(entries.Count >= 2, "Test archive needs at least two file entries.");

        var references = new List<byte[]>
        {
            await ExtractFullyAsync(entries[0]),
            await ExtractFullyAsync(entries[1]),
        };

        await using var streamA = await entries[0].OpenEntryStreamAsync();
        await using var streamB = await entries[1].OpenEntryStreamAsync();
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
                var read = await streamA.ReadAsync(bufferA);
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
                var read = await streamB.ReadAsync(bufferB);
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
    public void Solid_ConcurrentOpenEntryStream_Throws()
    {
        var archivePath = Path.Combine(TEST_ARCHIVES_PATH, "Rar.solid.rar");
        using var archive = RarArchive.OpenArchive(archivePath);
        Assert.True(archive.IsSolid);

        var entries = archive.Entries.Where(e => !e.IsDirectory).Take(2).ToList();
        Assert.True(entries.Count >= 2, "Solid test archive needs at least two file entries.");

        using var first = entries[0].OpenEntryStream();
        var exception = Assert.Throws<ArchiveOperationException>(() =>
            entries[1].OpenEntryStream()
        );
        Assert.Contains("concurrent entry streams", exception.Message);
        Assert.Contains("ExtractAllEntries", exception.Message);
    }

    [Fact]
    public async Task Solid_ConcurrentOpenEntryStream_Throws_Async()
    {
        var archivePath = Path.Combine(TEST_ARCHIVES_PATH, "Rar.solid.rar");
        await using var archive = await RarArchive.OpenAsyncArchive(archivePath);
        Assert.True(await archive.IsSolidAsync());

        var entries = new List<IArchiveEntry>();
        await foreach (var entry in archive.EntriesAsync)
        {
            if (!entry.IsDirectory)
            {
                entries.Add(entry);
                if (entries.Count == 2)
                {
                    break;
                }
            }
        }

        Assert.True(entries.Count >= 2, "Solid test archive needs at least two file entries.");

        await using var first = await entries[0].OpenEntryStreamAsync();
        var exception = await Assert.ThrowsAsync<ArchiveOperationException>(async () =>
            await entries[1].OpenEntryStreamAsync()
        );
        Assert.Contains("concurrent entry streams", exception.Message);
        Assert.Contains("ExtractAllEntries", exception.Message);
    }

    [Fact]
    public void Solid_SequentialOpenAfterDispose_Succeeds()
    {
        var archivePath = Path.Combine(TEST_ARCHIVES_PATH, "Rar.solid.rar");
        using var archive = RarArchive.OpenArchive(archivePath);
        Assert.True(archive.IsSolid);

        var entries = archive.Entries.Where(e => !e.IsDirectory).Take(2).ToList();
        Assert.True(entries.Count >= 2);

        using (var first = entries[0].OpenEntryStream())
        {
            first.CopyTo(Stream.Null);
        }

        using var second = entries[1].OpenEntryStream();
        second.CopyTo(Stream.Null);
    }

    private static byte[] ExtractFully(IArchiveEntry entry)
    {
        using var stream = entry.OpenEntryStream();
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    private static async Task<byte[]> ExtractFullyAsync(IArchiveEntry entry)
    {
        await using var stream = await entry.OpenEntryStreamAsync();
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms);
        return ms.ToArray();
    }
}
