using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Archives;
using SharpCompress.Archives.Tar;
using SharpCompress.Common;
using SharpCompress.Common.Rar.Headers;
using SharpCompress.IO;
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
        var archivePath = Path.Join(TEST_ARCHIVES_PATH, archiveName);

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
        var archivePath = Path.Join(TEST_ARCHIVES_PATH, archiveName);

        var syncEntries = ReadReaderEntries(archivePath);
        var asyncEntries = await ReadReaderEntriesAsync(archivePath);

        Assert.Equal(syncEntries, asyncEntries);
    }

    [Fact]
    public async Task AsyncReaderExtraction_ShouldRespectCancellationBeforeStart()
    {
        var archivePath = Path.Join(TEST_ARCHIVES_PATH, "Zip.deflate.zip");
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
        var archivePath = Path.Join(TEST_ARCHIVES_PATH, "Zip.deflate.zip");
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
        await using var stream = File.OpenRead(Path.Join(TEST_ARCHIVES_PATH, "Tar.tar"));
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
            Path.Join(TEST_ARCHIVES_PATH, "7Zip.LZMA.7z")
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
            Path.Join(TEST_ARCHIVES_PATH, "Zip.deflate.zip")
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
            Path.Join(TEST_ARCHIVES_PATH, "Zip.deflate.zip")
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

    [Theory]
    [InlineData("Rar.none.rar")]
    [InlineData("Rar5.none.rar")]
    public async Task RarHeaderFactory_ReadHeadersAsync_ShouldMatchSyncHeaders(string archiveName)
    {
        var syncHeaders = ReadRarHeaders(archiveName);
        var asyncHeaders = await ReadRarHeadersAsync(archiveName);

        Assert.Equal(syncHeaders, asyncHeaders);
    }

    [Theory]
    [InlineData("Rar.none.rar")]
    [InlineData("Rar5.none.rar")]
    public async Task RarHeaderFactory_ReadHeadersAsync_ShouldRespectCancellationAfterSignature(
        string archiveName
    )
    {
        var archiveBytes = await File.ReadAllBytesAsync(
            Path.Join(TEST_ARCHIVES_PATH, archiveName)
        );
        using var cts = new CancellationTokenSource();
        // RAR4 signature is 7 bytes and RAR5 is 8; cancel after that so MarkHeader
        // does not wrap the cancellation into RarHeaderReadException.
        await using var stream = new CancelAfterBytesReadStream(
            new MemoryStream(archiveBytes),
            cts,
            cancelAfterBytes: 16
        );
        var factory = CreateRarHeaderFactory();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in factory.ReadHeadersAsync(stream, cts.Token)) { }
        });
    }

    [Theory]
    [InlineData("Rar.none.rar")]
    [InlineData("Rar5.none.rar")]
    [InlineData("Rar.comment.rar")]
    [InlineData("Rar5.comment.rar")]
    public async Task RarHeaderFactory_ReadHeadersAsync_StopAfterFirstFile_SkipsNoPackedData(
        string archiveName
    )
    {
        var archiveBytes = await File.ReadAllBytesAsync(
            Path.Join(TEST_ARCHIVES_PATH, archiveName)
        );
        await using var stream = new SeekCountingStream(new MemoryStream(archiveBytes));
        var factory = CreateRarHeaderFactory();
        IRarFileHeader? firstFile = null;
        var seeksAtYield = -1;

        await foreach (var header in factory.ReadHeadersAsync(stream))
        {
            if (header.HeaderType != HeaderType.File || header is not IRarFileHeader fileHeader)
            {
                continue;
            }

            if (fileHeader.IsDirectory)
            {
                continue;
            }

            firstFile = fileHeader;
            seeksAtYield = stream.SeekCount;
            break;
        }

        Assert.NotNull(firstFile);
        Assert.Equal(firstFile.DataStartPosition, stream.Position);
        Assert.Equal(seeksAtYield, stream.SeekCount);
        Assert.DoesNotContain(
            firstFile.DataStartPosition + firstFile.CompressedSize,
            stream.SeekTargets
        );
    }

    [Theory]
    [InlineData("Rar.encrypted_filesAndHeader.rar")]
    [InlineData("Rar5.encrypted_filesAndHeader.rar")]
    public async Task RarHeaderFactory_ReadHeadersAsync_EncryptedHeaders_ShouldMatchSync(
        string archiveName
    )
    {
        const string password = "test";
        var syncHeaders = ReadRarHeaders(archiveName, password);
        var asyncHeaders = await ReadRarHeadersAsync(archiveName, password);

        Assert.Equal(syncHeaders, asyncHeaders);
        Assert.Contains(asyncHeaders, header => header.HeaderType == HeaderType.File);
    }

    [Fact]
    public async Task OpenAsyncArchive_CallerProvidedStream_ShouldRemainOpenByDefault()
    {
        var archiveBytes = await File.ReadAllBytesAsync(
            Path.Join(TEST_ARCHIVES_PATH, "Zip.deflate.zip")
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
            using var entryStream = new MemoryStream(new byte[64 * 1024]);
            writer.Write("large.bin", entryStream);
        }
        return stream.ToArray();
    }

    private static RarHeaderFactory CreateRarHeaderFactory(string? password = null) =>
        new(
            StreamingMode.Seekable,
            ReaderOptions.ForExternalStream with
            {
                Password = password,
            }
        );

    private static List<HeaderFieldSnapshot> ReadRarHeaders(
        string archiveName,
        string? password = null
    )
    {
        using var stream = File.OpenRead(Path.Join(TEST_ARCHIVES_PATH, archiveName));
        var factory = CreateRarHeaderFactory(password);
        return factory.ReadHeaders(stream).Select(SnapshotHeader).ToList();
    }

    private static async Task<List<HeaderFieldSnapshot>> ReadRarHeadersAsync(
        string archiveName,
        string? password = null
    )
    {
        await using var stream = File.OpenRead(Path.Join(TEST_ARCHIVES_PATH, archiveName));
        var factory = CreateRarHeaderFactory(password);
        var headers = new List<HeaderFieldSnapshot>();
        await foreach (var header in factory.ReadHeadersAsync(stream))
        {
            headers.Add(SnapshotHeader(header));
        }

        return headers;
    }

    private static HeaderFieldSnapshot SnapshotHeader(IRarHeader header)
    {
        if (header is IRarFileHeader fileHeader)
        {
            return new HeaderFieldSnapshot(
                header.HeaderType,
                fileHeader.FileName,
                fileHeader.DataStartPosition,
                fileHeader.CompressedSize,
                fileHeader.AdditionalDataSize
            );
        }

        return new HeaderFieldSnapshot(header.HeaderType, null, null, null, null);
    }

    private sealed record EntrySnapshot(
        string Key,
        long Size,
        CompressionType CompressionType,
        string Content
    );

    private sealed record HeaderFieldSnapshot(
        HeaderType HeaderType,
        string? FileName,
        long? DataStartPosition,
        long? CompressedSize,
        long? AdditionalDataSize
    );

    /// <summary>
    /// Counts explicit <see cref="Stream.Seek"/> calls and <see cref="Stream.Position"/>
    /// assignments so stop-after-first-file can prove packed data was not skipped.
    /// </summary>
    private sealed class SeekCountingStream(Stream inner) : Stream
    {
        public int SeekCount { get; private set; }
        public List<long> SeekTargets { get; } = [];

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set
            {
                SeekCount++;
                SeekTargets.Add(value);
                inner.Position = value;
            }
        }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) =>
            inner.Read(buffer, offset, count);

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default
        ) => await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);

        public override long Seek(long offset, SeekOrigin origin)
        {
            SeekCount++;
            var result = inner.Seek(offset, origin);
            SeekTargets.Add(result);
            return result;
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync().ConfigureAwait(false);
            await base.DisposeAsync().ConfigureAwait(false);
        }
    }
}
