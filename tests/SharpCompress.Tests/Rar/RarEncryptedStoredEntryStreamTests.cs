using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SharpCompress.Archives.Rar;
using SharpCompress.Common;
using SharpCompress.Common.Rar;
using SharpCompress.Common.Rar.Headers;
using SharpCompress.Compressors.Rar;
using SharpCompress.Readers;
using Xunit;

namespace SharpCompress.Test.Rar;

public class RarEncryptedStoredEntryStreamTests : ArchiveTests
{
    private const string Password = "test";

    private static readonly string[] MultiEncryptedParts =
    [
        "Rar5.multi.none.encrypted.part01.rar",
        "Rar5.multi.none.encrypted.part02.rar",
        "Rar5.multi.none.encrypted.part03.rar",
    ];

    [Fact]
    public void EncryptedStored_SingleVolume_UsesFastPath()
    {
        using var archive = OpenEncrypted("Rar5.none.encrypted.rar");
        var entry = archive.Entries.First(e => !e.IsDirectory && e.Size > 0);
        using var stream = entry.OpenEntryStream();
        Assert.IsType<EncryptedStoredRarEntryStream>(stream);
        Assert.True(stream.CanSeek);
        Assert.Equal(entry.Size, stream.Length);
    }

    [Fact]
    public void EncryptedStored_SingleVolume_FullRead_MatchesSlowPath()
    {
        using var archive = OpenEncrypted("Rar5.none.encrypted.rar");
        var entry = (RarArchiveEntry)archive.Entries.First(e => !e.IsDirectory && e.Size > 0);
        var slow = ReadSlowPath(entry, archive);

        using var stream = entry.OpenEntryStream();
        Assert.IsType<EncryptedStoredRarEntryStream>(stream);
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        Assert.Equal(slow, ms.ToArray());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    public void EncryptedStored_SingleVolume_SeekRead_MatchesSlowPathSlice(long offset)
    {
        using var archive = OpenEncrypted("Rar5.none.encrypted.rar");
        var entry = (RarArchiveEntry)archive.Entries.First(e => !e.IsDirectory && e.Size > 0);
        var slow = ReadSlowPath(entry, archive);
        if (offset >= slow.Length)
        {
            return;
        }

        using var stream = entry.OpenEntryStream();
        stream.Position = offset;
        var toRead = (int)Math.Min(64, slow.Length - offset);
        var buffer = new byte[toRead];
        Assert.Equal(toRead, stream.Read(buffer, 0, toRead));
        Assert.Equal(slow.AsSpan((int)offset, toRead).ToArray(), buffer);
    }

    [Fact]
    public void EncryptedStored_WrongPassword_Throws()
    {
        using var archive = RarArchive.OpenArchive(
            Path.Combine(TEST_ARCHIVES_PATH, "Rar5.none.encrypted.rar"),
            new ReaderOptions { Password = "wrong" }
        );
        var entry = archive.Entries.First(e => !e.IsDirectory && e.Size > 0);
        Assert.Throws<SharpCompress.Common.CryptographicException>(() => entry.OpenEntryStream());
    }

    [Fact]
    public void EncryptedStored_MultiVolume_CanSeek_AcrossParts()
    {
        using var archive = OpenEncryptedMulti();
        var entry = archive.Entries.Single(e => !e.IsDirectory);
        var slow = ReadSlowPath((RarArchiveEntry)entry, archive);

        using var stream = entry.OpenEntryStream();
        Assert.IsType<EncryptedStoredRarEntryStream>(stream);

        long[] offsets = [50_000, 150_000, 250_000];
        foreach (var offset in offsets)
        {
            stream.Position = offset;
            var buffer = new byte[32];
            Assert.Equal(32, stream.Read(buffer, 0, 32));
            Assert.Equal(slow.AsSpan((int)offset, 32).ToArray(), buffer);
        }
    }

    [Fact]
    public async Task EncryptedStored_Async_SeekRead_MatchesSlowPathSlice()
    {
        await using var archive = (RarArchive)
            await RarArchive.OpenAsyncArchive(
                Path.Combine(TEST_ARCHIVES_PATH, "Rar5.none.encrypted.rar"),
                new ReaderOptions { Password = Password }
            );
        var entry = (RarArchiveEntry)archive.Entries.First(e => !e.IsDirectory && e.Size > 0);
        var slow = ReadSlowPath(entry, archive);

        await using var stream = await entry.OpenEntryStreamAsync();
        Assert.IsType<EncryptedStoredRarEntryStream>(stream);

        var offset = slow.Length / 3;
        stream.Position = offset;
        var toRead = (int)Math.Min(128, slow.Length - offset);
        var buffer = new byte[toRead];
        var read = await stream.ReadAsync(buffer);
        Assert.Equal(toRead, read);
        Assert.Equal(slow.AsSpan((int)offset, toRead).ToArray(), buffer);
    }

    [Theory]
    [InlineData("Rar5.encrypted_filesOnly.rar")]
    public void EncryptedCompressed_UsesSlowPath(string archiveName)
    {
        using var archive = OpenEncrypted(archiveName);
        var entry = archive.Entries.First(e => !e.IsDirectory && e.Size > 0);
        using var stream = entry.OpenEntryStream();
        Assert.IsNotType<EncryptedStoredRarEntryStream>(stream);
        Assert.False(stream.CanSeek);
    }

    private static RarArchive OpenEncrypted(string archiveName) =>
        (RarArchive)
            RarArchive.OpenArchive(
                Path.Combine(TEST_ARCHIVES_PATH, archiveName),
                new ReaderOptions { Password = Password }
            );

    private static RarArchive OpenEncryptedMulti()
    {
        var streams = MultiEncryptedParts
            .Select(p => (Stream)File.OpenRead(Path.Combine(TEST_ARCHIVES_PATH, p)))
            .ToArray();
        return (RarArchive)
            RarArchive.OpenArchive(streams, new ReaderOptions { Password = Password });
    }

    private static byte[] ReadSlowPath(RarArchiveEntry entry, RarArchive archive)
    {
        var readStream = new MultiVolumeReadOnlyStream(entry.Parts.Cast<RarFilePart>());
        var unpack = archive.AcquireUnpackForEntry(
            entry.IsRarV3,
            archive.IsSolid,
            out var ownsUnpack
        );
        RarStream? stream = null;
        try
        {
            if (entry.FileHeader.FileCrc?.Length > 5)
            {
                stream = RarBLAKE2spStream.Create(
                    unpack,
                    entry.FileHeader,
                    readStream,
                    ownsUnpack,
                    null
                );
            }
            else
            {
                stream = RarCrcStream.Create(
                    unpack,
                    entry.FileHeader,
                    readStream,
                    ownsUnpack,
                    null
                );
            }

            stream.Initialize();
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            return ms.ToArray();
        }
        finally
        {
            stream?.Dispose();
        }
    }
}
