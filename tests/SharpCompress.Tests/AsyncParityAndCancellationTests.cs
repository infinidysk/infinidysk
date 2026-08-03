using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Archives;
using SharpCompress.Archives.Tar;
using SharpCompress.Common;
using SharpCompress.Readers;
using SharpCompress.Test.Mocks;
using SharpCompress.Writers;
using Xunit;

namespace SharpCompress.Test;

public class AsyncParityAndCancellationTests : TestBase
{
    [Theory]
    [InlineData("Zip.deflate.zip")]
    [InlineData("Tar.tar")]
    [InlineData("Rar.rar")]
    [InlineData("7Zip.nonsolid.7z")]
    public async Task ArchiveAsyncEntries_ShouldMatchSyncEntries(string archiveName)
    {
        var archivePath = Path.Combine(TEST_ARCHIVES_PATH, archiveName);

        var syncEntries = ReadArchiveEntries(archivePath);
        var asyncEntries = await ReadArchiveEntriesAsync(archivePath);

        Assert.Equal(syncEntries, asyncEntries);
    }

    [Theory]
    [InlineData("Zip.deflate.zip")]
    [InlineData("Tar.tar")]
    [InlineData("Tar.tar.gz")]
    [InlineData("Rar.rar")]
    public async Task ReaderAsyncEntries_ShouldMatchSyncEntries(string archiveName)
    {
        var archivePath = Path.Combine(TEST_ARCHIVES_PATH, archiveName);

        var syncEntries = ReadReaderEntries(archivePath);
        var asyncEntries = await ReadReaderEntriesAsync(archivePath);

        Assert.Equal(syncEntries, asyncEntries);
    }

