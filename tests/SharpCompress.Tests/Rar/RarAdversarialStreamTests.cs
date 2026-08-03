using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Archives;
using SharpCompress.Archives.Rar;
using SharpCompress.Archives.SevenZip;
using SharpCompress.Common;
using SharpCompress.Readers;
using SharpCompress.Test.Mocks;
using Xunit;

namespace SharpCompress.Test.Rar;

public class RarAdversarialStreamTests : ArchiveTests
{
    private static readonly string[] MultiVolumeParts =
    [
        "Rar.multi.part01.rar",
        "Rar.multi.part02.rar",
        "Rar.multi.part03.rar",
        "Rar.multi.part04.rar",
        "Rar.multi.part05.rar",
        "Rar.multi.part06.rar",
    ];

    [Fact(Timeout = 30_000)]
    public void Rar_MultiVolume_TruncatedLastPart_ThrowsIncompleteArchiveException()
    {
        AssertMultiVolumeTruncationThrows(useExplicitBufferLoop: false);
    }

    [Fact(Timeout = 30_000)]
    public void Rar_MultiVolume_TruncatedLastPart_Throws_ViaSyncRead()
    {
        AssertMultiVolumeTruncationThrows(useExplicitBufferLoop: true);
    }

    [Fact(Timeout = 30_000)]
    public async Task Rar_MultiVolume_TruncatedLastPart_ThrowsIncompleteArchiveException_Async()
    {
        await AssertMultiVolumeTruncationThrowsAsync(useMemoryOverload: false);
    }

    [Fact(Timeout = 30_000)]
    public async Task Rar_MultiVolume_TruncatedLastPart_Throws_ViaReadAsyncMemory()
    {
        await AssertMultiVolumeTruncationThrowsAsync(useMemoryOverload: true);
    }

    [Theory]
    [InlineData("Rar.encrypted_filesOnly.rar", "test", 1)]
    [InlineData("Rar.encrypted_filesOnly.rar", "test", 7)]
    [InlineData("Rar5.encrypted_filesOnly.rar", "test", 1)]
    [InlineData("Rar5.encrypted_filesOnly.rar", "test", 16)]
    public void Rar_Encrypted_ChunkyReads_MatchBaseline(
        string archiveName,
        string password,
        int chunkSize
    )
    {
        var bytes = File.ReadAllBytes(Path.Combine(TEST_ARCHIVES_PATH, archiveName));
        var options = ReaderOptions.ForExternalStream with { Password = password };

        var baseline = ExtractRarEntryBytes(new MemoryStream(bytes, writable: false), options);
        var chunked = ExtractRarEntryBytes(
            new ChunkyReadStream(new MemoryStream(bytes, writable: false), chunkSize),
            options
        );

        Assert.Equal(baseline.Count, chunked.Count);
        foreach (var key in baseline.Keys)
        {
            Assert.Equal(baseline[key], chunked[key]);
        }
    }

    [Theory]
    [InlineData("Rar.encrypted_filesOnly.rar", "test")]
    [InlineData("Rar5.encrypted_filesOnly.rar", "test")]
    public void Rar_Encrypted_AlternatingOddReads_MatchBaseline(string archiveName, string password)
    {
        int[] pattern = [1, 7, 15, 16, 17, 8192];
        var bytes = File.ReadAllBytes(Path.Combine(TEST_ARCHIVES_PATH, archiveName));
        var options = ReaderOptions.ForExternalStream with { Password = password };

        var baseline = ExtractRarEntryBytes(new MemoryStream(bytes, writable: false), options);
        var alternating = ExtractRarEntryBytesWithReadPattern(
            new MemoryStream(bytes, writable: false),
            options,
            pattern
        );

        Assert.Equal(baseline.Count, alternating.Count);
        foreach (var key in baseline.Keys)
        {
            Assert.Equal(baseline[key], alternating[key]);
        }
    }

