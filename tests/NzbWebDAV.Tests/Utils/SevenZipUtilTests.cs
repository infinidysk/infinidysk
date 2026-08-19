using System.Text;
using NzbWebDAV.Utils;
using SharpCompress.Common;

namespace NzbWebDAV.Tests.Utils;

public class SevenZipUtilTests
{
    private const string StoredArchiveBase64 =
        "N3q8ryccAARez7gsaAAAAAAAAAAUAAAAAAAAAJeoNbBzdG9yZWQtc2V2ZW56aXAtZW50cnkBAE4BBAYAAQkVAAcLAQABAQAMFQAICgHuw9/ZAAAFARkBABEXAHMAYQBtAHAAbABlAC4AdAB4AHQAAAAUCgEAwGcQ9pEQ3QEVBgEAIICAgQAAABcGFQEJUwAHCwEAASEhARgMTwAA";

    [Fact]
    public async Task GetSevenZipEntriesAsync_ReturnsStoredEntryByteRange()
    {
        var archiveBytes = Convert.FromBase64String(StoredArchiveBase64);
        var expectedEntryBytes = Encoding.UTF8.GetBytes("stored-sevenzip-entry");
        await using var archiveStream = new MemoryStream(archiveBytes);

        var entry = Assert.Single(
            await SevenZipUtil.GetSevenZipEntriesAsync(
                archiveStream, password: null, CancellationToken.None));

        Assert.Equal("sample.txt", entry.PathWithinArchive);
        Assert.Equal(CompressionType.None, entry.CompressionType);
        Assert.Equal(entry.FolderStartByteOffset, entry.ByteRangeWithinArchive.StartInclusive);

        var byteRange = entry.ByteRangeWithinArchive;
        var storedEntryBytes = archiveBytes
            .AsSpan(
                checked((int)byteRange.StartInclusive),
                checked((int)(byteRange.EndExclusive - byteRange.StartInclusive))
            )
            .ToArray();

        Assert.Equal(expectedEntryBytes, storedEntryBytes);
    }

    [Fact]
    public async Task GetSevenZipEntriesAsync_ThrowsInvalidFormatException_OnCorruptSignature()
    {
        // Import must fail as a managed InvalidFormatException so the queue marks
        // the item failed instead of crashing the process (audit F3 / #477).
        var archiveBytes = Convert.FromBase64String(StoredArchiveBase64);
        archiveBytes[0] ^= 0xFF;
        await using var archiveStream = new MemoryStream(archiveBytes);

        await Assert.ThrowsAsync<InvalidFormatException>(() =>
            SevenZipUtil.GetSevenZipEntriesAsync(
                archiveStream, password: null, CancellationToken.None));
    }

    [Fact]
    public async Task GetSevenZipEntriesAsync_PreCancelledTokenThrowsOperationCanceledException()
    {
        var archiveBytes = Convert.FromBase64String(StoredArchiveBase64);
        await using var archiveStream = new MemoryStream(archiveBytes);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            SevenZipUtil.GetSevenZipEntriesAsync(
                archiveStream, password: null, cts.Token));
    }

    [Fact]
    public async Task GetSevenZipEntriesAsync_CancellationMidLoadThrowsOperationCanceledException()
    {
        var archiveBytes = Convert.FromBase64String(StoredArchiveBase64);
        using var cts = new CancellationTokenSource();
        // 7z signature is 6 bytes; cancel after that so the failure is OCE, not
        // InvalidFormatException from a truncated signature scan.
        await using var archiveStream = new CancelAfterBytesReadStream(
            new MemoryStream(archiveBytes),
            cts,
            cancelAfterBytes: 16);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            SevenZipUtil.GetSevenZipEntriesAsync(
                archiveStream, password: null, cts.Token));
    }

    private sealed class CancelAfterBytesReadStream(
        Stream inner,
        CancellationTokenSource cancellationTokenSource,
        long cancelAfterBytes) : Stream
    {
        private long _bytesRead;

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException("Use async reads for this test stream.");

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = await inner.ReadAsync(buffer, cancellationToken);
            _bytesRead += read;
            if (_bytesRead > cancelAfterBytes)
            {
                await cancellationTokenSource.CancelAsync();
                cancellationToken.ThrowIfCancellationRequested();
            }

            return read;
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            inner.Seek(offset, origin);

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            await base.DisposeAsync();
        }
    }
}