    [Fact]
    public async Task AsyncReaderExtraction_ShouldRespectCancellationBeforeStart()
    {
        var archivePath = Path.Combine(TEST_ARCHIVES_PATH, "Zip.deflate.zip");
        await using var stream = File.OpenRead(archivePath);
        await using var reader = await ReaderFactory.OpenAsyncReader(stream);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await reader.WriteAllToDirectoryAsync(SCRATCH_FILES_PATH, cancellationToken: cts.Token)
        );
    }

    [Fact]
    public async Task AsyncArchiveExtraction_ShouldRespectCancellationBeforeStart()
    {
        var archivePath = Path.Combine(TEST_ARCHIVES_PATH, "Zip.deflate.zip");
        await using var archive = await ArchiveFactory.OpenAsyncArchive(archivePath);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await archive.WriteToDirectoryAsync(SCRATCH_FILES_PATH, cancellationToken: cts.Token)
        );
    }

    [Fact]
    public async Task TarArchiveOpenAsyncArchive_ShouldRespectCancellationBeforeValidationAsync()
    {
        await using var stream = File.OpenRead(Path.Combine(TEST_ARCHIVES_PATH, "Tar.tar"));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await TarArchive.OpenAsyncArchive(stream, cancellationToken: cts.Token)
        );
    }

    [Fact]
    public async Task TarArchiveOpenAsyncArchive_ShouldRespectCancellationDuringValidationAsync()
    {
        var archiveBytes = CreateLargeTarArchive();
        using var cts = new CancellationTokenSource();
        await using var stream = new CancelAfterBytesReadStream(
            new MemoryStream(archiveBytes),
            cts,
            cancelAfterBytes: 128
        );

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await TarArchive.OpenAsyncArchive(stream, cancellationToken: cts.Token)
        );
    }

    [Fact]
    public async Task AsyncReaderExtraction_ShouldRespectCancellationDuringRead()
    {
        var archiveBytes = CreateLargeTarArchive();
        using var cts = new CancellationTokenSource();
        await using var stream = new CancelAfterBytesReadStream(
            new MemoryStream(archiveBytes),
            cts,
            cancelAfterBytes: 2048
        );
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await using var reader = await ReaderFactory.OpenAsyncReader(
                stream,
                cancellationToken: cts.Token
            );
            await reader.WriteAllToDirectoryAsync(SCRATCH_FILES_PATH, cancellationToken: cts.Token);
        });
    }

    [Fact(Timeout = 30_000)]
    public async Task SevenZip_AsyncExtraction_ShouldRespectCancellationDuringRead()
    {
        var archiveBytes = await File.ReadAllBytesAsync(
            Path.Combine(TEST_ARCHIVES_PATH, "7Zip.LZMA.7z")
        );
        using var cts = new CancellationTokenSource();
        await using var stream = new CancelAfterBytesReadStream(
            new MemoryStream(archiveBytes),
            cts,
            cancelAfterBytes: 256
        );

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await using var archive = await ArchiveFactory.OpenAsyncArchive(
                stream,
                cancellationToken: cts.Token
            );
            await archive.WriteToDirectoryAsync(SCRATCH_FILES_PATH, cancellationToken: cts.Token);
        });
    }

    [Fact(Timeout = 30_000)]
    public async Task Zip_AsyncExtraction_ShouldRespectCancellationDuringRead()
    {
        var archiveBytes = await File.ReadAllBytesAsync(
            Path.Combine(TEST_ARCHIVES_PATH, "Zip.deflate.zip")
        );
        using var cts = new CancellationTokenSource();
        await using var stream = new CancelAfterBytesReadStream(
            new MemoryStream(archiveBytes),
            cts,
            cancelAfterBytes: 256
        );

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await using var archive = await ArchiveFactory.OpenAsyncArchive(
                stream,
                cancellationToken: cts.Token
            );
            await archive.WriteToDirectoryAsync(SCRATCH_FILES_PATH, cancellationToken: cts.Token);
        });
    }

    [Fact]
    public async Task OpenAsyncReader_CallerProvidedStream_ShouldRemainOpenByDefault()
    {
        var archiveBytes = await File.ReadAllBytesAsync(
            Path.Combine(TEST_ARCHIVES_PATH, "Zip.deflate.zip")
        );
        var stream = new TestStream(new MemoryStream(archiveBytes));

        try
        {
            await using (var reader = await ReaderFactory.OpenAsyncReader(stream))
            {
                Assert.True(await reader.MoveToNextEntryAsync());
            }

            Assert.False(stream.IsDisposed);
        }
        finally
        {
            stream.Dispose();
        }
    }

    [Fact]
    public async Task OpenAsyncArchive_CallerProvidedStream_ShouldRemainOpenByDefault()
    {
        var archiveBytes = await File.ReadAllBytesAsync(
            Path.Combine(TEST_ARCHIVES_PATH, "Zip.deflate.zip")
        );
        var stream = new TestStream(new MemoryStream(archiveBytes));

        try
        {
            await using (var archive = await ArchiveFactory.OpenAsyncArchive(stream))
            {
                await foreach (var _ in archive.EntriesAsync)
                {
                    break;
                }
            }

            Assert.False(stream.IsDisposed);
        }
        finally
        {
            await stream.DisposeAsync();
        }
    }

    private static List<EntrySnapshot> ReadArchiveEntries(string archivePath)
    {
        using var archive = ArchiveFactory.OpenArchive(archivePath);
        return archive
            .Entries.Where(entry => !entry.IsDirectory)
            .Select(entry =>
            {
                using var stream = entry.OpenEntryStream();
                using var memory = new MemoryStream();
                stream.CopyTo(memory);
                return new EntrySnapshot(
                    entry.Key ?? string.Empty,
                    entry.Size,
                    entry.CompressionType,
                    Convert.ToBase64String(memory.ToArray())
                );
            })
            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .ToList();
    }

    private static async Task<List<EntrySnapshot>> ReadArchiveEntriesAsync(string archivePath)
    {
        await using var archive = await ArchiveFactory.OpenAsyncArchive(archivePath);
        var entries = new List<EntrySnapshot>();
        await foreach (var entry in archive.EntriesAsync)
        {
            if (entry.IsDirectory)
            {
                continue;
            }

            await using var stream = await entry.OpenEntryStreamAsync();
            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory);
            entries.Add(
                new EntrySnapshot(
                    entry.Key ?? string.Empty,
                    entry.Size,
                    entry.CompressionType,
                    Convert.ToBase64String(memory.ToArray())
                )
            );
        }

        return entries.OrderBy(entry => entry.Key, StringComparer.Ordinal).ToList();
    }

    private static List<EntrySnapshot> ReadReaderEntries(string archivePath)
    {
        using var stream = File.OpenRead(archivePath);
        using var reader = ReaderFactory.OpenReader(stream);
        var entries = new List<EntrySnapshot>();
        while (reader.MoveToNextEntry())
        {
            if (reader.Entry.IsDirectory)
            {
                continue;
            }

            using var memory = new MemoryStream();
            reader.WriteEntryTo(memory);
            entries.Add(
                new EntrySnapshot(
                    reader.Entry.Key ?? string.Empty,
                    reader.Entry.Size,
                    reader.Entry.CompressionType,
                    Convert.ToBase64String(memory.ToArray())
                )
            );
        }

        return entries.OrderBy(entry => entry.Key, StringComparer.Ordinal).ToList();
    }

    private static async Task<List<EntrySnapshot>> ReadReaderEntriesAsync(string archivePath)
    {
        await using var stream = File.OpenRead(archivePath);
        await using var reader = await ReaderFactory.OpenAsyncReader(stream);
        var entries = new List<EntrySnapshot>();
        while (await reader.MoveToNextEntryAsync())
        {
            if (reader.Entry.IsDirectory)
            {
                continue;
            }

            using var memory = new MemoryStream();
            await reader.WriteEntryToAsync(memory);
            entries.Add(
                new EntrySnapshot(
                    reader.Entry.Key ?? string.Empty,
                    reader.Entry.Size,
                    reader.Entry.CompressionType,
                    Convert.ToBase64String(memory.ToArray())
                )
            );
        }

        return entries.OrderBy(entry => entry.Key, StringComparer.Ordinal).ToList();
    }

    private static byte[] CreateLargeTarArchive()
    {
        using var stream = new MemoryStream();
        using (
            var writer = WriterFactory.OpenWriter(
                stream,
                ArchiveType.Tar,
                new WriterOptions(CompressionType.None)
            )
        )
        {
            writer.Write("large.bin", new MemoryStream(new byte[64 * 1024]));
        }
        return stream.ToArray();
    }

    private sealed record EntrySnapshot(
        string Key,
        long Size,
        CompressionType CompressionType,
        string Content
    );
}