    [Theory]
    [InlineData("Rar.encrypted_filesOnly.rar", "test", 1)]
    [InlineData("Rar5.encrypted_filesOnly.rar", "test", 7)]
    public async Task Rar_Encrypted_ChunkyReads_MatchBaseline_Async(
        string archiveName,
        string password,
        int chunkSize
    )
    {
        var bytes = File.ReadAllBytes(Path.Combine(TEST_ARCHIVES_PATH, archiveName));
        var options = ReaderOptions.ForExternalStream with { Password = password };

        var baseline = await ExtractRarEntryBytesAsync(
            new MemoryStream(bytes, writable: false),
            options
        );
        var chunked = await ExtractRarEntryBytesAsync(
            new ChunkyReadStream(new MemoryStream(bytes, writable: false), chunkSize),
            options
        );

        Assert.Equal(baseline.Count, chunked.Count);
        foreach (var key in baseline.Keys)
        {
            Assert.Equal(baseline[key], chunked[key]);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task Rar_AsyncExtraction_RespectsCancellationDuringRead()
    {
        // RarArchive.OpenAsyncArchive still sync-reads headers, so CancelAfterBytesReadStream
        // (sync Read unsupported) cannot wrap the archive source. Cancel after the first entry chunk.
        var archiveBytes = await File.ReadAllBytesAsync(
            Path.Combine(TEST_ARCHIVES_PATH, "Rar.rar")
        );
        await using var archive = await RarArchive.OpenAsyncArchive(new MemoryStream(archiveBytes));
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var entry in archive.EntriesAsync.WithCancellation(cts.Token))
            {
                if (entry.IsDirectory)
                {
                    continue;
                }

                await using var entryStream = await entry.OpenEntryStreamAsync(cts.Token);
                var buffer = new byte[256];
                _ = await entryStream.ReadAsync(buffer, cts.Token);
                cts.Cancel();
                _ = await entryStream.ReadAsync(buffer, cts.Token);
            }
        });
    }

    [Fact(Timeout = 30_000)]
    public void SevenZip_Truncated_Throws()
    {
        var path = Path.Combine(TEST_ARCHIVES_PATH, "7Zip.solid.7z");
        using var fileStream = File.OpenRead(path);
        using var truncated = TruncatedStream.AtPercent(fileStream, 50, leaveOpen: false);

        var exception = Assert.ThrowsAny<Exception>(() =>
        {
            using var archive = SevenZipArchive.OpenArchive(truncated);
            foreach (var entry in archive.Entries.Where(e => !e.IsDirectory))
            {
                using var entryStream = entry.OpenEntryStream();
                entryStream.CopyTo(Stream.Null);
            }
        });

        AssertTruncationFailure(exception);
    }

    [Fact(Timeout = 30_000)]
    public void Zip_Truncated_Throws()
    {
        var path = Path.Combine(TEST_ARCHIVES_PATH, "Zip.deflate.zip");
        using var fileStream = File.OpenRead(path);
        using var truncated = TruncatedStream.AtPercent(fileStream, 50, leaveOpen: false);

        var exception = Assert.ThrowsAny<Exception>(() =>
        {
            using var archive = ArchiveFactory.OpenArchive(truncated);
            foreach (var entry in archive.Entries.Where(e => !e.IsDirectory))
            {
                using var entryStream = entry.OpenEntryStream();
                entryStream.CopyTo(Stream.Null);
            }
        });

        AssertTruncationFailure(exception);
    }

    [Theory]
    [InlineData("Zip.bzip2.pkware.zip", "test", 1)]
    [InlineData("Zip.bzip2.pkware.zip", "test", 7)]
    [InlineData("Zip.deflate.zip", null, 1)]
    public void Zip_ChunkyReads_MatchBaseline(string archiveName, string? password, int chunkSize)
    {
        var bytes = File.ReadAllBytes(Path.Combine(TEST_ARCHIVES_PATH, archiveName));
        var options = ReaderOptions.ForExternalStream;
        if (password is not null)
        {
            options = options with { Password = password };
        }

        var baseline = ExtractArchiveEntryBytes(new MemoryStream(bytes, writable: false), options);
        var chunked = ExtractArchiveEntryBytes(
            new ChunkyReadStream(new MemoryStream(bytes, writable: false), chunkSize),
            options
        );

        Assert.Equal(baseline.Count, chunked.Count);
        foreach (var key in baseline.Keys)
        {
            Assert.Equal(baseline[key], chunked[key]);
        }
    }

    [Theory]
    [InlineData("7Zip.LZMA.7z", null, 1)]
    [InlineData("7Zip.LZMA.7z", null, 16)]
    [InlineData("7Zip.LZMA.Aes.7z", "testpassword", 7)]
    public void SevenZip_ChunkyReads_MatchBaseline(
        string archiveName,
        string? password,
        int chunkSize
    )
    {
        var bytes = File.ReadAllBytes(Path.Combine(TEST_ARCHIVES_PATH, archiveName));
        var options = ReaderOptions.ForExternalStream;
        if (password is not null)
        {
            options = options with { Password = password };
        }

        var baseline = ExtractSevenZipEntryBytes(new MemoryStream(bytes, writable: false), options);
        var chunked = ExtractSevenZipEntryBytes(
            new ChunkyReadStream(new MemoryStream(bytes, writable: false), chunkSize),
            options
        );

        Assert.Equal(baseline.Count, chunked.Count);
        foreach (var key in baseline.Keys)
        {
            Assert.Equal(baseline[key], chunked[key]);
        }
    }

    private void AssertMultiVolumeTruncationThrows(bool useExplicitBufferLoop)
    {
        var streams = OpenTruncatedMultiVolume(percentOfLastPart: 50);
        try
        {
            using var archive = RarArchive.OpenArchive(streams);
            var exception = Assert.ThrowsAny<Exception>(() =>
            {
                foreach (var entry in archive.Entries.Where(e => !e.IsDirectory))
                {
                    using var entryStream = entry.OpenEntryStream();
                    if (useExplicitBufferLoop)
                    {
                        DrainWithSyncRead(entryStream);
                    }
                    else
                    {
                        entryStream.CopyTo(Stream.Null);
                    }
                }
            });

            AssertIncompleteOrWrapped(exception);
        }
        finally
        {
            foreach (var stream in streams)
            {
                stream.Dispose();
            }
        }
    }

    private async Task AssertMultiVolumeTruncationThrowsAsync(bool useMemoryOverload)
    {
        var streams = OpenTruncatedMultiVolume(percentOfLastPart: 50);
        try
        {
            await using var archive = await RarArchive.OpenAsyncArchive(streams);
            var exception = await Assert.ThrowsAnyAsync<Exception>(async () =>
            {
                await foreach (var entry in archive.EntriesAsync)
                {
                    if (entry.IsDirectory)
                    {
                        continue;
                    }

                    await using var entryStream = await entry.OpenEntryStreamAsync();
                    if (useMemoryOverload)
                    {
                        await DrainWithReadAsyncMemory(entryStream);
                    }
                    else
                    {
                        await DrainWithReadAsyncByteArray(entryStream);
                    }
                }
            });

            AssertIncompleteOrWrapped(exception);
        }
        finally
        {
            foreach (var stream in streams)
            {
                await stream.DisposeAsync();
            }
        }
    }

    private static void DrainWithSyncRead(Stream entryStream)
    {
        var buffer = new byte[4096];
        while (true)
        {
            var read = entryStream.Read(buffer, 0, buffer.Length);
            if (read == 0)
            {
                break;
            }
        }
    }

    private static async Task DrainWithReadAsyncByteArray(Stream entryStream)
    {
        var buffer = new byte[4096];
        while (true)
        {
            var read = await entryStream.ReadAsync(buffer, 0, buffer.Length);
            if (read == 0)
            {
                break;
            }
        }
    }

    private static async Task DrainWithReadAsyncMemory(Stream entryStream)
    {
        var buffer = new byte[4096];
        while (true)
        {
            var read = await entryStream.ReadAsync(buffer.AsMemory());
            if (read == 0)
            {
                break;
            }
        }
    }

    private Stream[] OpenTruncatedMultiVolume(int percentOfLastPart)
    {
        var streams = new List<Stream>();
        for (var i = 0; i < MultiVolumeParts.Length; i++)
        {
            var path = Path.Combine(TEST_ARCHIVES_PATH, MultiVolumeParts[i]);
            var bytes = File.ReadAllBytes(path);
            if (i == MultiVolumeParts.Length - 1)
            {
                var keep = Math.Max(1, bytes.Length * percentOfLastPart / 100);
                Array.Resize(ref bytes, keep);
            }

            streams.Add(new MemoryStream(bytes, writable: false));
        }

        return streams.ToArray();
    }

    private static void AssertIncompleteOrWrapped(Exception exception)
    {
        var current = exception;
        while (current is not null)
        {
            if (current is IncompleteArchiveException)
            {
                Assert.Contains("Unexpected end of stream", current.Message);
                return;
            }

            current = current.InnerException;
        }

        Assert.Fail(
            $"Expected IncompleteArchiveException, got {exception.GetType().Name}: {exception.Message}"
        );
    }

    private static void AssertTruncationFailure(Exception exception)
    {
        // Truncation must not succeed silently; formats may surface IncompleteArchiveException,
        // InvalidFormatException, decoder DataErrorException, or bounds errors depending on where
        // the cut lands.
        Assert.NotNull(exception);
        Assert.False(
            exception is ArgumentNullException or ObjectDisposedException,
            $"Unexpected exception type for truncation: {exception.GetType().Name}: {exception.Message}"
        );
    }

    private static Dictionary<string, byte[]> ExtractRarEntryBytes(
        Stream stream,
        ReaderOptions options
    )
    {
        var results = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        using var archive = RarArchive.OpenArchive(stream, options);
        foreach (var entry in archive.Entries.Where(e => !e.IsDirectory))
        {
            using var entryStream = entry.OpenEntryStream();
            using var ms = new MemoryStream();
            entryStream.CopyTo(ms);
            results[entry.Key!] = ms.ToArray();
        }

        return results;
    }

    private static Dictionary<string, byte[]> ExtractRarEntryBytesWithReadPattern(
        Stream stream,
        ReaderOptions options,
        int[] readSizes
    )
    {
        var results = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        using var archive = RarArchive.OpenArchive(stream, options);
        foreach (var entry in archive.Entries.Where(e => !e.IsDirectory))
        {
            using var entryStream = entry.OpenEntryStream();
            using var ms = new MemoryStream();
            var patternIndex = 0;
            var buffer = new byte[readSizes.Max()];
            int read;
            while (
                (read = entryStream.Read(buffer, 0, readSizes[patternIndex++ % readSizes.Length]))
                > 0
            )
            {
                ms.Write(buffer, 0, read);
            }

            results[entry.Key!] = ms.ToArray();
        }

        return results;
    }

    private static async Task<Dictionary<string, byte[]>> ExtractRarEntryBytesAsync(
        Stream stream,
        ReaderOptions options
    )
    {
        var results = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        await using var archive = await RarArchive.OpenAsyncArchive(stream, options);
        await foreach (var entry in archive.EntriesAsync)
        {
            if (entry.IsDirectory)
            {
                continue;
            }

            await using var entryStream = await entry.OpenEntryStreamAsync();
            using var ms = new MemoryStream();
            await entryStream.CopyToAsync(ms);
            results[entry.Key!] = ms.ToArray();
        }

        return results;
    }

    private static Dictionary<string, byte[]> ExtractArchiveEntryBytes(
        Stream stream,
        ReaderOptions options
    )
    {
        var results = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        using var archive = ArchiveFactory.OpenArchive(stream, options);
        foreach (var entry in archive.Entries.Where(e => !e.IsDirectory))
        {
            using var entryStream = entry.OpenEntryStream();
            using var ms = new MemoryStream();
            entryStream.CopyTo(ms);
            results[entry.Key!] = ms.ToArray();
        }

        return results;
    }

    private static Dictionary<string, byte[]> ExtractSevenZipEntryBytes(
        Stream stream,
        ReaderOptions options
    )
    {
        var results = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        using var archive = SevenZipArchive.OpenArchive(stream, options);
        foreach (var entry in archive.Entries.Where(e => !e.IsDirectory))
        {
            using var entryStream = entry.OpenEntryStream();
            using var ms = new MemoryStream();
            entryStream.CopyTo(ms);
            results[entry.Key!] = ms.ToArray();
        }

        return results;
    }
}
